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
    public class ProtonPrefixManager
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
            
            // Create prefix in ~/jackify/.prefix-<UUID>
            _prefixBaseDir = KnownFolders.EntryPoint.Parent.Parent.Combine("jackify");
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
                    ["DISPLAY"] = ""
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

            // Convert Linux path to Wine path for texconv.exe
            var texconvPath = @"Tools\texconv.exe".ToRelativePath().RelativeTo(KnownFolders.EntryPoint);
            var wineTexconvPath = ProtonDetector.ConvertToWinePath(texconvPath);
            
            _logger.LogDebug("Creating texconv process with Wine path: {WinePath}", wineTexconvPath);
            
            // Build the full command with proper quoting
            var args = new object[] { "run", wineTexconvPath }.Concat(texConvArgs);
            var argString = string.Join(" ", args.Select(arg => arg.ToString().Contains(" ") ? $"\"{arg}\"" : arg.ToString()));
            
            return new ProcessHelper
            {
                Path = "bash".ToAbsolutePath(),
                Arguments = new object[] { "-c", $"\"{protonWrapperPath}\" {argString}" },
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["WINEPREFIX"] = prefix.ToString(),
                    ["STEAM_COMPAT_DATA_PATH"] = prefix.ToString(),
                    ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = _protonDetector.GetSteamClientInstallPath(),
                    ["WINEDEBUG"] = "-all",
                    ["DISPLAY"] = ""
                },
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

            // Convert Linux path to Wine path for texdiag.exe
            var texdiagPath = @"Tools\texdiag.exe".ToRelativePath().RelativeTo(KnownFolders.EntryPoint);
            var wineTexdiagPath = ProtonDetector.ConvertToWinePath(texdiagPath);
            
            _logger.LogDebug("Creating texdiag process with Wine path: {WinePath}", wineTexdiagPath);
            
            // Build the full command with proper quoting
            var args = new object[] { "run", wineTexdiagPath }.Concat(texDiagArgs);
            var argString = string.Join(" ", args.Select(arg => arg.ToString().Contains(" ") ? $"\"{arg}\"" : arg.ToString()));
            
            return new ProcessHelper
            {
                Path = "bash".ToAbsolutePath(),
                Arguments = new object[] { "-c", $"\"{protonWrapperPath}\" {argString}" },
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["WINEPREFIX"] = prefix.ToString(),
                    ["STEAM_COMPAT_DATA_PATH"] = prefix.ToString(),
                    ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = _protonDetector.GetSteamClientInstallPath(),
                    ["WINEDEBUG"] = "-all",
                    ["DISPLAY"] = ""
                },
                ThrowOnNonZeroExitCode = true,
                LogError = true
            };
        }

        public void Cleanup()
        {
            // Optionally clean up the prefix directory
            // For now, we'll keep it for potential reuse
            // _currentPrefix.DeleteDirectory();
        }
    }
}
