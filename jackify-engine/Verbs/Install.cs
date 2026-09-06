using System;
using System.IO;
using System.Collections.Concurrent;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using K4os.Compression.LZ4;
using Wabbajack.CLI.Builder;
using Wabbajack.Common;
using Wabbajack.Downloaders;
using Wabbajack.Downloaders.GameFile;
using Wabbajack.DTOs;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Installer;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.Services.OSIntegrated;
using Wabbajack.VFS;
using Wabbajack.Networking.Http.Interfaces;

namespace Wabbajack.CLI.Verbs;

public class Install
{
    private readonly ILogger<Install> _logger;
    private readonly Client _wjClient;
    private readonly DownloadDispatcher _dispatcher;
    private readonly IServiceProvider _serviceProvider;
    private readonly DTOSerializer _dtos;
    private readonly FileHashCache _cache;
    private readonly GameLocator _gameLocator;

    public Install(ILogger<Install> logger, Client wjClient, DownloadDispatcher dispatcher, DTOSerializer dtos, 
        FileHashCache cache, GameLocator gameLocator, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _wjClient = wjClient;
        _dispatcher = dispatcher;
        _dtos = dtos;
        _serviceProvider = serviceProvider;
        _cache = cache;
        _gameLocator = gameLocator;
    }

    public static VerbDefinition Definition = new VerbDefinition("install", "Installs a wabbajack file", new[]
    {
        new OptionDefinition(typeof(AbsolutePath), "w", "wabbajack", "Wabbajack file"),
        new OptionDefinition(typeof(string), "m", "machineUrl", "Machine url to download"),
        new OptionDefinition(typeof(AbsolutePath), "o", "output", "Output path"),
        new OptionDefinition(typeof(AbsolutePath), "d", "downloads", "Downloads path"),
        new OptionDefinition(typeof(AbsolutePath), "g", "game", "Game installation directory (overrides auto-detection)", IsHidden: true),
        new OptionDefinition(typeof(string), "", "sse-bsa-compression", "SSE archive compression: maximum (default), balanced, or fast. Faster modes produce larger, byte-different archives; subsequent installs may rebuild them."),
    });

