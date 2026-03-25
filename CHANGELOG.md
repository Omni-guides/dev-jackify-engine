# Jackify-Engine Changelog

Jackify-Engine is a Linux-native fork of Wabbajack CLI that provides full modlist installation capability on Linux systems using Proton for texture processing.

## Version 0.5.3 - 2026-03-25
### Bug Fixes
* **NAME_MAX pre-flight check removed**: The filename length pre-flight check introduced in 0.5.0 has been removed. It was causing false positives for users on non-encrypted filesystems, blocking installs that would have succeeded. eCryptFS/fscrypt users will still receive an actionable error if a filename is genuinely too long, caught at the point of failure during install.
* **Google Drive downloads failing with size mismatch**: When Google Drive's warning page HTML did not contain the expected form (file too large to virus-scan), the downloader fell back to the original `drive.google.com/uc` URL which returns ~16 bytes when hit programmatically. The fallback now constructs a direct `drive.usercontent.google.com` URL with `confirm=t`, matching the URL the browser lands on after clicking through the warning.

## Version 0.5.2 - 2026-03-23
### Bug Fixes
* **`--skip-disk-check` flag now recognised**: `OptionDefinition.Aliases` was prepending `-` to an empty short-option string, producing a bare `-` alias that caused `System.CommandLine` to reject the option at registration time. Options with no short alias now emit only the long `--` form. Fixes the "unrecognised argument" error users on 0.5.1 saw when clicking "Continue Anyway" on a disk-full pre-flight error.
* **NAME_MAX pre-flight no longer false-positives on standard filesystems**: The filename length check now only runs when the install filesystem has a reduced NAME_MAX (< 255). Any modlist published on Wabbajack is NTFS-compatible by definition (255-char component limit), so triggering the check on a standard ext4/btrfs/xfs filesystem could only ever be a false positive. The check continues to protect against encrypted home directories (eCryptFS/fscrypt) which reduce NAME_MAX to ~138 chars.
* **Disk space pre-flight checks downloads and install drives separately**: Previously only the install drive was checked against the total installed size, ignoring the downloads drive entirely. Now checks the downloads drive against archive sizes and the install drive against directive sizes independently. If both paths share the same filesystem (e.g. both on internal SSD), requires archives + install to fit together since they coexist during installation. Covers the common Steam Deck case of downloads on SD card and install on internal storage.
* **Archive index lookup errors now actionable**: `InvalidOperationException: Sequence contains no matching element` during `InstallArchives` or BSA verification now emits `validation_failed` (exit 5) with the specific archive and file name, rather than `engine_error` (exit 6) with a bare LINQ exception. Error message tells the user which archive to delete and re-download.

