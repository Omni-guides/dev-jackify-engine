using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.Common;
using Wabbajack.Downloaders;
using Wabbajack.Downloaders.GameFile;
using Wabbajack.DTOs;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.DTOs.Interventions;
using Wabbajack.Installer;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.Services.OSIntegrated;
using Wabbajack.VFS;

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
        new OptionDefinition(typeof(AbsolutePath), "d", "downloads", "Downloads path")
    });

    internal async Task<int> Run(AbsolutePath wabbajack, AbsolutePath output, AbsolutePath downloads, string machineUrl, CancellationToken token)
    {
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

        var modlist = await StandardInstaller.LoadFromFile(_dtos, wabbajack);

        var installer = StandardInstaller.Create(_serviceProvider, new InstallerConfiguration
        {
            Downloads = downloads,
            Install = output,
            ModList = modlist,
            Game = modlist.GameType,
            ModlistArchive = wabbajack,
            GameFolder = _gameLocator.GameLocation(modlist.GameType)
        });

        var result = await installer.Begin(token);

        // Check for manual downloads and print summary if any were encountered
        var interventionHandler = _serviceProvider.GetService(typeof(IUserInterventionHandler)) as CLIUserInterventionHandler;
        if (interventionHandler != null && interventionHandler.GetManualDownloads().Any())
        {
            var manualDownloads = interventionHandler.GetManualDownloads();
            
            _logger.LogInformation("");
            _logger.LogInformation("╔══════════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║                           MANUAL DOWNLOADS REQUIRED                          ║");
            _logger.LogInformation("╚══════════════════════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("");
            _logger.LogInformation("The following {Count} files require manual download. Please download each file and place it in your downloads directory, then run the installation again.", manualDownloads.Count);
            _logger.LogInformation("");
            _logger.LogInformation("Downloads directory: {DownloadsPath}", downloads);
            _logger.LogInformation("");
            
            for (int i = 0; i < manualDownloads.Count; i++)
            {
                var manualDownload = manualDownloads[i];
                var url = manualDownload.Archive.State.PrimaryKeyString;
                
                // Extract just the URL part from the state string
                if (url.Contains("|"))
                {
                    url = url.Split('|').Last();
                }
                
                _logger.LogInformation("{Number}. {FileName}", i + 1, manualDownload.Archive.Name);
                _logger.LogInformation("    URL: {Url}", url);
                _logger.LogInformation("");
            }
            
            _logger.LogInformation("After downloading all files, run the installation command again.");
            _logger.LogInformation("");
            return 1; // Return error code to indicate manual downloads are needed
        }

        // Handle different install results
        return result switch
        {
            InstallResult.Succeeded => 0,  // Success
            InstallResult.DownloadFailed => 1,  // Manual downloads needed
            _ => 2  // Other errors
        };
    }

    private async Task<bool> DownloadMachineUrl(string machineUrl, AbsolutePath wabbajack, CancellationToken token)
    {
        _logger.LogInformation("Downloading {MachineUrl}", machineUrl);

        var lists = await _wjClient.LoadLists();
        var list = lists.FirstOrDefault(l => l.NamespacedName == machineUrl);
        if (list == null)
        {
            _logger.LogInformation("Couldn't find list {MachineUrl}", machineUrl);
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
        
        if (wabbajack.FileExists() && await _cache.FileHashCachedAsync(wabbajack, token) == list.DownloadMetadata!.Hash)
        {
            _logger.LogInformation("File already exists, using cached file");
            return true;
        }

        var state = _dispatcher.Parse(new Uri(list.Links.Download));
        var archive = new Archive
        {
            Name = wabbajack.FileName.ToString(),
            Hash = list.DownloadMetadata!.Hash,
            Size = list.DownloadMetadata.Size,
            State = state!
        };

        // Set up progress reporting with single-line updates
        var startTime = DateTime.UtcNow;
        Action<long, long> progressCallback = (processed, total) =>
        {
            var elapsed = DateTime.UtcNow - startTime;
            var speedMBps = elapsed.TotalSeconds > 0 ? (processed / 1024.0 / 1024.0) / elapsed.TotalSeconds : 0;
            var totalMB = total / 1024.0 / 1024.0;
            var processedMB = processed / 1024.0 / 1024.0;
            
            // Use single-line progress update instead of multi-line logging
            ConsoleOutput.PrintProgressWithDuration($"Downloading {archive.Name} ({processedMB:F1}/{totalMB:F1}MB) - {speedMBps:F1}MB/s");
        };

        await _dispatcher.Download(archive, wabbajack, token, progressCallback);
        
        // Clear the progress line after download completes
        ConsoleOutput.ClearProgressLine();

        return true;
    }
}