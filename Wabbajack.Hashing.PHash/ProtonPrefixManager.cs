using System;
using System.IO;
using System.Threading.Tasks;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using System.Collections.Generic;
using System.Linq;
using Wabbajack.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace Wabbajack.Hashing.PHash
{
    public class ProtonPrefixManager : IDisposable
    {
        private readonly AbsolutePath _prefixBaseDir;
        private readonly AbsolutePath _currentPrefix;
        private readonly ProtonDetector _protonDetector;
        private readonly ILogger _logger;
        private bool _initialized = false;

        public ProtonPrefixManager(ILogger logger)
        {
            _logger = logger;
            _protonDetector = new ProtonDetector(NullLogger<ProtonDetector>.Instance);
            
            // Create prefix in {jackify_data_dir}/.prefix-<UUID>
            _prefixBaseDir = JackifyConfig.GetDataDirectory();
            _currentPrefix = _prefixBaseDir.Combine($".prefix-{Guid.NewGuid():N}");
        }

        public async Task<AbsolutePath> GetOrCreatePrefix()
        {
            if (!_initialized)
            {
                await InitializePrefix();
                _initialized = true;
            }
            return _currentPrefix;
        }

        private async Task InitializePrefix()
        {
            _logger.LogDebug("Initializing Proton prefix at {PrefixPath}", _currentPrefix);

            // Ensure base directory exists
            _prefixBaseDir.CreateDirectory();

            // Create the prefix directory
            _currentPrefix.CreateDirectory();

            // Get Proton wrapper path using the detector
            var protonWrapperPath = await _protonDetector.GetProtonWrapperPathAsync();
            if (protonWrapperPath == null)
            {
                throw new InvalidOperationException("No Proton installation found. Please ensure Steam is installed with Proton (Experimental, 10.0, or 9.0)");
            }

            _logger.LogDebug("Using Proton wrapper at {ProtonPath}", protonWrapperPath);

            // Initialize the Proton prefix with wineboot
            var ph = new ProcessHelper
            {
                Path = protonWrapperPath.ToAbsolutePath(),
                Arguments = new object[] { "run", "wineboot", "--init" },
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["WINEPREFIX"] = _currentPrefix.ToString(),
                    ["STEAM_COMPAT_DATA_PATH"] = _currentPrefix.ToString(),
                    ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = _protonDetector.GetSteamClientInstallPath(),
                    ["WINEDEBUG"] = "-all",
                    ["DISPLAY"] = "",
                    ["WAYLAND_DISPLAY"] = "",
                    ["WINEDLLOVERRIDES"] = "msdia80.dll=n;conhost.exe=d;cmd.exe=d"
                },
                ThrowOnNonZeroExitCode = true,
                LogError = true
            };

            await ph.Start();
            _logger.LogDebug("Proton prefix initialized successfully");
        }

        public async Task<ProcessHelper> CreateTexConvProcess(object[] texConvArgs)
        {
            var prefix = await GetOrCreatePrefix();
            
            // Get Proton wrapper path
            var protonWrapperPath = await _protonDetector.GetProtonWrapperPathAsync();
            if (protonWrapperPath == null)
            {
                throw new InvalidOperationException("No Proton installation found. Please ensure Steam is installed with Proton (Experimental, 10.0, or 9.0)");
            }
            
            return new ProcessHelper
            {
                Path = protonWrapperPath.ToAbsolutePath(),
                Arguments = new object[] { "run", "Tools\\texconv.exe" }.Concat(texConvArgs),
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["WINEPREFIX"] = prefix.ToString(),
                    ["STEAM_COMPAT_DATA_PATH"] = prefix.ToString(),
                    ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = _protonDetector.GetSteamClientInstallPath(),
                    ["WINEDEBUG"] = "-all",
                    ["DISPLAY"] = "",
                    ["WAYLAND_DISPLAY"] = "",
                    ["WINEDLLOVERRIDES"] = "msdia80.dll=n;conhost.exe=d;cmd.exe=d"
                },
                WorkingDirectory = KnownFolders.EntryPoint.ToString(),
                ThrowOnNonZeroExitCode = true,
                LogError = true
            };
        }

        public async Task<ProcessHelper> CreateTexDiagProcess(object[] texDiagArgs)
        {
            var prefix = await GetOrCreatePrefix();
            
            // Get Proton wrapper path
            var protonWrapperPath = await _protonDetector.GetProtonWrapperPathAsync();
            if (protonWrapperPath == null)
            {
                throw new InvalidOperationException("No Proton installation found. Please ensure Steam is installed with Proton (Experimental, 10.0, or 9.0)");
            }
            
            return new ProcessHelper
            {
                Path = protonWrapperPath.ToAbsolutePath(),
                Arguments = new object[] { "run", "Tools\\texdiag.exe" }.Concat(texDiagArgs),
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["WINEPREFIX"] = prefix.ToString(),
                    ["STEAM_COMPAT_DATA_PATH"] = prefix.ToString(),
                    ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = _protonDetector.GetSteamClientInstallPath(),
                    ["WINEDEBUG"] = "-all",
                    ["DISPLAY"] = "",
                    ["WAYLAND_DISPLAY"] = "",
                    ["WINEDLLOVERRIDES"] = "msdia80.dll=n;conhost.exe=d;cmd.exe=d"
                },
                WorkingDirectory = KnownFolders.EntryPoint.ToString(),
                ThrowOnNonZeroExitCode = true,
                LogError = true
            };
        }

        public async Task<ProcessHelper> Create7zProcess(object[] sevenZipArgs)
        {
            var prefix = await GetOrCreatePrefix();
            
            // Get Proton wrapper path
            var protonWrapperPath = await _protonDetector.GetProtonWrapperPathAsync();
            if (protonWrapperPath == null)
            {
                throw new InvalidOperationException("No Proton installation found. Please ensure Steam is installed with Proton (Experimental, 10.0, or 9.0)");
            }
            
            return new ProcessHelper
            {
                Path = protonWrapperPath.ToAbsolutePath(),
                Arguments = new object[] { "run", "Extractors\\windows-x64\\7z.exe" }.Concat(sevenZipArgs),
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["WINEPREFIX"] = prefix.ToString(),
                    ["STEAM_COMPAT_DATA_PATH"] = prefix.ToString(),
                    ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = _protonDetector.GetSteamClientInstallPath(),
                    ["WINEDEBUG"] = "-all",
                    ["DISPLAY"] = ""
                },
                WorkingDirectory = KnownFolders.EntryPoint.ToString(),
                ThrowOnNonZeroExitCode = true,
                LogError = true
            };
        }

        public void Cleanup()
        {
            try
            {
                if (!_currentPrefix.DirectoryExists()) return;

                _logger.LogDebug("Cleaning up Wine prefix: {PrefixPath}", _currentPrefix);

                // Safety: ensure prefix is under our managed base directory
                var prefixStr = _currentPrefix.ToString();
                var baseStr = _prefixBaseDir.ToString();
                if (!prefixStr.StartsWith(baseStr, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Refusing to delete prefix outside base dir: {PrefixPath}", _currentPrefix);
                    return;
                }

                // Best-effort clean specific risky area: unlink dosdevices symlinks rather than recurse
                var dosDevices = _currentPrefix.Combine("pfx").Combine("dosdevices");
                if (dosDevices.DirectoryExists())
                {
                    try
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(dosDevices.ToString()))
                        {
                            var fi = new FileInfo(entry);
                            if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                // Remove the link itself; do not follow
                                try { File.Delete(entry); } catch { /* ignore */ }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Ignoring error while unlinking dosdevices entries");
                    }
                }

                // Now delete the prefix directory tree (our DeleteDirectory already skips symlinks)
                _currentPrefix.DeleteDirectory();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup Wine prefix: {PrefixPath}", _currentPrefix);
            }
        }

        public void Dispose()
        {
            Cleanup();
        }
    }
}