## Version 0.5.1 - 2026-03-15
### Bug Fixes
* **Disk space pre-flight skippable**: Added `--skip-disk-check` flag to bypass the pre-flight disk space check. Intended for update scenarios where the modlist is already installed and only minor changes are expected, making the full-size check a false positive. The structured `disk_full` error (exit 4) still fires normally when the flag is absent; Jackify GUI can offer a "Continue Anyway" dialog and re-launch with the flag.
* **NAME_MAX false positive on Windows-style paths**: Pre-flight filename length check was splitting directive paths on `/` only. Modlist paths use `\` as separator, so the entire path was treated as a single component and falsely triggered the check on any path longer than NAME_MAX chars in total. Now splits on both `/` and `\`.
* **Temp folder cleanup no longer fatal**: `DeleteDirectory()` on BSA and `__temp__` folders at the end of install is now wrapped in try/catch; an `IOException` (e.g. file still locked) logs a warning instead of crashing an otherwise successful installation.

## Version 0.5.0 - 2026-03-13
### Bug Fixes
* **Exception classifier**: Added `StructuredError.Classify(Exception)` — a single shared method that maps exception types to structured error types and messages (`disk_error`, `disk_full`, `permission_denied`, `network_error`, `auth_failed`, `engine_error`). All catch sites in `Install.Run`, `DownloadMachineUrl`, and `UnhandledException` now use the classifier instead of duplicated inline logic or hardcoded `engine_error` fallbacks. Adds new `disk_error` type (exit 4) for OS-level I/O read failures distinct from `disk_full`.
* **`installer.Begin()` exceptions unhandled**: Any exception thrown during installation (I/O errors, permission denied, network failures, etc.) previously escaped to the `UnhandledException` fallback and was always reported as `engine_error` exit 6. Now caught at the call site with proper classification.
* **`PathTooLongException` misclassified**: Previously fell through to the `IOException` → `disk_error` branch in `Classify()`, producing a misleading "failing storage device" message. Now classified as `validation_failed` (exit 5) with a message explaining the filesystem filename length limit and suggesting a non-encrypted install location.
* **Pre-flight validation**: Three checks now run after the modlist loads but before archive downloads begin, preventing failures mid-install or post-download:
  - **Game installed**: `IsInstalled()` checked upfront — previously `GameLocation()` threw outside the try/catch and escaped as a raw stack trace + exit 1.
  - **Filesystem NAME_MAX**: `pathconf()` probed on the install path to detect filesystems with reduced filename length limits (e.g. eCryptFS/fscrypt encrypted home directories). Scans all modlist directive paths and fails immediately with the offending names if any exceed the limit.
  - **Disk space**: Free space on the install drive checked against the sum of directive sizes before downloading begins.

### New Features
* **Manual download protocol**: Engine now communicates with Jackify GUI via a structured JSON stdin/stdout protocol for non-premium users and explicitly-manual archives.
  - Emits `manual_download_required` JSON events to stdout (one per missing file) with `file_name`, `nexus_url`, `expected_hash`, `expected_size`, `mod_name`, `mod_id`, `file_id`, `index`, `total`
  - Emits `manual_download_list_complete` and blocks on stdin waiting for `{"command":"continue"}`
  - On receipt of continue: rescans downloads directory, re-emits any still-missing files (incrementing `loop_iteration`), repeats until all found
  - Emits `manual_download_phase_complete` and continues installation normally once all files are present
  - Replaces the old unstructured log output and install-halting behavior for manual downloads
* **Non-premium Nexus hang fixed**: `NexusDownloader.DownloadManually()` previously blocked forever awaiting a browser download task that CLI mode never resolved. It now throws `ManualDownloadRequiredException` immediately, routing through the protocol instead.
* **Cleaner premium warnings**: Removed the ASCII-art box warnings for non-premium Nexus (Jackify GUI now handles UX for this case).
* **Nexus ToS compliance**: Non-premium accounts now have ALL Nexus archives routed to the manual download protocol upfront, before any download attempt. Premium status is checked once via `NexusApi.IsPremium()` at the start of the download phase; if non-premium, no Nexus CDN URL API call is ever made. The `nexus_url` in `manual_download_required` events now includes `file_id` (e.g. `?tab=files&file_id=462377`) so Jackify can open the browser directly to the correct file's download page.

## Version 0.4.8 - 2026-02-17
### Improvements
* **Remaining download size**: "Downloading Mod Archives" progress line now shows remaining GB (e.g. `(12/87) - 4.2MB/s - 23.1GB remaining`); counter decrements continuously as in-progress bytes are received, not only when an archive finishes
* **Stdout output quality**: Removed spurious blank lines after every section header and progress update; removed `[HH:MM:SS]` elapsed-time prefix from all stdout lines (Jackify adds its own wall-clock timestamp, making the engine prefix produce double-timestamp noise in logs); `[FILE_PROGRESS]` lines written to stdout with newline so Jackify's line-by-line capture sees each line; install-phase heartbeat moved to stderr to avoid duplicating the stdout progress line
* **Progress polling**: Display loops poll every 250ms (was 1000ms) for smoother progress updates
* **GE-Proton / ProtonFixes**: When initializing Proton, the engine now ensures `~/.config/protonfixes` exists.
* **Structured error output**: Engine now emits JSON error lines to stderr on failure, tagged with `"je":"1"` for consumption by the Jackify Python frontend. Each line includes `level`, `type`, `message`, and optional `context` fields. Covers all classified failure paths: `auth_failed`, `premium_required`, `network_error`, `disk_full`, `permission_denied`, `archive_corrupt`, `file_not_found`, `validation_failed`, `engine_error`.
* **Meaningful exit codes**: Exit codes 2–6 now map to specific failure categories (2=auth, 3=network, 4=disk/IO, 5=validation, 6=engine). Exit code 1 retained as fallback for unknown/cancelled paths. Fully backward-compatible — old Jackify versions ignore stderr entirely.
* **Crash handler**: `AppDomain.UnhandledException` handler registered in `Program.Main` ensures a structured `engine_error` line is emitted even for exceptions that escape the CLI pipeline (e.g. DI host construction failures), with exit code 6.

### Bug Fixes
* **Download speed always 0.0MB/s**: `TransferMetrics` rewritten to read NIC byte counters from `/proc/net/dev` on a 500ms timer; default interface detected via `/proc/net/route` (unambiguous default-route lookup). Replaces the previous ring-buffer approach and the earlier `BandwidthMonitor` class that picked the wrong interface on multi-NIC systems.
* **ZIP extraction with Cyrillic filenames**: Added Cyrillic to `ProblematicChars` so archives with Cyrillic filenames (e.g. Soule-like Music, BottleRim) are routed to Proton 7z.exe instead of Linux 7zz, fixing "N results, expected M" sanity check failures
* **Stale OAuth token blocks fresh session**: When an encrypted OAuth token file fails to refresh (e.g. token revoked after re-auth in Jackify GUI), the stale file is now deleted and the engine falls through to the `NEXUS_OAUTH_INFO` env var containing the fresh token. Previously a stale file permanently blocked a freshly-authenticated session.

## Version 0.4.7 - 2026-01-28
### Critical Bug Fixes
* **ModOrganizer.ini Path Quoting**: Fixed incorrect quoting/escaping of MO2 `customExecutables` by writing clean, unquoted Proton `Z:\...` paths in `ModOrganizer.ini`

## Version 0.4.6 - 2026-01-12
### Critical Bug Fixes
* **.wabbajack Download Source Array Bounds**: Fixed "Source array was not long enough" exception during .wabbajack file downloads
  - Added source array bounds checking in `AChunkedBufferingStream.ReadAsync` to prevent underflow when `chunkOffset >= chunkData.Length`
  - Completes the incomplete fix from v0.4.1 which only addressed destination buffer bounds
* **Foreign Character Detection False Positives**: Fixed false positive foreign character detection causing 7z and RAR archives to incorrectly route to Proton 7z.exe extraction
  - Restricted proactive foreign character detection to skip 7z archives (UTF-8 native, safe with Linux 7zz)
  - ZIP and RAR archives still checked proactively for encoding issues
  - Added diagnostic logging to show which files and characters trigger detection
  - Exit code 2 and sanity check fallbacks still work for all archive types (reparse points, symlinks, missed encoding issues)
  - Fixes installation failures where 7z archives that extract fine with Linux 7zz were incorrectly routed to Proton and failed
* **Nexus OAuth Token Refresh**: Rectified incorrect Nexus Client ID references
* **DirectoryNotFoundException for meta.ini Files**: Fixed installation failures during "Installing Included Files" phase when writing meta.ini files to directories with case-sensitive name mismatches
  - Regression in `CreateDirectory()` was returning early when a case-insensitive directory match was found, preventing creation of the exact-case directory needed on Linux
  - Removed early return in `AbsolutePathExtensions.CreateDirectory()` to ensure exact-case directories are created even when case-insensitive matches exist
  - Added explicit parent directory creation and verification in `StandardInstaller.InstallIncludedFiles()` before writing inline files
  - Fixes failures like "Could not find a part of the path '/path/to/Additional Dremora Faces/meta.ini'" when directory exists with different case (e.g., "Additional Dremora faces")

## Version 0.4.5 - 2025-01-02
### Critical Bug Fixes
* **Archive Extraction with Backslashes**: Fixed regression where files with backslashes in filenames weren't being converted to directory structure when extracted via Proton 7z.exe
  - Added post-processing call to `GatheringExtractWithProton7Zip` to fix backslashes (Linux 7zz path already had this)
* **Data Directory Path**: Fixed `TemporaryFileManager` using hardcoded lowercase `~/jackify` instead of honoring user-configured data directory
  - Now reads `jackify_data_dir` from `~/.config/jackify/config.json` (inlined to avoid circular dependency)
  - Falls back to `~/Jackify` (capitalized) if config read fails, instead of creating `~/jackify` (lowercase)
* **.wabbajack File Hash Validation**: Removed post-download hash validation to match upstream Wabbajack behavior
  - Upstream Wabbajack only validates hash before download (to check if file exists), not after download
  - File integrity is still checked when loading the modlist (will fail if corrupted)
  - Fixes installation failures when metadata hash doesn't match actual file hash (rare edge case)
* **Pattern Matching for Archives with Backslashes**: Fixed extraction failures for archives containing files with backslash path separators
  - Updated `GetPatterns` to include both backslash and forward-slash variants (matches upstream Wabbajack's `AllVariants`)
  - 7zip pattern matching is literal and requires exact separator matches
  - Fixes cases where Linux 7zz extracted 0 files because forward-slash patterns didn't match backslash paths in archive
  - Negligible performance impact (<0.1s overhead for 40-minute installation)
* **.wabbajack Download Progress Tracking**: Fixed `System.ArgumentException: Destination array was not long enough` error during .wabbajack file downloads
  - Replaced `ConcurrentQueue<T>` with `Queue<T>` and added proper locking for thread-safe access
  - Prevents race condition when `ToArray()` is called while queue is being modified concurrently
  - Matches the working pattern used in `DownloadModlist.cs`

## Version 0.4.4 - 2025-01-XX

### Improvements
* **Proton Detection**: Now parses `libraryfolders.vdf` to find Proton in additional Steam library folders
* **Nexus OAuth Logging**: Enhanced validation logging (actual bug fixed in Jackify v0.2.0.5)

## Version 0.4.3 - 2025-12-21
### Improvements
* **Improved Error Logging**: Exception messages now display user-friendly error descriptions instead of technical stack traces
  - User-friendly error messages are always shown (no `--debug` flag required)
  - Technical messages like "Sequence contains no matching element" are translated to clear explanations
  - File paths, hashes, and context are extracted and displayed prominently
  - Stack traces and technical details (exception type, original message) are hidden by default, shown only with `--debug` flag
  - Makes errors much more understandable for non-technical users
* **Destination Case-Sensitivity Hotfix**: Prevent installation failures when `mods/` subdirectories differ only by case
  - When installing files from archives, `AInstaller` now reuses an existing directory whose name matches case-insensitively (e.g. `translations/` vs `Translations/`, `Scripts/` vs `scripts/`)
  - Only applies when the original destination parent directory does not exist; successful installs are unchanged
  - Fixes update failures for large lists like Lorerim without requiring a full reinstall or re-download

### Critical Bug Fixes
* **Game File Download Case Sensitivity**: Fixed "Unable to download" errors for game files (Fallout4.exe, Data files, etc.) when modlist uses different casing than actual files
  - Added case-insensitive file lookup in `GameFileDownloader` using existing `FindCaseInsensitive()` method
  - Fixes: Modlist has `fallout4.exe` but actual file is `Fallout4.exe` on Linux filesystem
  - Affects: FO4 modlists like Anomaly that require game file downloads
  - Root cause: Linux filesystems are case-sensitive, modlists compiled on Windows use lowercase paths
* **Archive Extraction Pattern File Explosion**: Eliminated exponential case variant generation causing multi-GB pattern files and installation failures
  - OLD: AllVariants() generated 3^N case combinations per path (N = directory depth), creating up to 531,441 pattern lines per deeply nested file
  - User-reported symptom: 3.5GB pattern file found in temp directory, matching calculated 4.5GB for 4,808 files
  - NEW: Simple path separator variants (3 lines per file) + 7zip `-ssc-` flag for case-insensitive matching
  - Reduction: 2.5 billion pattern lines → 14,424 lines (173,000x smaller), 4.5GB → 2MB pattern file
  - Fixes: Installation crashes, out-of-memory exceptions, and "exit code 7" errors during archive extraction
  - Regression introduced in v0.4.1 when AllVariants() recursive case generation was added
* **File Installation Case Sensitivity**: Fixed DirectoryNotFoundException during file installation when directory case mismatches
  - Path normalization now happens BEFORE CreateDirectory() instead of catching exception after
  - Fixes: modlist directive says `scripts/` but `Scripts/` exists on disk → use existing `Scripts/` directory
  - Prevents creation of duplicate directories with different cases
  - Applies to both ExtractedNativeFile and ExtractedMemoryFile code paths

## Version 0.4.1 - 2025-12-19
### Critical Bug Fixes
* **Archive Extraction Case Sensitivity**: Fixed extraction failures when 7zip normalizes mixed-case archive paths (e.g., `Fairy/` → `fairy/`)
  - Added 7zip `-ssc-` flag and recursive path variant generation for case-insensitive matching
  - Fixes "Sanity check error extracting...0 results, expected N" failures
* **Download Reliability**: Fixed "Source array was not long enough" exception in chunked download buffering causing random download failures
  - Fixed incorrect bitwise XOR operator (should be bitwise AND with inverted mask) in chunk offset calculation
  - Added missing Position increment after array copy
  - Added destination buffer bounds checking to prevent "Destination array was not long enough" errors
* **BSA Case Sensitivity**: Added case-insensitive path matching for BSA building on Linux (handles "scripts/hardcore" vs "scripts/Hardcore" mismatches)
* **Meta File Write Failures**: Meta file creation failures no longer abort completed installations (meta files are non-critical post-install metadata)

### Performance Improvements
* **Meta File Optimization**: Skip writing .meta files that already exist, avoiding redundant hash operations
* **Progress Tracking**: Thread-safe progress reporting with ConcurrentQueue for .wabbajack downloads

### Upstream Updates
* **Google Drive Downloads**: Fixed handling of direct download links (application/octet-stream content type) from Google Drive
* **Multi-Game File Sourcing**: Added support for sourcing game files from multiple game folders (e.g., Fallout: New Vegas can source files from Fallout 3)

## Version 0.4.0 - 2025-12-06
### GUI Integration
* Added `--json` flag to `list-modlists` command for structured metadata output
* New `download-modlist-images` command for batch image download
* Added `--show-file-progress` flag for detailed progress tracking

### GPU Acceleration
* GPU-accelerated texture conversion via Proton/DXVK
* Automatic NVIDIA and AMD dGPU detection and configuration

## Version 0.3.19 - 2025-11-13
### Download Reliability Improvements
* **Resume Support**: Fixed download restart issue - partial files are now preserved on timeout, allowing resume instead of restart
* **Exception Handling**: Matched upstream Wabbajack behavior - TaskCanceledException (timeouts) now properly handled to allow silent resume
* **Hash Mismatch Logic**: Empty hash (incomplete downloads) no longer triggers file deletion - only actual hash mismatches delete files
* **Partial File Exclusion**: Partial downloads are excluded from hash checking (only exact size matches are hashed) to prevent false hash mismatches

### User Experience Improvements
* **BSA Progress Messages**: Building/Writing/Verifying BSA messages now overwrite the same line instead of creating new lines for cleaner output
* **Progress Line Clearing**: Progress line is properly cleared after BSA building completes

### Archive Extraction Error Messages
* **Improved Error Reporting**: Better error messages when both Linux 7zz and Proton 7z.exe fail to extract archives
* **User Guidance**: Clear instructions to manually delete corrupted archives and re-run installation
* **Synthesis.zip Note**: Specific guidance for archives like Synthesis.zip that use the same filename across versions

## Version 0.3.18 - 2025-11-05
### Archive Extraction Fix
* **Proton Fallback**: Exit code 2 (fatal errors) now automatically retries with Proton 7z.exe for ZIP/7Z/RAR archives
* **Reparse Point Support**: Fixes archives containing Windows symlinks/reparse points that Linux 7zz cannot handle
* **Error Reporting**: Fixed 7zip stderr capture showing type names instead of actual error messages

### Bandwidth Limiting (Throughput) Fix
* **Effective Throttling**: Fixed download pipeline not honoring `MaxThroughput` from `resource_settings.json`
* **Root Cause**: Downloader reported progress with `ReportNoWait`, bypassing the limiter
* **Implementation**: Now reports byte deltas via throttling-aware `Report(...)` and flushes remaining bytes at completion
* **Scope**: Applies to all HTTP downloads using the resumable downloader

### Nexus Premium Error Messages
* **Clear Non-Premium Detection**: Added prominent console error message when Nexus Premium is not detected (before downloads start)
* **User Guidance**: Explains why downloads appear stuck at 0MB/s and provides options (purchase Premium or continue with manual downloads)
* **403 Forbidden Handling**: Improved error message for 403 responses (edge case: Premium status changed or API rejection) to clearly indicate Premium requirement
* **Single Warning**: Shows the Premium warning only once per session to avoid console spam

### Download Failure Messaging
* **Concise Errors**: Per-file failures now show archive name, source, and a usable URL without dumping stack traces
* **Debug Detail**: Full exception details moved to debug logs for troubleshooting
* **Non-Blocking**: Installer continues remaining downloads; failures are summarized at the end

## Version 0.3.17 - 2025-10-09
### Google Drive Downloads
* **Request URI Fix**: Fixed "Request URI is null" error in Google Drive downloader when form parsing fails or response isn't HTML
* **Fallback Logic**: Added proper fallback to direct download URL when Google Drive form detection fails

### User Experience
* **Progress Indication**: Added progress indicators to "Hashing downloads" and "Looking for unmodified files" phases that previously showed no progress during large operations

## Version 0.3.16 - 2025-09-30
### Archive Extraction
* **Sanity Check Fallback**: Added Proton 7z.exe fallback for case sensitivity extraction failures

### Download Command
* **download-wabbajack-file**: Added CLI command to download .wabbajack files by machineURL

### Texture Processing
* **Enhanced Error Messages**: Improved texconv/texdiag error messages to include original texture file names and conversion parameters

### Logging
* **Noise Reduction**: Suppress verbose "HttpMessageHandler cleanup" console logs in non-debug runs

## Version 0.3.15 - 2025-09-18
### .wabbajack File Hash Verification Fix
* **Hash Cache Issue**: Fixed corrupted .wabbajack files being used due to stale hash cache entries
* **Fresh Hash Verification**: .wabbajack files now always verify actual file hash instead of using cached values

### ModOrganizer.ini Path Handling Fix
* **Spaces in Directory Names**: Fixed Wine path conversion to properly quote paths containing spaces

### Proton Selection and Window Suppression (Linux)
* **Config-Aware Proton**: Reads `proton_path` from `~/.config/jackify/config.json` and uses `${proton_path}/proton`
* **Detection Order**: GE-Proton10-* (compatibilitytools.d) → Proton - Experimental → Proton 10.0 → Proton 9.0
* **No Wine Fallback**: Proton is now required; if not found, a clear error is logged
* **Window Suppression**: Suppresses Proton console windows via `DISPLAY=""`, `WAYLAND_DISPLAY=""`, and `WINEDLLOVERRIDES="msdia80.dll=n;conhost.exe=d;cmd.exe=d` for texconv/texdiag/7z and prefix init
* **Debug Trace**: With `--debug`, logs a single line indicating Proton source (config/GE/Valve version)

