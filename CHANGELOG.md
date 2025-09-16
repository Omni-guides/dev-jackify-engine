# Jackify-Engine Changelog

Jackify-Engine is a Linux-native fork of Wabbajack CLI that provides full modlist installation capability on Linux systems using Proton for texture processing.

## Version 0.3.14 - 2025-01-XX (DEVELOPMENT)
### Nexus API Error Handling Improvements
* **404 Not Found Handling**: Added specific error messages for missing/removed Nexus mods with actionable user guidance
* **Enhanced Error Logging**: Improved error reporting with ArchiveName, Game, ModID, and FileID context for better troubleshooting
* **Manual Download Logic**: Removed inappropriate 404 fallback to manual downloads (file doesn't exist) while preserving 403 Forbidden fallback for auth issues

### Configurable Data Directory
* **Shared Config Integration**: jackify-engine now reads `jackify_data_dir` from `~/.config/jackify/config.json`
* **Replaced Hardcoded Paths**: All `~/Jackify/*` usages now use the configured data directory (prefixes, logs, temp, `.engine`, `downloaded_mod_lists`)
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
* **Root Cause Resolution**: Solves Linux 7zz filename corruption (ö → �) while preserving performance

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
