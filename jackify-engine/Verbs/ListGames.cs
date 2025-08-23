using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.Downloaders.GameFile;
using Wabbajack.DTOs;
using Wabbajack.Paths.IO;
using System.IO;
using Wabbajack.Paths;

namespace Wabbajack.CLI.Verbs;

public class ListGames
{
    private readonly ILogger<ListGames> _logger;
    private readonly GameLocator _locator;

    public ListGames(ILogger<ListGames> logger, GameLocator locator)
    {
        _logger = logger;
        _locator = locator;
    }

    /// <summary>
    /// Gets version information for a game executable, handling both Windows .exe files and Linux native executables
    /// </summary>
    private string GetGameVersion(AbsolutePath mainFile)
    {
        try
        {
            // For Windows .exe files, use FileVersionInfo
            if (mainFile.Extension == new Extension(".exe"))
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(mainFile.ToString());
                return versionInfo.ProductVersion ?? versionInfo.FileVersion ?? "Unknown";
            }
            
            // For Linux native executables, try to extract version using alternative methods
            return GetLinuxExecutableVersion(mainFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to get version for {File}: {Error}", mainFile, ex.Message);
            return "Unknown";
        }
    }

    /// <summary>
    /// Attempts to get version information from Linux native executables
    /// </summary>
    private string GetLinuxExecutableVersion(AbsolutePath executablePath)
    {
        try
        {
            // Method 1: Try running the executable with --version flag
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = executablePath.ToString(),
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    process.WaitForExit(3000); // 3 second timeout
                    if (process.ExitCode == 0)
                    {
                        var output = process.StandardOutput.ReadToEnd().Trim();
                        if (!string.IsNullOrEmpty(output) && output.Length < 100)
                        {
                            return output;
                        }
                    }
                }
            }
            catch
            {
                // Ignore version detection failures, continue to next method
            }

            // Method 2: Check if there's a version file in the game directory
            var gameDir = executablePath.Parent;
            var versionFiles = new[] { "version.txt", "VERSION", ".version", "version" };
            
            foreach (var versionFile in versionFiles)
            {
                var versionPath = gameDir.Combine(versionFile);
                if (versionPath.FileExists())
                {
                    var content = File.ReadAllText(versionPath.ToString()).Trim();
                    if (!string.IsNullOrEmpty(content) && content.Length < 50)
                    {
                        return content;
                    }
                }
            }

            // Method 3: Use file modification time as a fallback
            var fileInfo = new FileInfo(executablePath.ToString());
            return $"Modified: {fileInfo.LastWriteTime:yyyy-MM-dd}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to get Linux executable version for {File}: {Error}", executablePath, ex.Message);
            return "Unknown";
        }
    }

    public static VerbDefinition Definition = new("list-games",
        "Lists all games Wabbajack recognizes, and their installed versions/locations (if any)", Array.Empty<OptionDefinition>());
    
    internal Task<int> Run(CancellationToken token)
    {
        foreach (var game in GameRegistry.Games.OrderBy(g => g.Value.HumanFriendlyGameName))
        {
            if (_locator.IsInstalled(game.Key))
            {
                var location = _locator.GameLocation(game.Key);
                var mainFile = game.Value.MainExecutable!.Value.RelativeTo(location);

                if (!mainFile.FileExists())
                {
                    _logger.LogWarning("Main file {file} for {game} does not exist", mainFile, game.Key);
                    _logger.LogInformation("[X] {Game} (Unknown - Missing Executable) -> Path: {Path}", game.Value.HumanFriendlyGameName, location);
                }
                else
                {
                    var version = GetGameVersion(mainFile);
                    _logger.LogInformation("[X] {Game} ({Version}) -> Path: {Path}", game.Value.HumanFriendlyGameName, version, location);
                }
            }
            else
            {
                _logger.LogInformation("[ ] {Game}", game.Value.HumanFriendlyGameName);
            }
        }

        return Task.FromResult(0);
    }
}