### Archive Extraction Encoding Fix
* **Foreign Character Coverage**: Added degree symbol `°` to the special character list used to trigger the Proton 7z.exe fallback
* **User Report**: Handles files like `Mirror°.nif` correctly by routing extraction through Proton 7z.exe when needed

## Version 0.3.14 - 2025-09-15
### Nexus API Error Handling Improvements
* **404 Not Found Handling**: Added specific error messages for missing/removed Nexus mods with actionable user guidance
* **Enhanced Error Logging**: Improved error reporting with ArchiveName, Game, ModID, and FileID context for better troubleshooting
* **Manual Download Logic**: Removed inappropriate 404 fallback to manual downloads (file doesn't exist) while preserving 403 Forbidden fallback for auth issues

### Configurable Data Directory
* **Shared Config Integration**: jackify-engine now reads `jackify_data_dir` from `~/.config/jackify/config.json`
* **Replaced Hardcoded Paths**: All `~/Jackify/*` usages now use the configured data directory (prefixes, logs, temp, `.engine`, `downloaded_mod_lists`)
* **Complete Path Migration**: All application data now uses the configurable directory including caches (`GlobalHashCache2.sqlite`, `GlobalVFSCache5.sqlite`, `VerificationCacheV3.sqlite`, `PatchCache/`), encrypted data, image cache, and token storage.
* **Safe Fallback**: If config read fails, defaults to `~/Jackify`

## Version 0.3.13 - 2025-09-13 (STABLE)
### Wine Prefix Cleanup & Download System Fixes
* **Wine Prefix Cleanup**: Implemented automatic cleanup of ~281MB Wine prefix directories after each modlist installation
* **Manual Download Handling**: Fixed installation crashes when manual downloads are required - now stops cleanly instead of continuing with KeyNotFoundException
* **Download Error Messaging**: Enhanced error reporting with detailed mod information (Nexus Game/ModID/FileID, Google Drive, HTTP sources) for better troubleshooting
* **GoogleDrive & MEGA Integration**: Fixed download regressions and integrated with Jackify's configuration system
* **Creation Club File Handling**: Fixed incorrect re-download attempts for Creation Club files with hash mismatches (e.g., Curios case sensitivity issues)
* **BSA Extraction Fix**: Fixed DirectoryNotFoundException during BSA building by ensuring parent directories exist before file operations
* **Resource Settings Compliance**: Fixed resource settings not being respected by adding missing limiter parameters to VFS and Installer operations
* **VFS KeyNotFoundException Crash**: Fixed crashes during "Priming VFS" phase when archives are missing - now catches missing archives before VFS operations and provides clear error guidance

### Technical Implementation
* **ProtonPrefixManager**: Implemented IDisposable pattern with automatic cleanup after texture processing
* **TexConvImageLoader**: Added proper disposal to trigger prefix cleanup
* **StandardInstaller**: Added cleanup call after installation completion
* **IUserInterventionHandler**: Added HasManualDownloads() method to all intervention handler classes
* **Download Error Context**: Created GetModInfoFromArchive method to extract source information for failed downloads
* **MEGA Token Provider**: Modified to use Jackify config directory (~/.config/jackify/encrypted/) while maintaining upstream security
* **EncryptedJsonTokenProvider**: Made KeyPath virtual to allow path override for Jackify integration
* **GameFileSource Filter**: Excluded GameFileSource files from re-download logic to prevent incorrect re-download attempts for locally sourced files
* **ExtractedMemoryFile**: Added parent directory creation in Move method to prevent DirectoryNotFoundException during BSA extraction
* **VFS Context**: Added missing Limiter parameter to PDoAll calls to respect VFS MaxTasks setting from resource_settings.json, and defensive error handling in BackfillMissing for missing archives
* **StandardInstaller**: Added missing Limiter parameter to InlineFile processing to respect Installer MaxTasks setting, and moved missing archive detection earlier to prevent VFS crashes

### Bug Fixes
* **GoogleDrive Downloads**: Fixed 'Request URI is null' error and added handling for application/octet-stream content type responses
* **Manual Download Flow**: Installation now returns InstallResult.DownloadFailed when manual downloads are required instead of crashing
* **Error Context**: Generic 'Http Error NotFound' messages now include specific file name, source, and original error details
* **MEGA Compatibility**: Restored upstream MEGA behavior while integrating with Jackify's configuration system
* **Creation Club File Re-download**: Fixed incorrect re-download attempts for Creation Club files with hash mismatches - now properly handled as locally sourced files
* **BSA Building DirectoryNotFoundException**: Fixed crashes during BSA building when parent directories don't exist - now creates directories automatically
* **Resource Settings Ignored**: Fixed VFS and Installer operations ignoring configured MaxTasks settings - now properly respects resource_settings.json limits
* **VFS Priming KeyNotFoundException**: Fixed KeyNotFoundException crashes with missing archive hashes (e.g., 'MXmKeWd+KkI=') during VFS priming - now detects missing archives early with clear error messages

### User Experience
* **Disk Space Management**: No more accumulation of Wine prefix directories consuming hundreds of MB per installation
* **Clean Error Handling**: Manual download requirements now show clear summary instead of stack traces
* **Better Troubleshooting**: Download failures now show exactly which mod/file failed and where it came from
* **Configuration Integration**: MEGA tokens properly stored in Jackify's config directory structure
* **Resource Control**: Users can now properly control CPU usage during installation by modifying resource_settings.json (VFS, Installer, File Extractor MaxTasks)

## Version 0.3.11 - 2025-09-07 (STABLE)
### Proton Path Detection Fix - Dynamic System Compatibility
* **Fixed Hardcoded Paths**: Replaced hardcoded `/home/deck` Proton paths in 7z.exe fallback with dynamic detection
* **Cross-System Compatibility**: 7z.exe fallback now works on any Linux system regardless of Steam/Proton installation location
* **Reused Existing Logic**: Leverages proven `ProtonDetector` class used by texconv.exe instead of duplicating path detection
* **Error Handling**: Added proper null checks and error reporting when Proton installation is not found
* **Foreign Character Support**: Maintains 7z.exe fallback functionality for archives with Nordic, Romance, and Slavic characters

## Version 0.3.10 - 2025-09-01 (STABLE)
### HTTP Rate Limiting Fix - Download Stall Resolution
* **Configurable Resource Settings**: Fixed hardcoded 4 concurrent HTTP requests causing 20-30s download stalls
* **Upstream Compatibility**: Now uses `GetResourceSettings("Web Requests")` matching upstream Wabbajack behavior
* **Dynamic Concurrency**: Automatically scales to `Environment.ProcessorCount` (typically 8-16) concurrent requests
* **Download Performance**: Eliminates .wabbajack file download stalls and improves overall download reliability
* **Resource Management**: Both HTTP client and Wabbajack client now use configurable settings instead of hardcoded limits

### Critical Disk Space Fix - Temporary File Cleanup
* **__temp__ Directory Cleanup**: Fixed critical bug where 17GB+ temporary files were left behind after installation
* **Automatic Cleanup**: Added proper cleanup of `__temp__` directory containing extracted modlist files and processing artifacts
* **Professional Progress Display**: Added "=== Removing Temporary Files ===" section with progress indicators
* **Disk Space Recovery**: Eliminates accumulation of temporary files that could consume hundreds of GB over multiple installations
* **Upstream Compatibility**: Matches upstream Wabbajack's approach to temporary file management

### Foreign Character Archive Extraction Fix
* **ZIP Encoding Detection**: Added comprehensive foreign character detection for ZIP archives (Nordic: ö,ä,ü; Romance: á,é,í; Slavic: ć,č,đ; Other: ß,þ,ð)
* **Proton 7z.exe Fallback**: Automatic fallback to Windows 7z.exe via Proton for archives with problematic character encodings
* **Zero Overhead Design**: Normal archives (99.99%) continue using fast Linux 7zz extraction
* **Path Resolution Fix**: Fixed KnownFolders.EntryPoint path construction causing double "publish" directory issue
* **Archive Format Intelligence**: 7Z archives work perfectly (UTF-8 native), ZIP archives with encoding issues automatically use Proton
* **Root Cause Resolution**: Solves Linux 7zz filename corruption (ö →) while preserving performance

### Manual Download System & Error Handling Improvements
* **Manual Download Detection**: Complete system for detecting and handling files requiring manual download
* **User-Friendly Summary**: Prominent boxed header with clear instructions and numbered list of required downloads
* **Hash Mismatch Clarity**: Specific error messages distinguish between corrupted files and download failures
* **Automatic Cleanup**: Corrupted files automatically deleted with clear guidance on cause
* **Better Error Messages**: More helpful final summaries with possible causes and specific counts

### Technical Implementation
* **Foreign Character Detection**: Added ProblematicChars HashSet with comprehensive international character coverage
* **Proton 7z.exe Integration**: Implemented RunProton7zExtraction method with proper environment variable setup
* **Archive Format Routing**: Smart detection routes ZIP archives with foreign chars to Proton, others to Linux 7zz
* **Path Resolution Fix**: Corrected KnownFolders.EntryPoint usage to prevent double directory paths
* **Fallback Detection**: Safety net for missed encoding issues with suspicious character logging
* **Resource Settings Integration**: Updated ServiceExtensions to use configurable resource settings for all HTTP operations
* **Temporary File Management**: Added proper cleanup of `__temp__` directory in installation flow
* **Progress Integration**: Integrated cleanup into existing progress system with proper step counting
* **CLIUserInterventionHandler**: New handler that collects manual downloads without blocking installation
* **ManualDownloadRequiredException**: Custom exception for clean manual download signaling
* **Hash Mismatch Detection**: Enhanced error handling to distinguish file corruption from network issues
* **Error Message Filtering**: Manual downloads excluded from generic "Unable to download" errors
* **Upstream Compatibility**: Matches upstream Wabbajack's approach to hash mismatch handling

### User Experience
* **Download Reliability**: No more 20-30 second stalls during .wabbajack file downloads
* **Disk Space Efficiency**: No more wasted disk space from leftover temporary files
* **Clear Action Items**: Step-by-step numbered list of required downloads with exact URLs
* **No Confusion**: Clear distinction between manual downloads, hash mismatches, and network failures


## Version 0.3.8 - 2025-08-30 (STABLE)
### Critical Archive Compatibility Fix
* **ZIP Encoding Support**: Fixed sanity check errors for ZIP archives containing non-ASCII filenames (international characters)
* **InfoZIP Integration**: Added bundled unzip binary with -UU flag for proper raw byte handling of filenames
* **Encoding Robustness**: Added error handling for file enumeration failures with corrupted UTF-8 sequences
* **International Character Support**: Successfully extracts archives with Cyrillic, accented, and other international characters
* **Self-Contained Distribution**: Maintains zero external dependencies by bundling unzip with existing extractors

### Technical Implementation
* **Linux ZIP Detection**: Automatically uses unzip instead of 7zz for .zip files on Linux to preserve filename encoding
* **Raw Byte Preservation**: Uses unzip -UU flag to handle filenames as raw bytes, matching Windows filesystem behavior
* **Graceful Error Recovery**: File enumeration failures are caught and logged rather than causing installation crashes
* **Backward Compatibility**: No impact on existing ZIP archives that were working correctly with 7zz

### Verified Working Archives
* **International Music Mods**: Successfully extracts archives with Cyrillic filenames (Tavernmаirseаil.xwm, nd10_himinbjörg.xwm)
* **All Previous Archives**: Maintains compatibility with existing ASCII and UTF-8 encoded ZIP files
* **Mixed Character Sets**: Handles archives with combination of ASCII and international characters

## Version 0.3.7 - 2025-08-29 (STABLE)
### Critical Stability Fixes - Production Ready
* **Archive Extraction Case Sensitivity**: Fixed extraction failures for archives containing "Textures" vs "textures" directory case mismatches
* **Download Retry Reliability**: Fixed HttpRequestMessage reuse bug causing "already sent" exceptions during download retries
* **HttpIOException Handling**: Fixed "response ended prematurely" network errors now properly retry instead of failing
* **OMOD Extraction for Oblivion**: Fixed Linux path handling where OMOD files were created with backslashes in filenames
* **Hash Validation**: Fixed AAAAAAAAAAA= hash calculation errors for zero-length or corrupted downloads
* **Proton Path Conversion**: Automatic conversion of Linux paths to Proton-compatible Windows paths (Z: drive) in ModOrganizer.ini
* **Clean Output**: Suppressed debug messages (EXTRACTION DEBUG, POST-EXTRACTION) to debug level only

### Verified Working Modlists
* **CSVO - Optimized Follower**: Complete installation with 1,372 files and 1,366 successful extractions
* **SME (Skyrim Modding Essentials)**: Full download and installation success
* **APW Oblivion**: Full installation success with OMOD extraction working correctly
* **Archive Extraction**: Successfully handles both case-sensitive ("Textures") and case-insensitive ("textures") archives

### Technical Implementation
* **AllVariants Enhancement**: Added case variations for common directory names (textures/Textures, meshes/Meshes, sounds/Sounds, etc.)
* **CloneHttpRequestMessage**: New utility method to properly clone HttpRequestMessage objects for reliable retries
* **HttpIOException Retry**: Added HttpRequestException to download retry catch block for network interruptions
* **OMOD Post-Processing**: Added MoveFilesWithBackslashesToSubdirs method to fix Linux OMOD extraction
* **Hash Calculation Fix**: Added finalHash == 0 fallback to prevent invalid hash generation
* **Hash Cache Validation**: Filter out AAAAAAAAAAA= cache entries to force proper recalculation

### Performance & Reliability
* **Download Success Rate**: Significantly improved reliability for files requiring retry attempts (Synthesis.zip, large archives)
* **Installation Speed**: Maintains or exceeds upstream Wabbajack performance via Proton
* **Memory Management**: Stable operation with large modlists (1,000+ files)
* **Error Recovery**: Robust handling of network interruptions and temporary failures

## Version 0.3.6 - 2025-08-26
### Professional Bandwidth Monitoring System
* **Network Interface Monitoring**: Implemented system-level bandwidth monitoring using actual network interface statistics
* **5-Second Rolling Window**: Professional-grade bandwidth calculation with smooth averaging (matches Steam/browser standards)
* **1-Second UI Updates**: Responsive progress display that updates every second for optimal user experience
* **Accurate Speed Display**: Shows real network utilization (e.g., "5.9MB/s") based on actual bytes transferred
* **Concurrent Download Support**: Properly measures combined throughput from multiple simultaneous downloads

### Minor UX Improvements
* **BSA Building Progress**: Fixed multi-line output to use single-line progress display for cleaner console output
* **BSA Progress Counter**: Added BSA counter (3/12) and file count (127 files) to provide better progress feedback
* **SystemParameters Warning**: Suppressed "No SystemParameters set" warning to debug level (only shows with `--debug`)
* **Finished Message**: Changed from "Finished Installation" to "Finished Modlist Installation" for clarity

### Technical Implementation
* **BandwidthMonitor Class**: New professional monitoring system that samples network interface every 500ms
* **Primary Interface Detection**: Automatically detects main internet connection for accurate measurements
* **Thread-Safe Operation**: Concurrent access handling with proper cleanup and resource management
* **Sanity Checking**: Prevents unrealistic bandwidth values with reasonable maximum limits

## Version 0.3.5 - 2025-08-26
### Major UX Overhaul
* **Console Output System**: Complete redesign of progress reporting with single-line updates and timestamps
* **Progress Indicators**: Added duration timestamps to all operations for better user feedback
* **Download Progress**: Enhanced download speed display and progress reporting
* **Texture Processing**: Improved progress reporting during texture conversion operations
* **Build System**: Enhanced build script with better dependency checking and distribution packaging
* **Linux Compatibility**: Full Linux Steam game detection and path handling improvements

### Technical Improvements
* **Resource Concurrency**: Fixed VFS concurrency limits to restore proper performance
* **File Extraction**: Resolved race conditions and improved temp directory handling
* **Proton Integration**: Enhanced Proton window hiding and command execution
* **7zip Integration**: Optimized extraction parameters for Linux compatibility

## Version 0.3.4 - 2025-08-25
### Critical Bug Fixes
* **BSA Building**: Fixed race condition during BSA building by moving directory cleanup outside foreach loop
* **File Extraction**: Resolved file extraction race conditions with improved disposal patterns
* **7zip Extraction**: Reverted to single-threaded extraction to match upstream behavior and fix BSA issues
* **Proton Execution**: Fixed Proton command execution with proper path handling for spaces

### Performance Improvements
* **Texture Processing**: Optimized texconv execution via Proton with proper environment variables
* **Temp Directory Management**: Improved temporary file lifecycle management
* **Resource Settings**: Enhanced resource concurrency configuration for Linux systems

## Version 0.3.3 - 2025-08-24
### Core Functionality
* **Linux Native Operation**: Full Linux compatibility without requiring Wine (except for texconv.exe)
* **Proton Integration**: Complete Proton-based texture processing system
* **Steam Game Detection**: Linux Steam library detection and game path resolution
* **Self-Contained Binary**: 92MB self-contained Linux executable with all dependencies included

### Build System
* **Distribution Package**: 43MB .tar.gz package with complete documentation and tools
* **Cross-Platform Tools**: Included 7zz, innoextract, and texconv.exe for all platforms
* **Automated Build**: Comprehensive build script with dependency checking and validation

## Version 0.3.2 - 2025-08-23
### Initial Linux Port
* **Base Fork**: Created from upstream Wabbajack with minimal Linux compatibility changes
* **Proton Detection**: Automatic Steam Proton installation detection (Experimental, 10.0, 9.0)
* **Path Handling**: Linux-specific path conversion and Steam library parsing
* **Texture Processing**: Proton-wrapped texconv.exe execution for texture conversion

### Technical Foundation
* **File System Compatibility**: Linux case-sensitivity handling and path separator fixes
* **Archive Extraction**: Cross-platform 7zip integration with Linux-specific optimizations
* **Game Detection**: Linux Steam VDF parsing for multiple library locations
* **Error Handling**: Linux-specific error handling and logging improvements

## Version 0.3.1 - 2025-08-22
### Early Development
* **Project Setup**: Initial project structure and build system configuration
* **Dependency Management**: .NET 8.0 targeting for optimal Linux compatibility
* **Basic Integration**: Initial Proton integration and Steam detection

## Version 0.3.0 - 2025-08-21
### Initial Release
* **Fork Creation**: Initial fork from upstream Wabbajack
* **Linux Targeting**: Basic Linux compatibility setup
* **Proton Foundation**: Initial Proton integration framework

---

## Project History

Jackify-Engine represents the **10th attempt** at creating a Linux-native Wabbajack fork. Previous attempts were destroyed by AI agents making unnecessary modifications to core systems that worked perfectly in upstream Wabbajack.

### Key Principles
* **Minimal Changes**: Only essential Linux compatibility modifications
* **Upstream Compatibility**: 1:1 identical output hashes to upstream Wabbajack
* **Performance Parity**: Target ≤39 minutes installation time (matching upstream via Wine)
* **Self-Contained**: No external dependencies required for published binary

### Critical Systems (Never Modified)
* Archive extraction logic (Wabbajack.FileExtractor)
* Resume functionality and temp directory handling
* VFS (Virtual File System) operations
* Core installation flow (StandardInstaller.Begin)
* 7zip integration and error handling

### Linux-Specific Features
* **Proton Integration**: Uses Steam Proton for texconv.exe execution
* **Linux Steam Detection**: Automatic detection of Linux Steam installations
* **Path Handling**: Linux-specific path conversion and case sensitivity handling
* **Build System**: Linux-optimized build script and distribution packaging

---

## Compatibility

* **Modlist Compatibility**: 100% compatible with upstream Wabbajack modlists
* **Output Verification**: Identical file hashes to Windows Wabbajack installations
* **Performance**: Matches or exceeds upstream Wabbajack performance
* **Platform Support**: Linux x64 (tested on Steam Deck, Nobara, Ubuntu)

## Requirements

* **System**: Linux x64 with .NET 8.0 runtime
* **Steam**: Steam installation with Proton (Experimental, 10.0, or 9.0)
* **Storage**: Sufficient space for modlist installation and temporary files
* **Network**: Internet connection for mod downloads

## Usage

```bash
# Install a modlist
./jackify-engine install -o /path/to/install -d /path/to/downloads -m ModlistName/ModlistName

# List available modlists
./jackify-engine list-modlists

# List detected games
./jackify-engine list-games

# Enable debug logging
./jackify-engine --debug install [args]
```
