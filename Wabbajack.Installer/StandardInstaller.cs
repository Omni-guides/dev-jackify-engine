using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IniParser;
using IniParser.Model.Configuration;
using IniParser.Parser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wabbajack.Common;
using Wabbajack.Compression.BSA;
using Wabbajack.Compression.Zip;
using Wabbajack.Downloaders;
using Wabbajack.Downloaders.GameFile;
using Wabbajack.DTOs;
using Wabbajack.DTOs.BSA.FileStates;
using Wabbajack.DTOs.Directives;
using Wabbajack.DTOs.DownloadStates;
using Wabbajack.DTOs.Interventions;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Hashing.PHash;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Installer.Utilities;
using Wabbajack.Networking.Http.Interfaces;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;
using Wabbajack.VFS;

namespace Wabbajack.Installer;

public class StandardInstaller : AInstaller<StandardInstaller>
{

    public StandardInstaller(ILogger<StandardInstaller> logger,
        InstallerConfiguration config,
        IGameLocator gameLocator, FileExtractor.FileExtractor extractor,
        DTOSerializer jsonSerializer, Context vfs, FileHashCache fileHashCache,
        DownloadDispatcher downloadDispatcher, ParallelOptions parallelOptions, IResource<IInstaller> limiter, Client wjClient, IImageLoader imageLoader, IServiceProvider serviceProvider,
        ITransferMetrics transferMetrics) :
        base(logger, config, gameLocator, extractor, jsonSerializer, vfs, fileHashCache, downloadDispatcher,
            parallelOptions, limiter, wjClient, imageLoader, serviceProvider, transferMetrics)
    {
        MaxSteps = 15;
    }

    public static StandardInstaller Create(IServiceProvider provider, InstallerConfiguration configuration)
    {
        // Create a TexConvImageLoader with the installer's temp directory
        var installerTempManager = new TemporaryFileManager(configuration.Install.Combine("__temp__"));
        var texConvImageLoader = new TexConvImageLoader(installerTempManager, provider.GetRequiredService<ILogger<TexConvImageLoader>>());
        
        return new StandardInstaller(provider.GetRequiredService<ILogger<StandardInstaller>>(),
            configuration,
            provider.GetRequiredService<IGameLocator>(),
            provider.GetRequiredService<FileExtractor.FileExtractor>(),
            provider.GetRequiredService<DTOSerializer>(),
            provider.GetRequiredService<Context>(),
            provider.GetRequiredService<FileHashCache>(),
            provider.GetRequiredService<DownloadDispatcher>(),
            provider.GetRequiredService<ParallelOptions>(),
            provider.GetRequiredService<IResource<IInstaller>>(),
            provider.GetRequiredService<Client>(),
            texConvImageLoader,
            provider,
            provider.GetRequiredService<ITransferMetrics>());
    }

