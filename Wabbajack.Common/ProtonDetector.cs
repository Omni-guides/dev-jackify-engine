using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using System.Collections.Generic;

namespace Wabbajack.Common;

public class ProtonDetector
{
    private readonly ILogger<ProtonDetector> _logger;
    private string? _cachedWinePath;

    public ProtonDetector(ILogger<ProtonDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the Jackify Engine Wine prefix path
    /// </summary>
    /// <returns>Path to the Wine prefix</returns>
    public AbsolutePath GetWinePrefix()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return homeDir.ToAbsolutePath().Combine("Jackify", ".engine", "wineprefix");
    }

    /// <summary>
    /// Gets the best available Proton wrapper path, prioritizing Proton versions
    /// </summary>
    /// <returns>Path to the Proton wrapper, or null if none found</returns>
    public async Task<string?> GetProtonWrapperPathAsync()
    {
        // Debug logging removed

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogDebug("Running on Windows, no Proton needed");
            return null;
        }

        // Try Steam's proton command
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var steamProtonPaths = new[]
        {
            Path.Combine(homeDir, ".local", "share", "Steam", "steamapps", "common", "Proton - Experimental", "proton"),
            Path.Combine(homeDir, ".local", "share", "Steam", "steamapps", "common", "Proton 10.0", "proton"),
            Path.Combine(homeDir, ".local", "share", "Steam", "steamapps", "common", "Proton 9.0", "proton"),
            Path.Combine(homeDir, ".steam", "steam", "steamapps", "common", "Proton - Experimental", "proton"),
            Path.Combine(homeDir, ".steam", "steam", "steamapps", "common", "Proton 10.0", "proton"),
            Path.Combine(homeDir, ".steam", "steam", "steamapps", "common", "Proton 9.0", "proton")
        };

        foreach (var protonPath in steamProtonPaths)
        {
            if (File.Exists(protonPath))
            {
                _logger.LogDebug($"Found Steam Proton wrapper at {protonPath}");
                return protonPath;
            }
        }

