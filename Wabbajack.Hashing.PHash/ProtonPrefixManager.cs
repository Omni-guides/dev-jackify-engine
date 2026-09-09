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
using System.Threading;

namespace Wabbajack.Hashing.PHash
{
    public class ProtonPrefixManager : IDisposable
    {
        private readonly AbsolutePath _prefixBaseDir;
        private readonly AbsolutePath _currentPrefix;
        private readonly ProtonDetector _protonDetector;
        private readonly ILogger _logger;
        private volatile bool _initialized = false;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);
        private readonly string _gpuDisplay;
        private bool _gpuConfigLogged = false;

        // Cache GPU detection results to avoid repeated lspci/DRI scans on every texture conversion
        private readonly bool _hasMultipleGpus;
        private readonly bool _hasNvidiaGpu;

        public ProtonPrefixManager(ILogger logger)
        {
            _logger = logger;
            _protonDetector = new ProtonDetector(NullLogger<ProtonDetector>.Instance);

            // Create prefix in {jackify_data_dir}/.prefix-<UUID>
            _prefixBaseDir = JackifyConfig.GetDataDirectory();
            _currentPrefix = _prefixBaseDir.Combine($".prefix-{Guid.NewGuid():N}");

            // Get valid DISPLAY for GPU acceleration
            // GPU-accelerated DirectX Compute Shaders require a valid DISPLAY
            // Use user's display if available, otherwise fall back to :0
            _gpuDisplay = GetValidDisplay();

            // Perform GPU detection once during construction to avoid thousands of system calls
            // For modlists with thousands of textures (e.g., Magnum Opus), this prevents:
            // - Thousands of lspci subprocess spawns
            // - Thousands of /dev/dri directory scans
            _hasMultipleGpus = DetectMultipleGpus();
            _hasNvidiaGpu = DetectNvidiaGpu();

            _logger.LogDebug("Using DISPLAY={Display} for GPU-accelerated texture processing", _gpuDisplay);
        }
        
        /// <summary>
        /// Gets a valid DISPLAY value for GPU-accelerated operations.
        /// GPU acceleration requires a valid X11 display for DXVK/VKD3D to access the GPU.
        /// On Wayland, XWayland provides X11 compatibility, so DISPLAY will work there too.
        /// </summary>
        private static string GetValidDisplay()
        {
            // First, check if user has a DISPLAY set (works for both X11 and Wayland via XWayland)
            var userDisplay = Environment.GetEnvironmentVariable("DISPLAY");
            if (!string.IsNullOrEmpty(userDisplay))
            {
                return userDisplay;
            }
            
            // Fall back to :0 (default X11 display)
            // On X11: Direct access to display :0
            // On Wayland: XWayland typically runs on :0, so this should work
            // Note: For true headless systems without X11/Wayland, Xvfb would be needed
            return ":0";
        }
        
        /// <summary>
        /// Detects if the system has multiple GPUs (iGPU + dGPU setup).
        /// Returns true if multiple DRI devices (card0, card1, etc.) are found.
        /// Called once during construction and cached.
        /// </summary>
        private bool DetectMultipleGpus()
        {
            try
            {
                var driPath = "/dev/dri";
                if (!Directory.Exists(driPath))
                {
                    _logger.LogDebug("GPU_DETECTION: /dev/dri does not exist");
                    return false;
                }

                // Count DRI card devices
                var cardCount = Directory.GetFiles(driPath, "card*")
                    .Count(f => File.Exists(f));

                _logger.LogDebug("GPU_DETECTION: Found {Count} DRI card devices", cardCount);

                // Multiple GPUs if we have more than one card device
                return cardCount > 1;
            }
            catch (Exception ex)
            {
                // If we can't detect, assume single GPU (safer default)
                _logger.LogDebug(ex, "GPU_DETECTION: Failed to detect multiple GPUs, assuming single GPU");
                return false;
            }
        }
        