    public override async Task<InstallResult> Begin(CancellationToken token)
    {
        if (token.IsCancellationRequested) return InstallResult.Cancelled;
        
        // Start the installation stopwatch
        _installationStopWatch.Start();
        
        _logger.LogInformation("Installing: {Name} - {Version}", _configuration.ModList.Name, _configuration.ModList.Version);
        await _wjClient.SendMetric(MetricNames.BeginInstall, ModList.Name);
        NextStep(Consts.StepPreparing, "Configuring Installer", 0);
        _logger.LogInformation("Configuring Processor");

        if (_configuration.GameFolder == default)
            _configuration.GameFolder = _gameLocator.GameLocation(_configuration.Game);

        if (_configuration.GameFolder == default)
        {
            var otherGame = _configuration.Game.MetaData().CommonlyConfusedWith
                .Where(g => _gameLocator.IsInstalled(g)).Select(g => g.MetaData()).FirstOrDefault();
            if (otherGame != null)
            {
                _logger.LogError(
                    "In order to do a proper install Wabbajack needs to know where your {lookingFor} folder resides. However this game doesn't seem to be installed, we did however find an installed " +
                    "copy of {otherGame}, did you install the wrong game?",
                    _configuration.Game.MetaData().HumanFriendlyGameName, otherGame.HumanFriendlyGameName);
            }
            else
                _logger.LogError(
                    "In order to do a proper install Wabbajack needs to know where your {lookingFor} folder resides. However this game doesn't seem to be installed.",
                    _configuration.Game.MetaData().HumanFriendlyGameName);

            return InstallResult.GameMissing;
        }

        if (!_configuration.GameFolder.DirectoryExists())
        {
            _logger.LogError("Located game {game} at \"{gameFolder}\" but the folder does not exist!",
                _configuration.Game, _configuration.GameFolder);
            return InstallResult.GameInvalid;
        }


        _logger.LogInformation("Install Folder: {InstallFolder}", _configuration.Install);
        _logger.LogInformation("Downloads Folder: {DownloadFolder}", _configuration.Downloads);
        _logger.LogInformation("Game Folder: {GameFolder}", _configuration.GameFolder);
        _logger.LogInformation("Engine Folder: {WabbajackFolder}", KnownFolders.EntryPoint);

        _configuration.Install.CreateDirectory();
        _configuration.Downloads.CreateDirectory();

        await OptimizeModlist(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        await HashArchives(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        await DownloadArchives(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        await HashArchives(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        // Check for missing archives before proceeding to VFS operations
        var missing = ModList.Archives.Where(a => !HashedArchives.ContainsKey(a.Hash)).ToList();
        var nonManualMissing = missing.Where(a => a.State is not Manual && a.State is not GameFileSource).ToList();
        
        // Auto-cleanup files with hash mismatches and retry once
        if (nonManualMissing.Count > 0)
        {
            _logger.LogInformation("Detected {count} files with hash mismatches. Checking downloads...", nonManualMissing.Count);

            var cleanedFiles = new List<Archive>();
            foreach (var archive in nonManualMissing)
            {
                var expectedPath = _configuration.Downloads.Combine(archive.Name);
                if (expectedPath.FileExists())
                {
                    _logger.LogWarning("Hash mismatch for {name}, re-downloading...", archive.Name);
                    expectedPath.Delete();
                    cleanedFiles.Add(archive);
                }
            }

            if (cleanedFiles.Count > 0)
            {
                _logger.LogInformation("Re-downloading {count} files with hash mismatches...", cleanedFiles.Count);
                await DownloadMissingArchives(cleanedFiles, token);
                await HashArchives(token);
                
                // Recheck after cleanup and retry
                missing = ModList.Archives.Where(a => !HashedArchives.ContainsKey(a.Hash)).ToList();
                nonManualMissing = missing.Where(a => a.State is not Manual).ToList();
            }
        }
        
        if (nonManualMissing.Count > 0)
        {
            foreach (var a in nonManualMissing)
                _logger.LogCritical("Unable to download {name} ({primaryKeyString})", a.Name,
                    a.State.PrimaryKeyString);
            
            if (nonManualMissing.Count == 1)
            {
                _logger.LogCritical("Cannot continue, was unable to download 1 archive. This may be due to network issues, server problems, or corrupted existing files.");
            }
            else
            {
                _logger.LogCritical("Cannot continue, was unable to download {count} archives. This may be due to network issues, server problems, or corrupted existing files.", nonManualMissing.Count);
            }

            return InstallResult.DownloadFailed;
        }

        await ExtractModlist(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        await PrimeVFS();

        await BuildFolderStructure();

        await InstallArchives(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        await InstallIncludedFiles(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        await WriteMetaFiles(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        await BuildBSAs(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        // TODO: Port this
        await GenerateZEditMerges(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        await ForcePortable();
        await RemapMO2File();

        CreateOutputMods();

        SetScreenSizeInPrefs();

        await ExtractedModlistFolder!.DisposeAsync();
        
        // Cleanup Wine prefixes used for texture processing
        if (ImageLoader is IDisposable disposableImageLoader)
        {
            disposableImageLoader.Dispose();
            _logger.LogDebug("Cleaned up texture processing resources");
        }
        
        await _wjClient.SendMetric(MetricNames.FinishInstall, ModList.Name);

        NextStep(Consts.StepFinished, "Finished", 1);
        _logger.LogInformation("Finished Modlist Installation");
        return InstallResult.Succeeded;
    }


    private Task RemapMO2File()
    {
        var iniFile = _configuration.Install.Combine("ModOrganizer.ini");
        if (!iniFile.FileExists()) return Task.CompletedTask;

        _logger.LogInformation("Remapping ModOrganizer.ini");

        var iniData = iniFile.LoadIniFile();
        var settings = iniData["Settings"];
        settings["download_directory"] = _configuration.Downloads.ToString().Replace("\\", "/");
        iniData.SaveIniFile(iniFile);
        return Task.CompletedTask;
    }

    private void CreateOutputMods()
    {
        // Non MO2 Installs won't have this
        var profileDir = _configuration.Install.Combine("profiles");
        if (!profileDir.DirectoryExists()) return;

        profileDir
            .EnumerateFiles()
            .Where(f => f.FileName == Consts.SettingsIni)
            .Do(f =>
            {
                if (!f.FileExists())
                {
                    _logger.LogInformation("settings.ini is null for {profile}, skipping", f);
                    return;
                }

                var ini = f.LoadIniFile();

                var overwrites = ini["custom_overrides"];
                if (overwrites == null)
                {
                    _logger.LogInformation("No custom overwrites found, skipping");
                    return;
                }

                overwrites!.Do(keyData =>
                {
                    var v = keyData.Value;
                    var mod = _configuration.Install.Combine(Consts.MO2ModFolderName, (RelativePath) v);

                    mod.CreateDirectory();
                });
            });
    }

    private async Task ForcePortable()
    {
        var path = _configuration.Install.Combine("portable.txt");
        if (path.FileExists()) return;

        try
        {
            await path.WriteAllTextAsync("Created by Wabbajack");
        }
        catch (Exception e)
        {
            _logger.LogCritical(e, "Could not create portable.txt in {_configuration.Install}",
                _configuration.Install);
        }
    }

    private async Task WriteMetaFiles(CancellationToken token)
    {
        _logger.LogInformation("Looking for downloads by size");
        var bySize = UnoptimizedArchives.ToLookup(x => x.Size);

        var downloadFiles = _configuration.Downloads.EnumerateFiles()
            .Where(download => download.Extension != Ext.Meta)
            .ToArray();

        _logger.LogInformation("Writing Metas ({Count} files)", downloadFiles.Length);

        var totalFiles = downloadFiles.Length;
        var completedCount = 0;

        await downloadFiles.PDoAll(async download =>
            {
                // Update progress counter at start so early returns still count
                var current = Interlocked.Increment(ref completedCount);
                var percent = (double)current / totalFiles * 100.0;

                // Output progress (throttled to every 10 files)
                if (current % 10 == 0 || current == totalFiles)
                {
                    ConsoleOutput.PrintFileProgress("Checking", download.FileName.ToString(), percent, "", current, totalFiles);
                }

                var metaFile = download.WithExtension(Ext.Meta);

                // Fast path: If .meta already exists, skip entirely (don't hash)
                if (metaFile.FileExists())
                {
                    return;
                }

                var found = bySize[download.Size()];
                Hash hash = default;

                // Try to get hash from already-hashed archives first to avoid re-hashing
                var existingHash = HashedArchives.FirstOrDefault(kvp => kvp.Value.Equals(download));
                if (existingHash.Key != default)
                {
                    // File was already hashed during HashArchives - use cached hash
                    hash = existingHash.Key;
                }
                else
                {
                    // File not in HashedArchives, need to hash it
                    try
                    {
                        hash = await FileHashCache.FileHashCachedAsync(download, token);
                    }
                    catch(Exception ex)
                    {
                        _logger.LogError($"Failed to get hash for file {download}! Skipping meta file creation. Error: {ex.Message}");
                        return; // Skip this file and continue with others - meta files are not critical
                    }
                }

                var archive = found.FirstOrDefault(f => f.Hash == hash);

                IEnumerable<string> meta;

                if (archive == default)
                {
                    // archive is not part of the Modlist

                    if (metaFile.FileExists())
                    {
                        try
                        {
                            var parsed = metaFile.LoadIniFile();
                            if (parsed["General"] is not null && (
                                    parsed["General"]["removed"] is null ||
                                    parsed["General"]["removed"].Equals(bool.FalseString, StringComparison.OrdinalIgnoreCase)))
                            {
                                // add removed=true to files not part of the Modlist so they don't show up in MO2
                                parsed["General"]["removed"] = "true";

                                parsed.SaveIniFile(metaFile);
                            }
                        }
                        catch (Exception)
                        {
                            return;
                        }

                        return;
                    }

                    // create new meta file if missing
                    meta = new[]
                    {
                        "[General]",
                        "removed=true"
                    };
                }
                else
                {
                    if (metaFile.FileExists())
                    {
                        try
                        {
                            var parsed = metaFile.LoadIniFile();
                            if (parsed["General"] is not null && parsed["General"]["unknownArchive"] is null)
                            {
                                // meta doesn't have an associated archive
                                return;
                            }
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }

                    meta = AddInstalled(_downloadDispatcher.MetaIni(archive));
                }

                try
                {
                    await metaFile.WriteAllLinesAsync(meta, token);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to write meta file {metaFile}! Skipping. Error: {ex.Message}");
                    // Meta files are non-critical post-install metadata - continue installation
                }
            });

        _logger.LogInformation("Completed writing {count}/{total} meta files", completedCount, totalFiles);
    }

    private static IEnumerable<string> AddInstalled(IEnumerable<string> getMetaIni)
    {
        yield return "[General]";
        yield return "installed=true";

        foreach (var f in getMetaIni)
        {
            yield return f;
        }
    }

    /// <summary>
    /// Normalize path to use existing case on case-sensitive filesystems.
    /// Reuses the working implementation from AInstaller.
    /// </summary>
    private AbsolutePath? NormalizePathToCaseInsensitive(AbsolutePath path)
    {
        try
        {
            var parts = path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return path;

            var currentPath = "/".ToAbsolutePath();

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var nextPath = currentPath.Combine(part);

                if (nextPath.DirectoryExists() || nextPath.FileExists())
                {
                    currentPath = nextPath;
                }
                else if (currentPath.DirectoryExists())
                {
                    // Try case-insensitive match
                    var entries = currentPath.EnumerateDirectories(recursive: false)
                        .Concat(currentPath.EnumerateFiles("*", recursive: false))
                        .ToList();

                    var caseInsensitiveMatch = entries.FirstOrDefault(e =>
                        e.FileName.ToString().Equals(part, StringComparison.OrdinalIgnoreCase));

                    if (caseInsensitiveMatch != default(AbsolutePath))
                    {
                        currentPath = caseInsensitiveMatch;
                    }
                    else
                    {
                        currentPath = nextPath;
                    }
                }
                else
                {
                    currentPath = nextPath;
                }
            }

            return currentPath;
        }
        catch
        {
            return null;
        }
    }

    private async Task BuildBSAs(CancellationToken token)
    {
        var bsas = ModList.Directives.OfType<CreateBSA>().ToList();
        _logger.LogInformation("Generating debug caches");
        var indexedByDestination = UnoptimizedDirectives.ToDictionary(d => d.To);
        _logger.LogInformation("Building {bsasCount} bsa files", bsas.Count);
        NextStep("Installing", "Building BSAs", bsas.Count);

        using var fileProgressTracker = new FileProgressTracker();
        var lastFileProgressOutput = new ConcurrentDictionary<string, DateTime>();
        var totalBSAs = bsas.Count;
        long currentBSAIndex = 0;
        var displayCts = new CancellationTokenSource();
        var displayTask = Task.Run(async () =>
        {
            while (!displayCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, displayCts.Token);

                    var activeFiles = fileProgressTracker.GetActiveFiles();
                    var now = DateTime.UtcNow;
                    
                    // Read current BSA index (may be slightly stale, but fine for progress display)
                    var currentBSA = (int)Interlocked.Read(ref currentBSAIndex);

                    foreach (var (filename, progress) in activeFiles)
                    {
                        var shouldOutput = progress.IsCompleted ||
                                           !lastFileProgressOutput.TryGetValue(filename, out var lastOutput) ||
                                           (now - lastOutput).TotalMilliseconds >= 200;

                        if (shouldOutput)
                        {
                            var percent = progress.GetPercent();
                            var speed = progress.GetSpeedString();
                            var operation = progress.IsCompleted ? "Completed" : progress.Operation;
                            
                            // Include counter for BSA building operations (show current BSA / total BSAs)
                            var counterCompleted = progress.Operation == "Building" ? currentBSA : (int?)null;
                            var counterTotal = progress.Operation == "Building" ? totalBSAs : (int?)null;

                            ConsoleOutput.PrintFileProgress(operation, filename, percent, speed, counterCompleted, counterTotal);
                            lastFileProgressOutput[filename] = now;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, displayCts.Token);

        foreach (var bsa in bsas)
        {
            Interlocked.Increment(ref currentBSAIndex);
            UpdateProgress(1);
            ConsoleOutput.PrintProgressWithDuration($"Building {bsa.To.FileName}");

            var sourceDir = _configuration.Install.Combine(Consts.BSACreationDir, bsa.TempID);

            var fileCount = Math.Max(1, bsa.FileStates.Count());
            var totalSteps = Math.Max(1, fileCount * 2);
            long processedSteps = 0;
            var bsaName = bsa.To.FileName.ToString();
            fileProgressTracker.UpdateProgress(bsaName, "Building", 0, totalSteps, DateTime.UtcNow);

            await using var a = BSADispatch.CreateBuilder(bsa.State, _manager);

            var streams = await bsa.FileStates.PMapAllBatchedAsync(_limiter, async state =>
            {
                // Build the expected file path
                var filePath = sourceDir.Combine(state.Path);

                // If file doesn't exist with exact case, normalize using the WORKING approach from AInstaller
                if (!filePath.FileExists())
                {
                    // Use the same case normalization that works for file installation
                    var normalizedPath = NormalizePathToCaseInsensitive(filePath);
                    if (normalizedPath.HasValue && normalizedPath.Value.FileExists())
                    {
                        filePath = normalizedPath.Value;
                    }
                    else
                    {
                        throw new FileNotFoundException($"Could not find BSA source file: {state.Path} (searched: {filePath})", filePath.ToString());
                    }
                }

                var fs = filePath.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                await a.AddFile(state, fs, token);
                var current = Interlocked.Increment(ref processedSteps);
                fileProgressTracker.UpdateProgress(bsaName, "Building", current, totalSteps, DateTime.UtcNow);
                return fs;
            }).ToList();

            ConsoleOutput.PrintProgressWithDuration($"Writing {bsa.To.FileName}");
            var outPath = _configuration.Install.Combine(bsa.To);

            await using (var outStream = outPath.Open(FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await a.Build(outStream, token);
            }

            streams.Do(s => s.Dispose());

            await FileHashCache.FileHashWriteCache(outPath, bsa.Hash);

            ConsoleOutput.PrintProgressWithDuration($"Verifying {bsa.To.FileName}");
            var reader = await BSADispatch.Open(outPath);
            var results = await reader.Files.PMapAllBatchedAsync(_limiter, async state =>
            {
                var sf = await state.GetStreamFactory(token);
                await using var stream = await sf.GetStream();
                var hash = await stream.Hash(token);

                var astate = bsa.FileStates.FirstOrDefault(f => f.Path == state.Path);
                if (astate == null)
                    throw new InvalidOperationException(
                        $"BSA verification failed: '{state.Path}' exists in the built '{bsa.To.FileName}' but has no matching FileState in the modlist directive. " +
                        $"This may indicate a corrupt modlist or an incompatible archive version.");
                var srcDirective = indexedByDestination[Consts.BSACreationDir.Combine(bsa.TempID, astate.Path)];
                //DX10Files are lossy
                if (astate is not BA2DX10File && srcDirective.IsDeterministic)
                    ThrowOnNonMatchingHash(bsa, srcDirective, astate, hash);

                var current = Interlocked.Increment(ref processedSteps);
                fileProgressTracker.UpdateProgress(bsaName, "Building", current, totalSteps, DateTime.UtcNow);

                return (srcDirective, hash);
            }).ToHashSet();

            fileProgressTracker.UpdateProgress(bsaName, "Building", totalSteps, totalSteps, DateTime.UtcNow);
            fileProgressTracker.MarkCompleted(bsaName);
            var completedInfo = fileProgressTracker.GetFileInfo(bsaName);
            if (completedInfo != null && completedInfo.IsCompleted)
            {
                var percent = completedInfo.GetPercent();
                var speed = completedInfo.GetSpeedString();
                // Include counter showing completed/total BSAs
                var currentBSA = (int)Interlocked.Read(ref currentBSAIndex);
                ConsoleOutput.PrintFileProgress("Completed", bsaName, percent, speed, currentBSA, totalBSAs);
            }
        }
        
        // Clear the progress line after BSA building is complete
        ConsoleOutput.ClearProgressLine();

        NextStep(Consts.StepFinished, "Removing Temporary Files", 2);
        
        var bsaDir = _configuration.Install.Combine(Consts.BSACreationDir);
        if (bsaDir.DirectoryExists())
        {
            _logger.LogInformation("Removing temp folder {bsaCreationDir}", Consts.BSACreationDir);
            try { bsaDir.DeleteDirectory(); } catch (IOException ex) { _logger.LogWarning("Could not remove temp folder {bsaCreationDir}: {msg}", Consts.BSACreationDir, ex.Message); }
            UpdateProgress(1);
        }

        // Clean up the main temporary directory
        var tempDir = _configuration.Install.Combine("__temp__");
        if (tempDir.DirectoryExists())
        {
            _logger.LogInformation("Removing temp folder __temp__");
            try { tempDir.DeleteDirectory(); } catch (IOException ex) { _logger.LogWarning("Could not remove temp folder __temp__: {msg}", ex.Message); }
            UpdateProgress(1);
        }

        displayCts.Cancel();
        try { await displayTask; }
        catch (OperationCanceledException) { }

        // Clean up any stale Proton prefixes
        try
        {
            var jackifyDir = JackifyConfig.GetDataDirectory();
            if (jackifyDir.DirectoryExists())
            {
                var prefixCount = 0;
                foreach (var dir in Directory.EnumerateDirectories(jackifyDir.ToString()))
                {
                    var name = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name) && name.StartsWith(".prefix-", StringComparison.Ordinal))
                    {
                        try
                        {
                            // Safety: ensure it's under Jackify dir
                            if (!dir.StartsWith(jackifyDir.ToString(), StringComparison.Ordinal))
                                continue;

                            // Unlink dosdevices symlinks before deletion
                            var dosDevices = Path.Combine(dir, "pfx", "dosdevices");
                            if (Directory.Exists(dosDevices))
                            {
                                foreach (var entry in Directory.EnumerateFileSystemEntries(dosDevices))
                                {
                                    try
                                    {
                                        var fi = new FileInfo(entry);
                                        if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                            File.Delete(entry);
                                    }
                                    catch { /* ignore */ }
                                }
                            }

                            dir.ToAbsolutePath().DeleteDirectory();
                            prefixCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to remove stale prefix: {Prefix}", dir);
                        }
                    }
                }
                if (prefixCount > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} stale Proton prefixes", prefixCount);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean up stale Proton prefixes");
        }
    }

    private async Task InstallIncludedFiles(CancellationToken token)
    {
        _logger.LogInformation("Writing inline files");
        NextStep(Consts.StepInstalling, "Installing Included Files", ModList.Directives.OfType<InlineFile>().Count());
        await ModList.Directives
            .OfType<InlineFile>()
            .PDoAll(_limiter, async directive =>
            {
                UpdateProgress(1);
                var outPath = _configuration.Install.Combine(directive.To);
                // Ensure parent directory exists before writing - CreateDirectory() may fail silently
                if (!outPath.Parent.DirectoryExists())
                {
                    outPath.Parent.CreateDirectory();
                    // Verify it was actually created
                    if (!outPath.Parent.DirectoryExists())
                    {
                        throw new DirectoryNotFoundException($"Failed to create directory: {outPath.Parent}");
                    }
                }
                outPath.Delete();

                switch (directive)
                {
                    case RemappedInlineFile file:
                        await WriteRemappedFile(file);
                        await FileHashCache.FileHashCachedAsync(outPath, token);
                        break;
                    default:
                        var hash = await outPath.WriteAllHashedAsync(await LoadBytesFromPath(directive.SourceDataID), token);
                        if (!Consts.KnownModifiedFiles.Contains(directive.To.FileName))
                            ThrowOnNonMatchingHash(directive, hash);

                        await FileHashCache.FileHashWriteCache(outPath, directive.Hash);
                        break;
                }
            });
    }

    private void SetScreenSizeInPrefs()
    {
        var profilesPath = _configuration.Install.Combine("profiles");

        // Don't remap files for Native Game Compiler games
        if (!profilesPath.DirectoryExists()) return;
        if (_configuration.SystemParameters == null)
            _logger.LogDebug("No SystemParameters set, ignoring ini settings for system parameters");

        var config = new IniParserConfiguration {AllowDuplicateKeys = true, AllowDuplicateSections = true};
        config.CommentRegex = new Regex(@"^(#|;)(.*)");
        var oblivionPath = (RelativePath) "Oblivion.ini";

        if (profilesPath.DirectoryExists())
        {
            foreach (var file in profilesPath.EnumerateFiles()
                         .Where(f => ((string) f.FileName).EndsWith("refs.ini") || f.FileName == oblivionPath))
                try
                {
                    var parser = new FileIniDataParser(new IniDataParser(config));
                    var data = parser.ReadFile(file.ToString());
                    var modified = false;
                    if (data.Sections["Display"] != null)
                        if (data.Sections["Display"]["iSize W"] != null && data.Sections["Display"]["iSize H"] != null)
                        {
                            data.Sections["Display"]["iSize W"] =
                                _configuration.SystemParameters!.ScreenWidth.ToString(CultureInfo.CurrentCulture);
                            data.Sections["Display"]["iSize H"] =
                                _configuration.SystemParameters.ScreenHeight.ToString(CultureInfo.CurrentCulture);
                            modified = true;
                        }

                    if (data.Sections["MEMORY"] != null)
                        if (data.Sections["MEMORY"]["VideoMemorySizeMb"] != null)
                        {
                            data.Sections["MEMORY"]["VideoMemorySizeMb"] =
                                _configuration.SystemParameters!.EnbLEVRAMSize.ToString(CultureInfo.CurrentCulture);
                            modified = true;
                        }

                    if (!modified) continue;
                    parser.WriteFile(file.ToString(), data);
                    _logger.LogTrace("Remapped screen size in {file}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping screen size remap for {file} due to parse error.", file);
                }
        }

        var tweaksPath = (RelativePath) "SSEDisplayTweaks.ini";
        foreach (var file in _configuration.Install.EnumerateFiles()
            .Where(f => f.FileName == tweaksPath))
            try
            {
                var parser = new FileIniDataParser(new IniDataParser(config));
                var data = parser.ReadFile(file.ToString());
                var modified = false;
                if (data.Sections["Render"] != null)
                    if (data.Sections["Render"]["Resolution"] != null)
                    {
                        data.Sections["Render"]["Resolution"] =
                            $"{_configuration.SystemParameters!.ScreenWidth.ToString(CultureInfo.CurrentCulture)}x{_configuration.SystemParameters.ScreenHeight.ToString(CultureInfo.CurrentCulture)}";
                        modified = true;
                    }

                if (modified)
                    parser.WriteFile(file.ToString(), data);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping screen size remap for {file} due to parse error.", file);
            }

        // The Witcher 3
        if (_configuration.Game == Game.Witcher3)
        {
            var name = (RelativePath)"user.settings";
            foreach (var file in _configuration.Install.Combine("profiles").EnumerateFiles()
                         .Where(f => f.FileName == name))
            {
                try
                {
                    var parser = new FileIniDataParser(new IniDataParser(config));
                    var data = parser.ReadFile(file.ToString());
                    data["Viewport"]["Resolution"] =
                        $"{_configuration.SystemParameters!.ScreenWidth}x{_configuration.SystemParameters!.ScreenHeight}";
                    parser.WriteFile(file.ToString(), data);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "While remapping user.settings");
                }
            }
        }
    }

    private async Task WriteRemappedFile(RemappedInlineFile directive)
    {
        var data = Encoding.UTF8.GetString(await LoadBytesFromPath(directive.SourceDataID));

        // For ModOrganizer/INI config files we want clean, unquoted Wine-style paths.
        // Texconv/texdiag still use ProtonDetector.ConvertToWinePath (with quoting) via TexConvImageLoader.
        static string ToWinePathNoQuotes(AbsolutePath path)
        {
            // Map Linux path (/home/...) to Wine Z: drive (Z:\home\...)
            // NOTE: Intentionally do NOT add surrounding quotes here; MO2 handles its own quoting/escaping.
            return $"Z:{path.ToString().Replace('/', '\\')}";
        }

        var gameFolder = ToWinePathNoQuotes(_configuration.GameFolder);
        var installPath = ToWinePathNoQuotes(_configuration.Install);
        var downloadsPath = ToWinePathNoQuotes(_configuration.Downloads);

        data = data.Replace(Consts.GAME_PATH_MAGIC_BACK, gameFolder);
        data = data.Replace(Consts.GAME_PATH_MAGIC_DOUBLE_BACK, gameFolder.Replace("\\", "\\\\"));
        data = data.Replace(Consts.GAME_PATH_MAGIC_FORWARD, gameFolder.Replace("\\", "/"));

        data = data.Replace(Consts.MO2_PATH_MAGIC_BACK, installPath);
        data = data.Replace(Consts.MO2_PATH_MAGIC_DOUBLE_BACK, installPath.Replace("\\", "\\\\"));
        data = data.Replace(Consts.MO2_PATH_MAGIC_FORWARD, installPath.Replace("\\", "/"));

        data = data.Replace(Consts.DOWNLOAD_PATH_MAGIC_BACK, downloadsPath);
        data = data.Replace(Consts.DOWNLOAD_PATH_MAGIC_DOUBLE_BACK, downloadsPath.Replace("\\", "\\\\"));
        data = data.Replace(Consts.DOWNLOAD_PATH_MAGIC_FORWARD, downloadsPath.Replace("\\", "/"));

        await _configuration.Install.Combine(directive.To).WriteAllTextAsync(data);
    }

    public async Task GenerateZEditMerges(CancellationToken token)
    {
        var patches = _configuration.ModList
            .Directives
            .OfType<MergedPatch>()
            .ToList();
        NextStep("Installing", "Generating ZEdit Merges", patches.Count);

        await patches.PMapAllBatchedAsync(_limiter, async m =>
        {
            UpdateProgress(1);
            _logger.LogInformation("Generating zEdit merge: {to}", m.To);

            var srcData = (await m.Sources.SelectAsync(async s =>
                        await _configuration.Install.Combine(s.RelativePath).ReadAllBytesAsync(token))
                    .ToReadOnlyCollection())
                .ConcatArrays();

            var patchData = await LoadBytesFromPath(m.PatchID);

            await using var fs = _configuration.Install.Combine(m.To)
                .Open(FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var hash = await BinaryPatching.ApplyPatch(new MemoryStream(srcData), new MemoryStream(patchData), fs);
                ThrowOnNonMatchingHash(m, hash);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "While creating zEdit merge, entering debugging mode");
                foreach (var source in m.Sources)
                {
                    var hash = await _configuration.Install.Combine(source.RelativePath).Hash();
                    _logger.LogInformation("For {Source} expected hash {Expected} got {Got}", source.RelativePath, source.Hash, hash);
                }

                throw;
            }

            return m;
        }).ToList();
    }

    public static async Task<ModList> Load(DTOSerializer dtos, DownloadDispatcher dispatcher, ModlistMetadata metadata, CancellationToken token)
    {
        var archive = new Archive
        {
            State = dispatcher.Parse(new Uri(metadata.Links.Download))!,
            Size = metadata.DownloadMetadata!.Size,
            Hash = metadata.DownloadMetadata.Hash
        };

        await using var stream = await dispatcher.ChunkedSeekableStream(archive, token);
        await using var reader = new ZipReader(stream);
        var entry = (await reader.GetFiles()).First(e => e.FileName == "modlist");
        using var ms = new MemoryStream();
        await reader.Extract(entry, ms, token);
        ms.Position = 0;
        return JsonSerializer.Deserialize<ModList>(ms, dtos.Options)!;
    }
}