        _logger.LogDebug("No Proton wrapper found, falling back to Wine binary");
        return await GetWinePathAsync();
    }

    /// <summary>
    /// Gets the Steam compatibility data path for Proton
    /// </summary>
    /// <returns>Path to the Steam compatibility data directory</returns>
    public string GetSteamCompatDataPath()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ".local", "share", "Steam", "steamapps", "compatdata", "wabbajack_pfx");
    }

    /// <summary>
    /// Gets the Steam client install path for Proton
    /// </summary>
    /// <returns>Path to the Steam client installation</returns>
    public string GetSteamClientInstallPath()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ".local", "share", "Steam");
    }

    /// <summary>
    /// Clears the cached Wine path to force re-detection
    /// </summary>
    public void ClearWinePathCache()
    {
        _logger.LogInformation("Clearing cached Wine path: {Path}", _cachedWinePath);
        _cachedWinePath = null;
    }

    /// <summary>
    /// Gets the best available Wine binary path, prioritizing Proton versions
    /// </summary>
    /// <returns>Path to the Wine binary, or null if none found</returns>
    public async Task<string?> GetWinePathAsync()
    {
        _logger.LogInformation("ProtonDetector.GetWinePath() called");
        
        if (_cachedWinePath != null)
        {
            _logger.LogInformation("Using cached Wine path: {Path}", _cachedWinePath);
            return _cachedWinePath;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogDebug("Running on Windows, no Wine needed");
            return null;
        }

        // Ensure Wine prefix exists and is properly initialized
        await EnsureWinePrefixInitialized();

        // Try Proton versions in order of preference
        var protonVersions = new[]
        {
            "Proton - Experimental",
            "Proton 10.0",
            "Proton 9.0"
        };

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var steamPaths = new[]
        {
            Path.Combine(homeDir, ".local", "share", "Steam"),
            Path.Combine(homeDir, ".steam", "steam"),
            "/usr/share/steam",
            "/opt/steam"
        };

        foreach (var steamPath in steamPaths)
        {
            if (!Directory.Exists(steamPath)) continue;

            var protonDir = Path.Combine(steamPath, "steamapps", "common");
            if (!Directory.Exists(protonDir)) continue;

            foreach (var version in protonVersions)
            {
                var winePath = Path.Combine(protonDir, version, "files", "bin", "wine");
                if (File.Exists(winePath))
                {
                    _logger.LogInformation("Found Proton Wine: {Version} at {Path}", version, winePath);
                    _cachedWinePath = winePath;
                    return winePath;
                }
                else
                {
                    _logger.LogDebug("Proton version {Version} not found at {Path}", version, winePath);
                }
            }
        }

        // Fallback to system Wine
        var systemWine = FindSystemWine();
        if (systemWine != null)
        {
            _logger.LogInformation("Using system Wine: {Path}", systemWine);
            _cachedWinePath = systemWine;
            return systemWine;
        }

        _logger.LogWarning("No Wine installation found");
        return null;
    }

    /// <summary>
    /// Synchronous version for backward compatibility
    /// </summary>
    public string? GetWinePath()
    {
        return GetWinePathAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Converts a Linux path to a Wine path (Z: drive mapping)
    /// </summary>
    /// <param name="linuxPath">Linux path to convert</param>
    /// <returns>Wine path with Z: drive mapping</returns>
    public static string ConvertToWinePath(string linuxPath)
    {
        // Convert Linux path to Wine path with backslashes
        return $"Z:{linuxPath.Replace('/', '\\')}";
    }

    /// <summary>
    /// Converts a Linux path to a Wine path (Z: drive mapping)
    /// </summary>
    /// <param name="path">Linux path to convert</param>
    /// <returns>Wine path with Z: drive mapping</returns>
    public static string ConvertToWinePath(AbsolutePath path)
    {
        return ConvertToWinePath(path.ToString());
    }

    private string? FindSystemWine()
    {
        var winePaths = new[]
        {
            "/usr/bin/wine",
            "/usr/local/bin/wine",
            "/opt/wine/bin/wine"
        };

        foreach (var winePath in winePaths)
        {
            if (File.Exists(winePath))
                return winePath;
        }

        return null;
    }

    /// <summary>
    /// Ensures the Wine prefix exists and is properly initialized
    /// </summary>
    private async Task EnsureWinePrefixInitialized()
    {
        var prefixPath = GetWinePrefix();
        var system32Path = prefixPath.Combine("drive_c", "windows", "system32");
        
        _logger.LogInformation("Checking Wine prefix at {PrefixPath}", prefixPath);
        
        // Check if prefix already exists and has basic Windows components
        if (Directory.Exists(prefixPath.ToString()) && Directory.Exists(system32Path.ToString()))
        {
            _logger.LogInformation("Wine prefix already exists and appears initialized");
            return;
        }
        
        _logger.LogInformation("Initializing new Wine prefix at {PrefixPath}", prefixPath);
        
        // Create the prefix directory
        Directory.CreateDirectory(prefixPath.ToString());
        
        // Get Wine binary for initialization
        var winePath = await FindWineBinary();
        if (winePath == null)
        {
            throw new InvalidOperationException("No Wine installation found for prefix initialization");
        }
        
        // Initialize the prefix with wineboot
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = winePath,
                Arguments = "wineboot --init",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                EnvironmentVariables =
                {
                    ["WINEPREFIX"] = prefixPath.ToString(),
                    ["WINEDEBUG"] = "-all"
                }
            }
        };
        
        var output = new StringBuilder();
        var error = new StringBuilder();
        
        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                output.AppendLine(e.Data);
        };
        
        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                error.AppendLine(e.Data);
        };
        
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        
        if (process.ExitCode != 0)
        {
            var errorMessage = $"Failed to initialize Wine prefix. Exit code: {process.ExitCode}\nStdOut: {output}\nStdErr: {error}";
            _logger.LogError(errorMessage);
            throw new InvalidOperationException(errorMessage);
        }
        
        _logger.LogInformation("Wine prefix initialized successfully");
    }

    /// <summary>
    /// Finds a Wine binary without requiring a prefix
    /// </summary>
    private async Task<string?> FindWineBinary()
    {
        // Try Proton versions in order of preference
        var protonVersions = new[] { "Proton - Experimental", "Proton 10.0", "Proton 9.0" };
        
        foreach (var version in protonVersions)
        {
            var winePath = FindProtonWinePath(version);
            if (winePath != null && File.Exists(winePath))
            {
                _logger.LogInformation("Found Proton Wine: {Version} at {Path}", version, winePath);
                return winePath;
            }
        }
        
        // Try system Wine as fallback
        var systemWine = "/usr/bin/wine";
        if (File.Exists(systemWine))
        {
            _logger.LogInformation("Found system Wine at {Path}", systemWine);
            return systemWine;
        }
        
        return null;
    }

    /// <summary>
    /// Finds the Wine binary path for a specific Proton version
    /// </summary>
    private string? FindProtonWinePath(string version)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var steamPaths = new[]
        {
            Path.Combine(homeDir, ".local", "share", "Steam"),
            Path.Combine(homeDir, ".steam", "steam"),
            "/usr/share/steam",
            "/opt/steam"
        };

        foreach (var steamPath in steamPaths)
        {
            if (!Directory.Exists(steamPath)) continue;

            var protonDir = Path.Combine(steamPath, "steamapps", "common");
            if (!Directory.Exists(protonDir)) continue;

            var winePath = Path.Combine(protonDir, version, "files", "bin", "wine");
            if (File.Exists(winePath))
            {
                return winePath;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a temporary Wine prefix for texconv operations using Proton
    /// </summary>
    /// <returns>Path to the temporary Wine prefix, or null if creation failed</returns>
    public async Task<string?> CreateTemporaryWinePrefixAsync()
    {
        _logger.LogDebug("Creating temporary Wine prefix for texconv using Proton");
        
        var protonWrapperPath = await GetProtonWrapperPathAsync();
        if (protonWrapperPath == null)
        {
            _logger.LogError("No Proton wrapper found for temporary prefix creation");
            return null;
        }

        try
        {
            // Create temporary directory for Wine prefix in our own directory structure
            var jackifyEngineDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Jackify", ".engine");
            var tempPrefixPath = Path.Combine(jackifyEngineDir, $"temp-wine-{Guid.NewGuid()}");
            Directory.CreateDirectory(tempPrefixPath);
            
            // Create the pfx subdirectory that Proton expects
            var pfxPath = Path.Combine(tempPrefixPath, "pfx");
            Directory.CreateDirectory(pfxPath);
            
            _logger.LogDebug($"Created temporary Wine prefix at: {tempPrefixPath}");

            // Set up environment variables for Proton
            var envVars = new Dictionary<string, string>
            {
                ["STEAM_COMPAT_DATA_PATH"] = tempPrefixPath,
                ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = GetSteamClientInstallPath(),
                ["WINEPREFIX"] = pfxPath, // Use absolute path for Wine
                ["WINEDEBUG"] = "-all",
                ["DISPLAY"] = "",
                ["WINEDLLOVERRIDES"] = "msdia80.dll=n"
            };

            // Initialize the Wine prefix using Proton's wineboot
            var initProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = protonWrapperPath,
                    Arguments = "run wineboot --init",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            // Set environment variables
            foreach (var kvp in envVars)
            {
                initProcess.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }

            initProcess.Start();
            await initProcess.WaitForExitAsync();

            if (initProcess.ExitCode == 0)
            {
                _logger.LogDebug("Temporary Wine prefix initialized successfully using Proton");
                return tempPrefixPath;
            }
            else
            {
                _logger.LogError($"Failed to initialize temporary Wine prefix with Proton, exit code: {initProcess.ExitCode}");
                // Clean up failed prefix
                try { Directory.Delete(tempPrefixPath, true); } catch { }
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to create temporary Wine prefix with Proton: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Cleans up a temporary Wine prefix
    /// </summary>
    /// <param name="prefixPath">Path to the temporary Wine prefix to clean up</param>
    public void CleanupTemporaryWinePrefix(string prefixPath)
    {
        try
        {
            if (Directory.Exists(prefixPath))
            {
                Directory.Delete(prefixPath, true);
                _logger.LogDebug($"Cleaned up temporary Wine prefix: {prefixPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Failed to clean up temporary Wine prefix: {prefixPath} - {ex.Message}");
        }
    }

    /// <summary>
    /// Cleans up all leftover temporary Wine prefixes in the Jackify engine directory
    /// </summary>
    public void CleanupAllTemporaryWinePrefixes()
    {
        try
        {
            var jackifyEngineDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Jackify", ".engine");
            if (!Directory.Exists(jackifyEngineDir))
                return;

            var tempPrefixes = Directory.GetDirectories(jackifyEngineDir, "temp-wine-*");
            var cleanedCount = 0;

            foreach (var prefixPath in tempPrefixes)
            {
                try
                {
                    Directory.Delete(prefixPath, true);
                    cleanedCount++;
                    _logger.LogDebug($"Cleaned up leftover temporary Wine prefix: {prefixPath}");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"Failed to clean up leftover temporary Wine prefix: {prefixPath} - {ex.Message}");
                }
            }

            if (cleanedCount > 0)
            {
                _logger.LogInformation($"Cleaned up {cleanedCount} leftover temporary Wine prefixes");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Failed to clean up leftover temporary Wine prefixes: {ex.Message}");
        }
    }
}