        /// <summary>
        /// Detects if the system has an NVIDIA GPU.
        /// Checks lspci output for NVIDIA VGA controllers.
        /// Called once during construction and cached.
        /// </summary>
        private bool DetectNvidiaGpu()
        {
            try
            {
                // Try to run lspci to detect NVIDIA GPUs. Stderr is left unredirected (inherits the
                // parent's) rather than captured and never drained: redirecting it without reading it
                // risks a deadlock if lspci fills the stderr pipe while ReadToEnd blocks on stdout.
                var startInfo = new ProcessStartInfo
                {
                    FileName = "lspci",
                    Arguments = "",
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    _logger.LogDebug("GPU_DETECTION: lspci process failed to start");
                    return false;
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                // Check if output contains NVIDIA VGA controller
                var hasNvidia = output.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
                               output.Contains("VGA", StringComparison.OrdinalIgnoreCase);

                if (hasNvidia)
                {
                    _logger.LogDebug("GPU_DETECTION: NVIDIA GPU detected via lspci");
                }
                else
                {
                    _logger.LogDebug("GPU_DETECTION: No NVIDIA GPU found in lspci output");
                }

                return hasNvidia;
            }
            catch (Exception ex)
            {
                // If lspci fails or isn't available, assume no NVIDIA GPU (safer default)
                _logger.LogDebug(ex, "GPU_DETECTION: lspci detection failed, assuming no NVIDIA GPU");
                return false;
            }
        }
        
        /// <summary>
        /// Gets environment variables for GPU-accelerated processes that suppress windows.
        /// Windows are suppressed via WINEDLLOVERRIDES (conhost.exe, cmd.exe) and ProcessHelper.CreateNoWindow.
        /// If TexconvConfig.DisableGpuTexconv is true, this method returns the legacy
        /// CPU-only environment (DISPLAY=\"\", no PRIME/DXVK hints) to match pre-GPU behavior.
        /// </summary>
        private Dictionary<string, string> GetGpuEnvironmentVariables(AbsolutePath prefix)
        {
            // Fallback: CPU-only texconv behavior (legacy mode)
            if (TexconvConfig.DisableGpuTexconv)
            {
                var cpuEnv = new Dictionary<string, string>
                {
                    ["WINEPREFIX"] = prefix.ToString(),
                    ["STEAM_COMPAT_DATA_PATH"] = prefix.ToString(),
                    ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = _protonDetector.GetSteamClientInstallPath(),
                    ["WINEDEBUG"] = "-all",
                    // Legacy behavior: hide Proton windows and prevent GPU usage
                    ["DISPLAY"] = "",
                    ["WAYLAND_DISPLAY"] = "",
                    ["WINEDLLOVERRIDES"] = "msdia80.dll=n;conhost.exe=d;cmd.exe=d"
                };

                _logger.LogDebug("GPU_CONFIG: GPU acceleration for texconv.exe disabled via --disable-gpu-texconv (CPU-only mode)");
                return cpuEnv;
            }

            var env = new Dictionary<string, string>
            {
                ["WINEPREFIX"] = prefix.ToString(),
                ["STEAM_COMPAT_DATA_PATH"] = prefix.ToString(),
                ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = _protonDetector.GetSteamClientInstallPath(),
                ["WINEDEBUG"] = "-all",
                // CRITICAL: Valid DISPLAY required for GPU access (DXVK/VKD3D need X11)
                // Works on both X11 (direct) and Wayland (via XWayland)
                ["DISPLAY"] = _gpuDisplay,
                // Suppress console windows: conhost.exe and cmd.exe disabled
                // This prevents 30,000+ focus-stealing windows during texture conversion
                ["WINEDLLOVERRIDES"] = "msdia80.dll=n;conhost.exe=d;cmd.exe=d",
                // Force DXVK usage for GPU acceleration (Proton should do this automatically, but be explicit)
                ["PROTON_USE_WINED3D"] = "0"
            };
            
            // Preserve WAYLAND_DISPLAY if set (for Wayland users, XWayland will handle X11 compatibility)
            var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
            if (!string.IsNullOrEmpty(waylandDisplay))
            {
                env["WAYLAND_DISPLAY"] = waylandDisplay;
            }
            
            // Handle GPU selection for dual-GPU systems (iGPU + dGPU)
            // NVIDIA GPUs require NVIDIA-specific environment variables
            // AMD/Mesa GPUs use DRI_PRIME
            var nvPrimeRenderOffload = Environment.GetEnvironmentVariable("__NV_PRIME_RENDER_OFFLOAD");
            var nvGlxVendorLibrary = Environment.GetEnvironmentVariable("__GLX_VENDOR_LIBRARY_NAME");
            
            if (!string.IsNullOrEmpty(nvPrimeRenderOffload) || !string.IsNullOrEmpty(nvGlxVendorLibrary))
            {
                // User has explicitly set NVIDIA variables - respect their choice
                if (!string.IsNullOrEmpty(nvPrimeRenderOffload))
                    env["__NV_PRIME_RENDER_OFFLOAD"] = nvPrimeRenderOffload;
                if (!string.IsNullOrEmpty(nvGlxVendorLibrary))
                    env["__GLX_VENDOR_LIBRARY_NAME"] = nvGlxVendorLibrary;
                // Also set Vulkan layer for NVIDIA-only if not explicitly set
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("__VK_LAYER_NV_optimus")))
                {
                    env["__VK_LAYER_NV_optimus"] = "NVIDIA_only";
                }
                _logger.LogDebug("Using user-specified NVIDIA GPU environment variables");
            }
            else if (_hasNvidiaGpu && _hasMultipleGpus)
            {
                // NVIDIA dGPU detected in dual-GPU system - set NVIDIA variables to use dGPU
                env["__NV_PRIME_RENDER_OFFLOAD"] = "1";
                env["__GLX_VENDOR_LIBRARY_NAME"] = "nvidia";
                env["__VK_LAYER_NV_optimus"] = "NVIDIA_only";
                if (!_gpuConfigLogged)
                {
                    _logger.LogDebug("NVIDIA dGPU detected in dual-GPU system - using NVIDIA PRIME render offload for GPU acceleration");
                    _gpuConfigLogged = true;
                }
            }
            else
            {
                // Handle DRI_PRIME for AMD/Mesa dual-GPU systems
                // DRI_PRIME=1 tells Mesa to use the discrete GPU instead of integrated
                var driPrime = Environment.GetEnvironmentVariable("DRI_PRIME");
                if (!string.IsNullOrEmpty(driPrime))
                {
                    // User has explicitly set DRI_PRIME - respect their choice
                    env["DRI_PRIME"] = driPrime;
                }
                else if (_hasMultipleGpus)
                {
                    // Multiple GPUs detected (AMD/Mesa) - default to dGPU for better performance
                    env["DRI_PRIME"] = "1";
                    _logger.LogDebug("Multiple GPUs detected (AMD/Mesa) - using DRI_PRIME=1 to prefer dGPU");
                }
                // If single GPU, don't set GPU selection variables (no effect, but cleaner)
            }
            
