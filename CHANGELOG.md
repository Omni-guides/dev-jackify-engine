# Jackify-Engine Changelog

Jackify-Engine is a Linux-native fork of Wabbajack CLI that provides full modlist installation capability on Linux systems using Proton for texture processing.

## Version 0.3.6 - 2024-08-26
### Minor UX Improvements
* **BSA Building Progress**: Fixed multi-line output to use single-line progress display for cleaner console output
* **BSA Progress Counter**: Added BSA counter (3/12) and file count (127 files) to provide better progress feedback
* **SystemParameters Warning**: Suppressed "No SystemParameters set" warning to debug level (only shows with `--debug`)
* **Finished Message**: Changed from "Finished Installation" to "Finished Modlist Installation" for clarity

## Version 0.3.5 - 2024-08-26
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

## Version 0.3.4 - 2024-08-25
### Critical Bug Fixes
* **BSA Building**: Fixed race condition during BSA building by moving directory cleanup outside foreach loop
* **File Extraction**: Resolved file extraction race conditions with improved disposal patterns
* **7zip Extraction**: Reverted to single-threaded extraction to match upstream behavior and fix BSA issues
* **Proton Execution**: Fixed Proton command execution with proper path handling for spaces

### Performance Improvements
* **Texture Processing**: Optimized texconv execution via Proton with proper environment variables
* **Temp Directory Management**: Improved temporary file lifecycle management
* **Resource Settings**: Enhanced resource concurrency configuration for Linux systems

## Version 0.3.3 - 2024-08-24
### Core Functionality
* **Linux Native Operation**: Full Linux compatibility without requiring Wine (except for texconv.exe)
* **Proton Integration**: Complete Proton-based texture processing system
* **Steam Game Detection**: Linux Steam library detection and game path resolution
* **Self-Contained Binary**: 92MB self-contained Linux executable with all dependencies included

### Build System
* **Distribution Package**: 43MB .tar.gz package with complete documentation and tools
* **Cross-Platform Tools**: Included 7zz, innoextract, and texconv.exe for all platforms
* **Automated Build**: Comprehensive build script with dependency checking and validation

## Version 0.3.2 - 2024-08-23
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

## Version 0.3.1 - 2024-08-22
### Early Development
* **Project Setup**: Initial project structure and build system configuration
* **Dependency Management**: .NET 8.0 targeting for optimal Linux compatibility
* **Basic Integration**: Initial Proton integration and Steam detection

## Version 0.3.0 - 2024-08-21
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