    internal async Task<int> Run(AbsolutePath wabbajack, AbsolutePath output, AbsolutePath downloads, string machineUrl, AbsolutePath game, string sseBsaCompression, CancellationToken token)
    {
        var compressionLevel = sseBsaCompression?.ToLowerInvariant() switch
        {
            null or "maximum" => LZ4Level.L12_MAX,
            "balanced" => LZ4Level.L06_HC,
            "fast" => LZ4Level.L00_FAST,
            _ => (LZ4Level?)null
        };
        if (compressionLevel == null)
        {
            StructuredError.WriteError(StructuredError.ErrorType.ValidationFailed,
                "--sse-bsa-compression must be maximum, balanced, or fast.");
            return StructuredError.ExitCodeFor(StructuredError.ErrorType.ValidationFailed);
        }
        if (compressionLevel != LZ4Level.L12_MAX)
            _logger.LogWarning("Using {Level} for SSE BSA compression. Archive bytes and sizes differ from the modlist; subsequent installs may rebuild these archives. Extracted file verification remains enabled.", compressionLevel);

        if (!string.IsNullOrEmpty(machineUrl))
        {
            if (!await DownloadMachineUrl(machineUrl, wabbajack, token))
                return 1;
            
            // Update wabbajack path to the downloaded file if it was empty
            if (wabbajack == AbsolutePath.Empty)
            {
                var fileName = machineUrl.Replace("/", "_@@_") + ".wabbajack";
                var downloadDir = JackifyConfig.GetDataDirectory().Combine("downloaded_mod_lists");
                downloadDir.CreateDirectory();
                wabbajack = downloadDir.Combine(fileName);
            }
        }

        // Print version header (no timestamps - these are informational messages before installation)
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        _logger.LogInformation("jackify-engine v{Version}: Minimal Linux-native modlist installer for Jackify", version);
        _logger.LogInformation("---------------------------------------------------------------");

        // Load modlist with error handling for incomplete files
        ModList modlist;
        try
        {
            modlist = await StandardInstaller.LoadFromFile(_dtos, wabbajack);
        }
        catch (System.IO.InvalidDataException ex) when (ex.Message.Contains("End of Central Directory") || ex.Message.Contains("could not be found"))
        {
            _logger.LogError("The .wabbajack file is incomplete or corrupted. This usually means the download was interrupted.");
            _logger.LogError("Please delete the file and try again: {Path}", wabbajack);
            _logger.LogError("Error: {Error}", ex.Message);
            StructuredError.WriteError(StructuredError.ErrorType.ArchiveCorrupt,
                "The .wabbajack file is incomplete or corrupted. This usually means the download was interrupted.",
                new Dictionary<string, object?> { ["filename"] = wabbajack.FileName.ToString() });
            return StructuredError.ExitCodeFor(StructuredError.ErrorType.ArchiveCorrupt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load .wabbajack file: {Path}", wabbajack);
            var t = ex is System.IO.FileNotFoundException
                ? StructuredError.ErrorType.FileNotFound
                : StructuredError.ErrorType.ArchiveCorrupt;
            StructuredError.WriteError(t, $"Failed to load .wabbajack file: {ex.Message}",
                new Dictionary<string, object?> { ["path"] = wabbajack.ToString() });
            return StructuredError.ExitCodeFor(t);
        }

        // --- Pre-flight validation ---

        AbsolutePath gameFolder;
        if (game != AbsolutePath.Empty)
        {
            if (!game.DirectoryExists())
            {
                _logger.LogError("Specified game path does not exist: {Path}", game);
                StructuredError.WriteError(StructuredError.ErrorType.FileNotFound,
                    $"The specified game path does not exist: {game}",
                    new Dictionary<string, object?> { ["path"] = game.ToString() });
                return StructuredError.ExitCodeFor(StructuredError.ErrorType.FileNotFound);
            }
            gameFolder = game;
            _logger.LogInformation("Using provided game path: {Path}", game);
        }
        else
        {
            // 1. Game installed
            if (!_gameLocator.IsInstalled(modlist.GameType))
            {
                _logger.LogError("Required game '{Game}' is not installed on this system", modlist.GameType);
                StructuredError.WriteError(StructuredError.ErrorType.FileNotFound,
                    $"The game '{modlist.GameType}' required by this modlist is not installed. Install the game before running the modlist.",
                    new Dictionary<string, object?> { ["game"] = modlist.GameType.ToString() });
                return StructuredError.ExitCodeFor(StructuredError.ErrorType.FileNotFound);
            }
            gameFolder = _gameLocator.GameLocation(modlist.GameType);
        }

        var installer = StandardInstaller.Create(_serviceProvider, new InstallerConfiguration
        {
            Downloads = downloads,
            Install = output,
            ModList = modlist,
            Game = modlist.GameType,
            ModlistArchive = wabbajack,
            GameFolder = gameFolder,
            SseBsaCompressionLevel = compressionLevel.Value
        });

        InstallResult result;
        try
        {
            result = await installer.Begin(token);
        }
        catch (Exception ex)
        {
            var (errType, errMsg) = StructuredError.Classify(ex);
            if (errType == null) return 1; // cancelled
            _logger.LogError("Installation failed: {Message}", ex.Message);
            StructuredError.WriteError(errType, errMsg);
            return StructuredError.ExitCodeFor(errType);
        }

        // Handle different install results
        int EmitAndReturn(string type, string message)
        {
            StructuredError.WriteError(type, message);
            return StructuredError.ExitCodeFor(type);
        }

        return result switch
        {
            InstallResult.Succeeded   => 0,
            InstallResult.Cancelled   => 1,
            InstallResult.DownloadFailed => EmitAndReturn(StructuredError.ErrorType.NetworkError,
                "Installation failed: one or more downloads could not be completed."),
            InstallResult.GameMissing => EmitAndReturn(StructuredError.ErrorType.FileNotFound,
                "Installation failed: the target game was not found on this system."),
            InstallResult.GameInvalid => EmitAndReturn(StructuredError.ErrorType.ValidationFailed,
                "Installation failed: the game installation is invalid or corrupted."),
            _ => EmitAndReturn(StructuredError.ErrorType.EngineError,
                $"Installation failed with unexpected result: {result}."),
        };
    }


    /// <summary>
    private async Task<bool> DownloadMachineUrl(string machineUrl, AbsolutePath wabbajack, CancellationToken token)
    {
        _logger.LogInformation("Downloading {MachineUrl}", machineUrl);

        var lists = await _wjClient.LoadLists();
        var list = lists.FirstOrDefault(l => l.NamespacedName == machineUrl);
        if (list == null)
        {
            _logger.LogInformation("Couldn't find list {MachineUrl}", machineUrl);
            StructuredError.WriteError(StructuredError.ErrorType.FileNotFound,
                $"Modlist '{machineUrl}' was not found in the modlist index.",
                new Dictionary<string, object?> { ["machine_url"] = machineUrl });
            return false;
        }
        
        // Generate a filename from the machine URL if wabbajack path is empty
        if (wabbajack == AbsolutePath.Empty)
        {
            var fileName = machineUrl.Replace("/", "_@@_") + ".wabbajack";
            var downloadDir = JackifyConfig.GetDataDirectory().Combine("downloaded_mod_lists");
            downloadDir.CreateDirectory();
            wabbajack = downloadDir.Combine(fileName);
        }
        
        // Optimize validation: check file size first before expensive hash check
        // If size doesn't match, skip hash check and proceed to download/resume
        if (wabbajack.FileExists())
        {
            var existingSize = wabbajack.Size();
            if (existingSize == list.DownloadMetadata!.Size)
            {
                // Size matches - verify hash to ensure file is correct
                if (await wabbajack.Hash(token) == list.DownloadMetadata.Hash)
                {
                    _logger.LogInformation("File already exists, using cached file");
                    return true;
                }
                // Hash mismatch - file is corrupted, will be redownloaded
                _logger.LogInformation("Existing file hash mismatch, will redownload");
            }
            else
            {
                // Size mismatch - partial download, will resume
                _logger.LogInformation("Existing file size mismatch ({ExistingSize} vs {ExpectedSize}), will resume download", 
                    existingSize.ToFileSizeString(), list.DownloadMetadata.Size.ToFileSizeString());
            }
        }

        var state = _dispatcher.Parse(new Uri(list.Links.Download));
        var archive = new Archive
        {
            Name = wabbajack.FileName.ToString(),
            Hash = list.DownloadMetadata!.Hash,
            Size = list.DownloadMetadata.Size,
            State = state!
        };

        // Simple progress tracking - match DownloadModlist.cs approach
        var totalMB = archive.Size / 1024.0 / 1024.0;
        var samples = new System.Collections.Generic.Queue<(DateTime time, long bytes)>();
        var samplesLock = new object();
        const double sampleWindowSeconds = 3.0;

        // Initialize with existing file size if resuming
        long initialBytes = wabbajack.FileExists() ? wabbajack.Size() : 0;
        if (initialBytes > 0)
        {
            lock (samplesLock)
            {
                samples.Enqueue((DateTime.UtcNow, initialBytes));
            }
        }

        // Update display periodically from samples
        var displayCts = new CancellationTokenSource();
        var displayTask = Task.Run(async () =>
        {
            while (!displayCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, displayCts.Token);

                    var now = DateTime.UtcNow;
                    double speedMBps = 0;
                    long currentBytes = initialBytes;

                    lock (samplesLock)
                    {
                        // Remove samples older than our window
                        var cutoffTime = now.AddSeconds(-sampleWindowSeconds);
                        while (samples.Count > 0 && samples.Peek().time < cutoffTime)
                        {
                            samples.Dequeue();
                        }

                        // Calculate speed from samples
                        if (samples.Count >= 2)
                        {
                            var oldest = samples.Peek();
                            var sampleArray = samples.ToArray();
                            var newest = sampleArray[sampleArray.Length - 1];
                            var timeSpan = (newest.time - oldest.time).TotalSeconds;
                            var bytesDelta = newest.bytes - oldest.bytes;

                            if (timeSpan > 0.5 && bytesDelta > 0)
                            {
                                speedMBps = (bytesDelta / 1024.0 / 1024.0) / timeSpan;
                            }
                            currentBytes = newest.bytes;
                        }
                        else if (samples.Count == 1)
                        {
                            currentBytes = samples.Peek().bytes;
                        }
                    }

                    var processedMB = currentBytes / 1024.0 / 1024.0;
                    ConsoleOutput.PrintProgressWithDuration($"Downloading .wabbajack ({processedMB:F1}/{totalMB:F1}MB) - {speedMBps:F1}MB/s");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, displayCts.Token);

        try
        {
            // Use progress callback to collect samples (accurate, immediate)
            // Match upstream Wabbajack behavior: Download the file but don't validate hash after download
            // Upstream only validates hash before download (to check if file already exists)
            // The download itself will fail if there's a network issue, and file integrity is checked
            // when loading the modlist (which will fail if the file is corrupted)
            await _dispatcher.Download(archive, wabbajack, token, (processed, total) =>
            {
                // Add sample from callback (this is the accurate bytes downloaded)
                lock (samplesLock)
                {
                    samples.Enqueue((DateTime.UtcNow, processed));
                }
            }, null);
            
            // Verify file is a valid ZIP (quick integrity check)
            try
            {
                using var zipStream = wabbajack.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read);
                // If we can open it and read entries, it's valid
                var entryCount = zip.Entries.Count;
            }
            catch (System.IO.InvalidDataException ex)
            {
                _logger.LogError("Downloaded file is corrupted or incomplete (invalid ZIP). Deleting incomplete file. Error: {Error}", ex.Message);
                StructuredError.WriteError(StructuredError.ErrorType.ArchiveCorrupt,
                    "Downloaded .wabbajack file is corrupted or incomplete.",
                    new Dictionary<string, object?> { ["filename"] = wabbajack.FileName.ToString() });
                try
                {
                    wabbajack.Delete();
                }
                catch { }
                return false;
            }
            catch (Exception ex)
            {
                var (errType, errMsg) = StructuredError.Classify(ex);
                if (errType == null) return false;
                _logger.LogError("Failed to validate downloaded file as ZIP: {Message}", ex.Message);
                StructuredError.WriteError(errType,
                    $"Failed to validate downloaded .wabbajack file: {errMsg}",
                    new Dictionary<string, object?> { ["filename"] = wabbajack.FileName.ToString() });
                return false;
            }
        }
        catch (Exception ex)
        {
            var (errType, errMsg) = StructuredError.Classify(ex);
            if (errType == null) { _logger.LogInformation("Download cancelled by user"); return false; }
            _logger.LogError("Download failed: {Message}", ex.Message);
            StructuredError.WriteError(errType, errMsg,
                new Dictionary<string, object?> { ["filename"] = archive.Name });
            return false;
        }
        finally
        {
            displayCts.Cancel();
            try
            {
                await displayTask;
            }
            catch (OperationCanceledException)
            {
                // Swallow cancellation
            }
            
            // Clear progress line after completion
            ConsoleOutput.ClearProgressLine();
        }

        return true;
    }
}
