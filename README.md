# Jackify Engine

A Linux-native fork of [Wabbajack](https://github.com/wabbajack-tools/wabbajack) (specifically wabbajack-cli.exe) that provides full modlist installation capability on Linux systems using Proton for texture processing.

## Overview

Jackify Engine is a minimal Linux port of Wabbajack that:
- Targets .NET 8.0 for optimal Linux compatibility
- Uses Steam Proton for texture processing (texconv.exe)
- Maintains upstream performance 
- Achieves identical output hashes to Windows Wabbajack
- Requires no system Wine installation

## Source Code Availability

This repository contains the source code for Jackify Engine, a Linux-native fork of Wabbajack. The source code is made available to comply with the upstream Wabbajack licensing requirements.

**Note**: Jackify Engine is designed to be used as part of the larger Jackify application ecosystem, not as a standalone tool. For end-user installation and usage instructions, please refer to the main Jackify application documentation.

## Key Features

### Linux Compatibility
- **Steam Proton Integration**: Automatically detects and uses your Steam Proton installation for texture processing
- **Cross-System Compatibility**: Works on any Linux system regardless of Steam/Proton installation location
- **Linux File System**: Proper handling of case sensitivity and path separators

### Performance & Reliability
- **Upstream Performance**: Maintains similar performance to running Wabbajack via Proton
- **Temporary File Management**: Automatic cleanup prevents disk space accumulation

### User Experience
- **Human-Readable Modlist Names**: Display actual modlist titles instead of machineURL when using list-installed command
- **Additional Filtering**: Filter by game, author, or search terms
- **Manual Download handling**: Clear instructions for files requiring manual download (not automated)

## Technical Implementation

### Proton-Based Texture Processing
- Uses upstream `texconv.exe` via Steam Proton for identical texture conversion results
- Automatic Proton prefix management with proper environment variable setup
- Path conversion from Linux to Wine format (Z: drive mapping) used during texture conversion phase

### Archive Extraction
- **Zero-Overhead Design**: 99.99% of archives use fast Linux 7zz extraction
- **Proactive Detection**: Linux 7zz extraction issues detected before extraction fails
- **Fallback Safety Net**: Automatic Proton 7z.exe retry for missed encoding issues

## About This Fork

Jackify Engine is a fork of the upstream [Wabbajack](https://github.com/wabbajack-tools/wabbajack) project. For information about the original Wabbajack project, including its features, community, and development, please visit the [official Wabbajack repository](https://github.com/wabbajack-tools/wabbajack).

### What's Different
- **Linux-Native**: Built specifically for Linux systems with .NET 8.0
- **Proton Integration**: Uses Steam Proton to ru texconv.exe for texture processing
- **Enhanced CLI**: Improved command-line interface with better filtering and display options
- **Linux Optimizations**: File system compatibility fixes and performance optimizations

### Compatibility
- **Full Modlist Compatibility**: All Wabbajack modlists work identically to upstream Wabbajack
- **Identical Output**: Same file hashes as Windows Wabbajack installations
- **Upstream Features**: All core Wabbajack CLI functionality preserved

## License

This project is licensed under the GPL3 license, same as the upstream Wabbajack project. See [LICENSE.txt](LICENSE.txt) for details.

## Contributing

This is a specialized Linux fork focused on Proton integration and Linux compatibility. For general Wabbajack development, please contribute to the [upstream Wabbajack project](https://github.com/wabbajack-tools/wabbajack).

## Support

For issues specific to Jackify Engine (Linux compatibility, Proton integration, etc.), please open an issue in this repository. For general Wabbajack questions or modlist-specific issues, please refer to the [upstream Wabbajack community](https://www.wabbajack.org/discord).
