using System;
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
        DownloadDispatcher downloadDispatcher, ParallelOptions parallelOptions, IResource<IInstaller> limiter, Client wjClient, IImageLoader imageLoader, IServiceProvider serviceProvider) :
        base(logger, config, gameLocator, extractor, jsonSerializer, vfs, fileHashCache, downloadDispatcher,
            parallelOptions, limiter, wjClient, imageLoader, serviceProvider)
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
            provider);
    }

    public override async Task<InstallResult> Begin(CancellationToken token)
    {
        if (token.IsCancellationRequested) return InstallResult.Cancelled;
        
        // Start the installation stopwatch
        _installationStopWatch.Start();
        
        _logger.LogInformation("{Duration} Installing: {Name} - {Version}", ConsoleOutput.GetDurationTimestamp(), _configuration.ModList.Name, _configuration.ModList.Version);
        await _wjClient.SendMetric(MetricNames.BeginInstall, ModList.Name);
        NextStep(Consts.StepPreparing, "Configuring Installer", 0);
        _logger.LogInformation("{Duration} Configuring Processor", ConsoleOutput.GetDurationTimestamp());

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


        _logger.LogInformation("{Duration} Install Folder: {InstallFolder}", ConsoleOutput.GetDurationTimestamp(), _configuration.Install);
        _logger.LogInformation("{Duration} Downloads Folder: {DownloadFolder}", ConsoleOutput.GetDurationTimestamp(), _configuration.Downloads);
        _logger.LogInformation("{Duration} Game Folder: {GameFolder}", ConsoleOutput.GetDurationTimestamp(), _configuration.GameFolder);
        _logger.LogInformation("{Duration} Engine Folder: {WabbajackFolder}", ConsoleOutput.GetDurationTimestamp(), KnownFolders.EntryPoint);

        _configuration.Install.CreateDirectory();
        _configuration.Downloads.CreateDirectory();

        await OptimizeModlist(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        await HashArchives(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        await DownloadArchives(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;
        
        // Check if any manual downloads were encountered and stop installation if so
        var interventionHandler = _serviceProvider.GetService<IUserInterventionHandler>();
        if (interventionHandler != null && interventionHandler.HasManualDownloads())
        {
            _logger.LogInformation("Manual downloads detected. Stopping installation.");
            return InstallResult.DownloadFailed;
        }

        await HashArchives(token);
        if (token.IsCancellationRequested) return InstallResult.Cancelled;

        // Check for missing archives before proceeding to VFS operations
        var missing = ModList.Archives.Where(a => !HashedArchives.ContainsKey(a.Hash)).ToList();
        var nonManualMissing = missing.Where(a => a.State is not Manual && a.State is not GameFileSource).ToList();
        
        // Auto-cleanup corrupted files and retry once
        if (nonManualMissing.Count > 0)
        {
            _logger.LogInformation("Detected {count} files with hash mismatches. Checking for corrupted downloads...", nonManualMissing.Count);
            
            var cleanedFiles = new List<Archive>();
            foreach (var archive in nonManualMissing)
            {
                var expectedPath = _configuration.Downloads.Combine(archive.Name);
                if (expectedPath.FileExists())
                {
                    _logger.LogWarning("Corrupted file detected: {name}. Deleting and re-downloading...", archive.Name);
                    expectedPath.Delete();
                    cleanedFiles.Add(archive);
                }
            }
            
            if (cleanedFiles.Count > 0)
            {
                _logger.LogInformation("Attempting to re-download {count} corrupted files...", cleanedFiles.Count);
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
        Console.WriteLine(); // Add newline after progress line
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
        _logger.LogInformation("{Duration} Looking for downloads by size", ConsoleOutput.GetDurationTimestamp());
        var bySize = UnoptimizedArchives.ToLookup(x => x.Size);

        var downloadFiles = _configuration.Downloads.EnumerateFiles()
            .Where(download => download.Extension != Ext.Meta)
            .ToArray();
        
        _logger.LogInformation("{Duration} Writing Metas ({Count} files)", ConsoleOutput.GetDurationTimestamp(), downloadFiles.Length);
        
        var completedCount = 0;
        await downloadFiles.PDoAll(async download =>
            {
                var metaFile = download.WithExtension(Ext.Meta);

                var found = bySize[download.Size()];
                Hash hash = default;
                try
                {
                    hash = await FileHashCache.FileHashCachedAsync(download, token);
                }
                catch(Exception ex)
                {
                    _logger.LogError($"Failed to get hash for file {download}!");
                    throw;
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

                await metaFile.WriteAllLinesAsync(meta, token);
                Interlocked.Increment(ref completedCount);
            });
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

    private async Task BuildBSAs(CancellationToken token)
    {
        // Add blank line before Building BSAs section
        Console.WriteLine();
        
        var bsas = ModList.Directives.OfType<CreateBSA>().ToList();
        _logger.LogInformation("{Duration} Generating debug caches", ConsoleOutput.GetDurationTimestamp());
        var indexedByDestination = UnoptimizedDirectives.ToDictionary(d => d.To);
        _logger.LogInformation("{Duration} Building {bsasCount} bsa files", ConsoleOutput.GetDurationTimestamp(), bsas.Count);
        NextStep("Installing", "Building BSAs", bsas.Count);

        int currentBsaIndex = 0;
        foreach (var bsa in bsas)
        {
            currentBsaIndex++;
            UpdateProgress(1);
            int totalFiles = bsa.FileStates.Length;
            
            // Use single-line progress with BSA counter and file count
            ConsoleOutput.PrintProgressWithDuration($"Building BSA {currentBsaIndex}/{bsas.Count}: {bsa.To.FileName} ({totalFiles} files)");
            
            var sourceDir = _configuration.Install.Combine(Consts.BSACreationDir, bsa.TempID);

            await using var a = BSADispatch.CreateBuilder(bsa.State, _manager);
            var streams = await bsa.FileStates.PMapAllBatchedAsync(_limiter, async state =>
            {
                // Try the normal path first
                var filePath = sourceDir.Combine(state.Path);
                if (!filePath.FileExists())
                {
                    // Fallback: try with forward slashes converted to backslashes (Linux path issue)
                    // Files may have been extracted with backslashes in their names
                    var backslashPath = state.Path.ToString().Replace("/", "\\");
                    filePath = sourceDir.Combine(backslashPath.ToRelativePath());
                }
                
                var fs = filePath.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                await a.AddFile(state, fs, token);
                return fs;
            }).ToList();

            // Update progress for writing phase with BSA counter
            ConsoleOutput.PrintProgressWithDuration($"Writing BSA {currentBsaIndex}/{bsas.Count}: {bsa.To.FileName}");
            var outPath = _configuration.Install.Combine(bsa.To);

            await using (var outStream = outPath.Open(FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await a.Build(outStream, token);
            }

            streams.Do(s => s.Dispose());

            await FileHashCache.FileHashWriteCache(outPath, bsa.Hash);

            // Update progress for verification phase with BSA counter
            ConsoleOutput.PrintProgressWithDuration($"Verifying BSA {currentBsaIndex}/{bsas.Count}: {bsa.To.FileName}");
            var reader = await BSADispatch.Open(outPath);
            var results = await reader.Files.PMapAllBatchedAsync(_limiter, async state =>
            {
                var sf = await state.GetStreamFactory(token);
                await using var stream = await sf.GetStream();
                var hash = await stream.Hash(token);

                var astate = bsa.FileStates.First(f => f.Path == state.Path);
                var srcDirective = indexedByDestination[Consts.BSACreationDir.Combine(bsa.TempID, astate.Path)];
                //DX10Files are lossy
                if (astate is not BA2DX10File && srcDirective.IsDeterministic)
                    ThrowOnNonMatchingHash(bsa, srcDirective, astate, hash);
                return (srcDirective, hash);
            }).ToHashSet();
        }
        
        // Clear the progress line after BSA building is complete
        ConsoleOutput.ClearProgressLine();

        // Add newline before cleanup section for proper separation
        Console.WriteLine();
        NextStep(Consts.StepFinished, "Removing Temporary Files", 2);
        
        var bsaDir = _configuration.Install.Combine(Consts.BSACreationDir);
        if (bsaDir.DirectoryExists())
        {
            _logger.LogInformation("Removing temp folder {bsaCreationDir}", Consts.BSACreationDir);
            bsaDir.DeleteDirectory();
            UpdateProgress(1);
        }

        // Clean up the main temporary directory
        var tempDir = _configuration.Install.Combine("__temp__");
        if (tempDir.DirectoryExists())
        {
            _logger.LogInformation("Removing temp folder __temp__");
            tempDir.DeleteDirectory();
            UpdateProgress(1);
        }
    }

    private async Task InstallIncludedFiles(CancellationToken token)
    {
        // Add newline to ensure this appears on its own line after progress
        Console.WriteLine();
        _logger.LogInformation("{Duration} Writing inline files", ConsoleOutput.GetDurationTimestamp());
        // Add newline before section header for proper separation
        Console.WriteLine();
        NextStep(Consts.StepInstalling, "Installing Included Files", ModList.Directives.OfType<InlineFile>().Count());
        await ModList.Directives
            .OfType<InlineFile>()
            .PDoAll(_limiter, async directive =>
            {
                UpdateProgress(1);
                var outPath = _configuration.Install.Combine(directive.To);
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

        // Convert Linux paths to Proton-compatible Windows paths
        var gameFolder = ProtonDetector.ConvertToWinePath(_configuration.GameFolder);
        var installPath = ProtonDetector.ConvertToWinePath(_configuration.Install);
        var downloadsPath = ProtonDetector.ConvertToWinePath(_configuration.Downloads);

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