            return env;
        }

        public async Task<AbsolutePath> GetOrCreatePrefix()
        {
            if (_initialized)
            {
                return _currentPrefix;
            }

            await _initializationGate.WaitAsync();
            try
            {
                if (!_initialized)
                {
                    await InitializePrefix();
                    _initialized = true;
                }
            }
            finally
            {
                _initializationGate.Release();
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

            // GE-Proton (and other builds using ProtonFixes) expect ~/.config/protonfixes to exist;
            // if it is missing, ProtonFixes aborts and can cause non-zero exit when running texconv/7z.
            // Creating it here is harmless for Valve Proton (it does not use ProtonFixes).
            var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                var protonFixesDir = Path.Combine(home, ".config", "protonfixes");
                try
                {
                    Directory.CreateDirectory(protonFixesDir);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not ensure ProtonFixes config dir exists at {Path}", protonFixesDir);
                }
            }

            // Get Proton wrapper path using the detector
            var protonWrapperPath = await _protonDetector.GetProtonWrapperPathAsync();
            if (protonWrapperPath == null)
            {
                throw new InvalidOperationException("No Proton installation found. Please ensure Steam is installed with Proton (Experimental, 10.0, or 9.0)");
            }

            _logger.LogDebug("Using Proton wrapper at {ProtonPath}", protonWrapperPath);

            // Initialize the Proton prefix with wineboot
            // Use valid DISPLAY for GPU access (needed for DXVK/VKD3D initialization)
            var ph = new ProcessHelper
            {
                Path = protonWrapperPath.ToAbsolutePath(),
                Arguments = new object[] { "run", "wineboot", "--init" },
                EnvironmentVariables = GetGpuEnvironmentVariables(_currentPrefix),
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
            
            // CRITICAL: Use valid DISPLAY for GPU-accelerated BC7 compression
            // texconv.exe uses DirectX Compute Shaders which require GPU access via DXVK/VKD3D
            // Setting DISPLAY="" prevents GPU access, causing 26+ hour CPU-only conversions
            // Windows are suppressed via WINEDLLOVERRIDES (conhost.exe, cmd.exe) to prevent
            // 30,000+ focus-stealing windows during large modlist installations
            var envVars = GetGpuEnvironmentVariables(prefix);
            
            return new ProcessHelper
            {
                Path = protonWrapperPath.ToAbsolutePath(),
                Arguments = new object[] { "run", "Tools\\texconv.exe" }.Concat(texConvArgs),
                EnvironmentVariables = envVars,
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
            
            // Use valid DISPLAY for GPU access (texdiag may also use GPU for texture analysis)
            return new ProcessHelper
            {
                Path = protonWrapperPath.ToAbsolutePath(),
                Arguments = new object[] { "run", "Tools\\texdiag.exe" }.Concat(texDiagArgs),
                EnvironmentVariables = GetGpuEnvironmentVariables(prefix),
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
            
            // 7z.exe doesn't need GPU, so we can suppress display to hide windows
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
                    ["DISPLAY"] = "" // 7z doesn't need GPU, so hide windows
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
            _initializationGate.Dispose();
            Cleanup();
        }
    }
}
