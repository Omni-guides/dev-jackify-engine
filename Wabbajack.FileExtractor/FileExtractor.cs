using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OMODFramework;
using Wabbajack.Common;
using Wabbajack.Common.FileSignatures;
using Wabbajack.Compression.BSA;
using Wabbajack.DTOs.Streams;
using Wabbajack.FileExtractor.ExtractedFiles;
using Wabbajack.FileExtractor.ExtractorHelpers;
using Wabbajack.IO.Async;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;
using Wabbajack.Hashing.PHash;


namespace Wabbajack.FileExtractor;

public class FileExtractor
{
    public static readonly SignatureChecker ArchiveSigs = new(FileType.TES3,
        FileType.BSA,
        FileType.BA2,
        FileType.BTAR,
        FileType.ZIP,
        FileType.EXE,
        FileType.RAR_OLD,
        FileType.RAR_NEW,
        FileType._7Z);

    private static readonly Extension OMODExtension = new(".omod");
    private static readonly Extension FOMODExtension = new(".fomod");

    private static readonly Extension BSAExtension = new(".bsa");

    public static readonly HashSet<Extension> ExtractableExtensions = new()
    {
        new Extension(".bsa"),
        new Extension(".ba2"),
        new Extension(".7z"),
        new Extension(".7zip"),
        new Extension(".rar"),
        new Extension(".zip"),
        new Extension(".btar"),
        new Extension(".exe"),
        OMODExtension,
        FOMODExtension
    };

    // Known problematic characters that cause 7zz encoding issues on Linux
    private static readonly HashSet<char> ProblematicChars = new()
    {
        // Nordic
        'ä', 'ö', 'ü', 'å', 'ø', 'æ', 'Ä', 'Ö', 'Ü', 'Å', 'Ø', 'Æ',
        // Romance
        'á', 'é', 'í', 'ó', 'ú', 'à', 'è', 'ì', 'ò', 'ù', 'â', 'ê', 'î', 'ô', 'û', 'ç', 'ñ',
        'Á', 'É', 'Í', 'Ó', 'Ú', 'À', 'È', 'Ì', 'Ò', 'Ù', 'Â', 'Ê', 'Î', 'Ô', 'Û', 'Ç', 'Ñ',
        // Slavic
        'ć', 'č', 'đ', 'š', 'ž', 'ř', 'ě', 'ý', 'ť', 'ď', 'ň', 'ĺ', 'ľ',
        'Ć', 'Č', 'Đ', 'Š', 'Ž', 'Ř', 'Ě', 'Ý', 'Ť', 'Ď', 'Ň', 'Ĺ', 'Ľ',
        // Other
        'ß', 'þ', 'ð', 'Þ', 'Ð',
        // Degree symbol reported by user (e.g., Mirror°.nif)
        '°'
    };

    private readonly IResource<FileExtractor> _limiter;
    private readonly ILogger<FileExtractor> _logger;
    private readonly TemporaryFileManager _manager;

    private readonly ParallelOptions _parallelOptions;

    public FileExtractor(ILogger<FileExtractor> logger, ParallelOptions parallelOptions, TemporaryFileManager manager,
        IResource<FileExtractor> limiter)
    {
        _logger = logger;
        _parallelOptions = parallelOptions;
        _manager = manager;
        _limiter = limiter;
    }

    public FileExtractor WithTemporaryFileManager(TemporaryFileManager manager)
    {
        return new FileExtractor(_logger, _parallelOptions, manager, _limiter);
    }

    /// <summary>
    /// Detects if any files contain characters that are known to cause extraction issues with 7zz on Linux
    /// </summary>
    private static bool ContainsProblematicCharacters(HashSet<RelativePath> onlyFiles)
    {
        foreach (var file in onlyFiles)
        {
            var filename = file.ToString();
            foreach (var c in filename)
            {
                if (ProblematicChars.Contains(c))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public async Task<IDictionary<RelativePath, T>> GatheringExtract<T>(
        IStreamFactory sFn,
        Predicate<RelativePath> shouldExtract,
        Func<RelativePath, IExtractedFile, ValueTask<T>> mapfn,
        CancellationToken token,
        HashSet<RelativePath>? onlyFiles = null,
        Action<Percent>? progressFunction = null)
    {
        if (sFn is NativeFileStreamFactory) _logger.LogDebug("Extracting {file}", sFn.Name);
        await using var archive = await sFn.GetStream();
        var sig = await ArchiveSigs.MatchesAsync(archive);
        archive.Position = 0;


        IDictionary<RelativePath, T> results;

        switch (sig)
        {
            case FileType.RAR_OLD:
            case FileType.RAR_NEW:
            case FileType._7Z:
            case FileType.ZIP:
            {
                if (sFn.Name.FileName.Extension == OMODExtension)
                {
                    results = await GatheringExtractWithOMOD(archive, shouldExtract, mapfn, token);
                }
                else
                {
                    // Check if we need to use Proton 7z.exe for foreign character handling
                    bool useProtonFallback = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && 
                                            onlyFiles != null && 
                                            ContainsProblematicCharacters(onlyFiles);
                    
                    if (useProtonFallback)
                    {
                        _logger.LogDebug("Archive {ArchiveName} contains files with foreign characters, using Proton 7z.exe for extraction", sFn.Name.FileName);
                        results = await GatheringExtractWithProton7Zip(sFn, shouldExtract,
                            mapfn, onlyFiles, token, progressFunction);
                    }
                    else
                    {
                        await using var tempFolder = _manager.CreateFolder();
                        results = await GatheringExtractWith7Zip(sFn, shouldExtract,
                            mapfn, onlyFiles, token, progressFunction);
                    }
                }

                break;
            }
            case FileType.BTAR:
                results = await GatheringExtractWithBTAR(sFn, shouldExtract, mapfn, token);
                break;

            case FileType.BSA:
            case FileType.BA2:
                results = await GatheringExtractWithBSA(sFn, (FileType) sig, shouldExtract, mapfn, token);
                break;

            case FileType.TES3:
                if (sFn.Name.FileName.Extension == BSAExtension)
                    results = await GatheringExtractWithBSA(sFn, (FileType) sig, shouldExtract, mapfn, token);
                else
                    throw new Exception($"Invalid file format {sFn.Name}");
                break;
            case FileType.EXE:
                results = await GatheringExtractWithInnoExtract(sFn, shouldExtract,
                    mapfn, onlyFiles, token, progressFunction);
                break;
            default:
                throw new Exception($"Invalid file format {sFn.Name}");
        }

        if (onlyFiles != null && onlyFiles.Count != results.Count)
        {
            // Log missing files for debugging at debug level first - will escalate to error if fallback fails
            var missingFiles = onlyFiles.Where(expected => !results.ContainsKey(expected)).ToList();
            var extractedFiles = results.Keys.ToList();
            
            _logger.LogDebug("Sanity check failed for {ArchiveName} - {ResultCount}/{ExpectedCount} files extracted. Missing files:", 
                sFn.Name.FileName, results.Count, onlyFiles.Count);
            
            foreach (var missing in missingFiles.Take(10)) // Log first 10 missing files
            {
                _logger.LogDebug("Missing: {MissingFile}", missing);
            }
            
            if (missingFiles.Count > 10)
            {
                _logger.LogDebug("... and {AdditionalCount} more missing files", missingFiles.Count - 10);
            }
            
            // Check if this might be a foreign character encoding issue that we missed
            bool couldBeForeignCharIssue = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && 
                                          (sFn.Name.FileName.Extension == Extension.FromPath(".zip") ||
                                           sFn.Name.FileName.Extension == Extension.FromPath(".7z") ||
                                           sFn.Name.FileName.Extension == Extension.FromPath(".rar")) &&
                                          onlyFiles.Count < 1000; // Only try for reasonably sized archives
            
            if (couldBeForeignCharIssue && !ContainsProblematicCharacters(onlyFiles))
            {
                _logger.LogDebug("Attempting Proton 7z.exe fallback for potential encoding issue in {ArchiveName}", sFn.Name.FileName);
                
                try
                {
                    var protonResults = await GatheringExtractWithProton7Zip(sFn, shouldExtract, mapfn, onlyFiles, token, progressFunction);
                    if (protonResults.Count == onlyFiles.Count)
                    {
                        _logger.LogDebug("Proton 7z.exe fallback successful for {ArchiveName}: {Count}/{ExpectedCount} files extracted", 
                            sFn.Name.FileName, protonResults.Count, onlyFiles.Count);
                        return protonResults;
                    }
                    else
                    {
                        _logger.LogError("Proton 7z.exe fallback still has count mismatch for {ArchiveName}: {Count}/{ExpectedCount}", 
                            sFn.Name.FileName, protonResults.Count, onlyFiles.Count);
                        // Log the original failure details now that fallback also failed
                        _logger.LogError("Original sanity check failed for {ArchiveName} - {ResultCount}/{ExpectedCount} files extracted. Missing files:", 
                            sFn.Name.FileName, results.Count, onlyFiles.Count);
                        foreach (var missing in missingFiles.Take(10))
                        {
                            _logger.LogError("Missing: {MissingFile}", missing);
                        }
                        if (missingFiles.Count > 10)
                        {
                            _logger.LogError("... and {AdditionalCount} more missing files", missingFiles.Count - 10);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Proton 7z.exe fallback failed for {ArchiveName}", sFn.Name.FileName);
                    // Log the original failure details now that fallback also failed
                    _logger.LogError("Original sanity check failed for {ArchiveName} - {ResultCount}/{ExpectedCount} files extracted. Missing files:", 
                        sFn.Name.FileName, results.Count, onlyFiles.Count);
                    foreach (var missing in missingFiles.Take(10))
                    {
                        _logger.LogError("Missing: {MissingFile}", missing);
                    }
                    if (missingFiles.Count > 10)
                    {
                        _logger.LogError("... and {AdditionalCount} more missing files", missingFiles.Count - 10);
                    }
                    // Fall through to original exception
                }
            }
            
            // If we get here, either no fallback was attempted or it failed
            throw new Exception(
                $"Sanity check error extracting {sFn.Name} - {results.Count} results, expected {onlyFiles.Count}. This is a critical extraction failure that must be resolved.");
        }
        return results;
    }

    private async Task<IDictionary<RelativePath, T>> GatheringExtractWith7Zip<T>(
        IStreamFactory sFn, 
        Predicate<RelativePath> shouldExtract, 
        Func<RelativePath, IExtractedFile, ValueTask<T>> mapfn, 
        HashSet<RelativePath>? onlyFiles, 
        CancellationToken token,
        Action<Percent>? progressFunction)
    {
        TemporaryPath? tmpFile = null;
        await using var dest = _manager.CreateFolder();

        TemporaryPath? spoolFile = null;
        AbsolutePath source;
        
        var job = await _limiter.Begin($"Extracting {sFn.Name}", 0, token);
        try
        {
            if (sFn.Name is AbsolutePath abs)
            {
                source = abs;
            }
            else
            {
                spoolFile = _manager.CreateFile(sFn.Name.FileName.Extension);
                await using var s = await sFn.GetStream();
                await spoolFile.Value.Path.WriteAllAsync(s, token);
                source = spoolFile.Value.Path;
            }

            _logger.LogDebug("Extracting {Source}", source.FileName);

            var initialPath = "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                initialPath = @"Extractors\windows-x64\7z.exe";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                initialPath = @"Extractors\linux-x64\7zz";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                initialPath = @"Extractors\mac\7zz";

            var process = new ProcessHelper
                {Path = initialPath.ToRelativePath().RelativeTo(KnownFolders.EntryPoint)};

            if (onlyFiles != null)
            {
                //It's stupid that we have to do this, but 7zip's file pattern matching isn't very fuzzy
                IEnumerable<string> AllVariants(string input)
                {
                    var forward = input.Replace("\\", "/");
                    
                    // Common case variations for directory names
                    var caseVariants = new List<string> { input, forward };
                    
                    // Add case variations for common directory names
                    var commonDirs = new Dictionary<string, string>
                    {
                        { "textures", "Textures" },
                        { "meshes", "Meshes" },
                        { "sounds", "Sounds" },
                        { "music", "Music" },
                        { "scripts", "Scripts" },
                        { "interface", "Interface" }
                    };
                    
                    foreach (var (lower, upper) in commonDirs)
                    {
                        if (input.Contains(lower, StringComparison.OrdinalIgnoreCase))
                        {
                            // Replace with proper case
                            var upperCase = input.Replace(lower, upper, StringComparison.OrdinalIgnoreCase);
                            var upperCaseForward = upperCase.Replace("\\", "/");
                            caseVariants.Add(upperCase);
                            caseVariants.Add(upperCaseForward);
                            
                            // Also try lowercase variant
                            var lowerCase = input.Replace(upper, lower, StringComparison.OrdinalIgnoreCase);
                            var lowerCaseForward = lowerCase.Replace("\\", "/");
                            caseVariants.Add(lowerCase);
                            caseVariants.Add(lowerCaseForward);
                        }
                    }
                    
                    // Remove duplicates and generate quoted patterns
                    var uniqueVariants = caseVariants.Distinct().ToList();
                    
                    foreach (var variant in uniqueVariants)
                    {
                        yield return $"\"{variant}\"";
                        yield return $"\"\\{variant}\"";
                        yield return $"\"/{variant}\"";
                    }
                }

                tmpFile = _manager.CreateFile();
                await tmpFile.Value.Path.WriteAllLinesAsync(onlyFiles.SelectMany(f => AllVariants((string)f)),
                    token);
                process.Arguments = new object[]
                {
                    "x", "-bsp1", "-y", $"-o\"{dest}\"", source, $"@\"{tmpFile.Value.ToString()}\"", "-mmt=off"
                };
            }
            else
            {
                process.Arguments = ["x", "-bsp1", "-y", $"-o\"{dest}\"", source, "-mmt=off"];
            }

            _logger.LogTrace("{prog} {args}", process.Path, process.Arguments);
            
            if (tmpFile != null)
            {
                var patternContent = await tmpFile.Value.Path.ReadAllTextAsync();
                var lines = patternContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                _logger.LogDebug("EXTRACTION DEBUG: {archive} - Pattern file {patternFile} has {lines} lines, size {size} bytes", 
                    source.FileName, tmpFile.Value.Path.FileName, lines.Length, patternContent.Length);
            }

            var totalSize = source.Size();
            var lastPercent = 0;
            job.Size = totalSize;

            // Retry mechanism for 7zip extraction failures
            int maxRetries = 2;
            int retryCount = 0;
            int exitCode = 0;

            while (retryCount <= maxRetries)
            {
                if (retryCount > 0)
                {
                    _logger.LogWarning("Retrying 7zip extraction for {archive} (attempt {attempt}/{maxAttempts})", 
                        source.FileName, retryCount + 1, maxRetries + 1);
                    
                    // Clean up destination directory before retry
                    if (dest.Path.DirectoryExists())
                    {
                        dest.Path.DeleteDirectory();
                    }
                    
                    // Small delay before retry
                    await Task.Delay(1000, token);
                }

                var result = process.Output.Where(d => d.Type == ProcessHelper.StreamType.Output)
                    .ForEachAsync(p =>
                    {
                        var (_, line) = p;
                        if (line == null)
                            return;

                        if (line.Length <= 4 || line[3] != '%') return;

                        if (!int.TryParse(line[..3], out var percentInt)) return;

                        var oldPosition = lastPercent == 0 ? 0 : totalSize / 100 * lastPercent;
                        var newPosition = percentInt == 0 ? 0 : totalSize / 100 * percentInt;
                        var throughput = newPosition - oldPosition;
                        job.ReportNoWait((int) throughput);
                        
                        progressFunction?.Invoke(Percent.FactoryPutInRange(lastPercent, 100));
                        
                        lastPercent = percentInt;
                    }, token);

                exitCode = await process.Start();

                // If successful, break out of retry loop
                if (exitCode == 0)
                {
                    break;
                }

                retryCount++;
            }

            // Check for 7zip extraction errors
            if (exitCode != 0)
            {
                // Collect error output for better diagnostics
                var errorOutput = string.Join("\n", process.Output
                    .Where(d => d.Type == ProcessHelper.StreamType.Error)
                    .Select(d => d.Line));

                // Provide specific guidance based on exit code
                string errorMessage = exitCode switch
                {
                    255 => "Archive may be corrupted, insufficient disk space, or permission denied",
                    1 => "Warning or non-fatal error occurred during extraction",
                    2 => "Fatal error occurred during extraction",
                    7 => "Command line error",
                    8 => "Not enough memory for operation",
                    _ => $"Unknown error code {exitCode}"
                };

                _logger.LogError("7zip failed with exit code {exitCode} for {archive}: {errorMessage}", 
                    exitCode, source.FileName, errorMessage);
                
                if (!string.IsNullOrEmpty(errorOutput))
                {
                    _logger.LogError("7zip error output: {errorOutput}", errorOutput);
                }

                // For exit code 255, try to provide more specific diagnostics
                if (exitCode == 255)
                {
                    var availableSpace = new DriveInfo(Path.GetPathRoot(dest.Path.ToString()) ?? "/").AvailableFreeSpace;
                    var archiveSize = source.Size();
                    
                    _logger.LogError("Archive size: {archiveSize} bytes, Available disk space: {availableSpace} bytes", 
                        archiveSize, availableSpace);
                    
                    if (availableSpace < archiveSize * 2) // Need at least 2x space for extraction
                    {
                        _logger.LogError("Insufficient disk space for extraction. Need at least {neededSpace} bytes, have {availableSpace} bytes", 
                            archiveSize * 2, availableSpace);
                    }
                }

                throw new InvalidOperationException($"7zip extraction failed with exit code {exitCode} for {source.FileName}: {errorMessage}");
            }
            
            var extractedFiles = dest.Path.EnumerateFiles().ToList();
            _logger.LogDebug("POST-EXTRACTION: {archive} extracted {count} files to {dest}", 
                source.FileName, extractedFiles.Count, dest.Path);

            // Post-process: move files with backslashes in their names to correct subdirectories
            // This must happen BEFORE processing and sanity checks to avoid path mismatches
            await MoveFilesWithBackslashesToSubdirs(dest.Path.ToString());

            // Add small delay to ensure file system sync after post-processing
            await Task.Delay(100, token);

            
            job.Dispose();
            var results = await dest.Path.EnumerateFiles()
                .SelectAsync(async f =>
                {
                    try
                    {
                        var path = f.RelativeTo(dest.Path);
                        if (!shouldExtract(path)) return ((RelativePath, T)) default;
                        var file = new ExtractedNativeFile(f);
                        var mapResult = await mapfn(path, file);
                        f.Delete();
                        return (path, mapResult);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException || 
                                              ex is UnauthorizedAccessException || ex is ArgumentException)
                    {
                        _logger.LogError("Failed to process extracted file: {file} ({error})", f, ex.Message);
                        throw;
                    }
                })
                .Where(d => d.Item1 != default)
                .ToDictionary(d => d.Item1, d => d.Item2);
            

            return results;
        }
        finally
        {
            job.Dispose();
            
            if (tmpFile != null) await tmpFile.Value.DisposeAsync();

            if (spoolFile != null) await spoolFile.Value.DisposeAsync();
        }
    }

    private async Task<IDictionary<RelativePath, T>> GatheringExtractWithProton7Zip<T>(
        IStreamFactory sFn, 
        Predicate<RelativePath> shouldExtract, 
        Func<RelativePath, IExtractedFile, ValueTask<T>> mapfn, 
        HashSet<RelativePath>? onlyFiles, 
        CancellationToken token,
        Action<Percent>? progressFunction)
    {
        await using var tempFolder = _manager.CreateFolder();
        
        TemporaryPath? spoolFile = null;
        AbsolutePath source;
        
        try
        {
            // Handle archive source - same as regular 7zip extraction
            if (sFn.Name is AbsolutePath abs)
            {
                source = abs;
            }
            else
            {
                spoolFile = _manager.CreateFile(sFn.Name.FileName.Extension);
                await using var s = await sFn.GetStream();
                await spoolFile.Value.Path.WriteAllAsync(s, token);
                source = spoolFile.Value.Path;
            }
            
            _logger.LogDebug("Proton 7z.exe extracting {Source}", source.FileName);
            
            // Extract EVERYTHING using Proton 7z.exe - don't try to extract specific files
            // The test script proved this works, so replicate that exactly
            var processResult = await RunProton7zExtraction(source.ToString(), tempFolder.Path.ToString());
            
            if (processResult != 0)
            {
                throw new Exception($"Proton 7z.exe extraction failed with exit code {processResult}");
            }
            
            _logger.LogDebug("Proton 7z.exe extraction completed successfully");
            
            // Process extracted files - same as regular extraction
            var results = new Dictionary<RelativePath, T>();
            var extractedFiles = tempFolder.Path.EnumerateFiles(recursive: true);
            
            foreach (var file in extractedFiles)
            {
                var relativePath = file.RelativeTo(tempFolder.Path);
                if (!shouldExtract(relativePath)) continue;
                
                var extractedFile = new ExtractedNativeFile(file);
                var result = await mapfn(relativePath, extractedFile);
                results[relativePath] = result;
                file.Delete(); // Clean up like regular extraction
            }
            
            return results;
        }
        finally
        {
            if (spoolFile != null) await spoolFile.Value.DisposeAsync();
        }
    }
    
    private async Task<int> RunProton7zExtraction(string archivePath, string outputPath)
    {
        // Use dynamic Proton detection like texconv.exe does
        var protonDetector = new ProtonDetector(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProtonDetector>.Instance);

        var protonPath = await protonDetector.GetProtonWrapperPathAsync();
        
        if (protonPath == null)
        {
            _logger.LogError("No Proton installation found, cannot run 7z.exe via Proton");
            return -1;
        }
        
        var winePrefixPath = Path.Combine(Path.GetTempPath(), "jackify-proton-extraction");
        Directory.CreateDirectory(winePrefixPath);
        
        // Use absolute path to 7z.exe to avoid path resolution issues
        var sevenZipPath = "Extractors/windows-x64/7z.exe".ToRelativePath().RelativeTo(KnownFolders.EntryPoint).ToString();
        
        var processInfo = new ProcessStartInfo
        {
            FileName = protonPath,
            Arguments = $"run \"{sevenZipPath}\" x -sccUTF-8 -o\"{outputPath}\" \"{archivePath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = KnownFolders.EntryPoint.ToString()
        };

        // Set environment variables properly 
        processInfo.Environment["WINEPREFIX"] = winePrefixPath;
        processInfo.Environment["STEAM_COMPAT_DATA_PATH"] = winePrefixPath;
        processInfo.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = protonDetector.GetSteamClientInstallPath();
        processInfo.Environment["WINEDEBUG"] = "-all";
        processInfo.Environment["DISPLAY"] = "";
        processInfo.Environment["WAYLAND_DISPLAY"] = "";
        processInfo.Environment["WINEDLLOVERRIDES"] = "msdia80.dll=n;conhost.exe=d;cmd.exe=d";

        _logger.LogDebug("PROTON EXTRACTION DEBUG:");
        _logger.LogDebug("Command: {ProtonPath} {Arguments}", protonPath, processInfo.Arguments);
        _logger.LogDebug("Working Dir: {WorkingDir}", processInfo.WorkingDirectory);
        _logger.LogDebug("Archive exists: {ArchiveExists}", File.Exists(archivePath));
        _logger.LogDebug("7z.exe exists: {ExtractorExists}", File.Exists(sevenZipPath));
        _logger.LogDebug("Output dir exists: {OutputExists}", Directory.Exists(outputPath));
        _logger.LogDebug("WINEPREFIX: {WinePrefix}", winePrefixPath);

        using var process = new Process { StartInfo = processInfo };
        process.Start();
        
        // Capture output for debugging
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        
        await process.WaitForExitAsync();
        
        if (process.ExitCode != 0)
        {
            _logger.LogError("Proton 7z.exe failed with exit code {ExitCode}", process.ExitCode);
            _logger.LogError("STDOUT: {StdOut}", stdout);
            _logger.LogError("STDERR: {StdErr}", stderr);
        }
        else
        {
            _logger.LogDebug("Proton 7z.exe succeeded");
            _logger.LogDebug("STDOUT: {StdOut}", stdout);
        }
        
        return process.ExitCode;
    }

    private async Task<IDictionary<RelativePath,T>> GatheringExtractWithBTAR<T>
        (IStreamFactory sFn, Predicate<RelativePath> shouldExtract, Func<RelativePath,IExtractedFile,ValueTask<T>> mapfn, CancellationToken token)
    {
        await using var strm = await sFn.GetStream();
        var astrm = new AsyncBinaryReader(strm);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(await astrm.ReadBytes(4));
        // BTAR Magic
        if (magic != 0x42544152) throw new Exception("Not a valid BTAR file");
        if (await astrm.ReadUInt16() != 1) throw new Exception("Invalid BTAR major version, should be 1");
        var minorVersion = await astrm.ReadUInt16();
        if (minorVersion is < 2 or > 4) throw new Exception("Invalid BTAR minor version");

        var results = new Dictionary<RelativePath, T>();

        while (astrm.Position < astrm.Length)
        {
            var nameLength = await astrm.ReadUInt16();
            var name = Encoding.UTF8.GetString(await astrm.ReadBytes(nameLength)).ToRelativePath();
            var dataLength = await astrm.ReadUInt64();
            var newPos = astrm.Position + (long)dataLength;
            if (!shouldExtract(name))
            {
                astrm.Position += (long)dataLength;
                continue;
            }

            var result = await mapfn(name, new BTARExtractedFile(sFn, name, astrm, astrm.Position, (long) dataLength));
            results.Add(name, result);
            astrm.Position = newPos;
        }

        return results;
    }

    private class BTARExtractedFile : IExtractedFile
    {
        private readonly IStreamFactory _parent;
        private readonly AsyncBinaryReader _rdr;
        private readonly long _start;
        private readonly long _length;
        private readonly RelativePath _name;
        private bool _disposed = false;

        public BTARExtractedFile(IStreamFactory parent, RelativePath name, AsyncBinaryReader rdr, long startingPosition, long length)
        {
            _name = name;
            _parent = parent;
            _rdr = rdr;
            _start = startingPosition;
            _length = length;
        }

        public DateTime LastModifiedUtc => _parent.LastModifiedUtc;
        public IPath Name => _name;
        public async ValueTask<Stream> GetStream()
        {
            _rdr.Position = _start;
            var data = await _rdr.ReadBytes((int) _length);
            return new MemoryStream(data);
        }

        public bool CanMove { get; set; } = true;
        public async ValueTask Move(AbsolutePath newPath, CancellationToken token)
        {
            await using var output = newPath.Open(FileMode.Create, FileAccess.Read, FileShare.Read);
            _rdr.Position = _start;
            await _rdr.BaseStream.CopyToLimitAsync(output, (int)_length, token);
            _disposed = true;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                // BTAR files are memory-based, no cleanup needed
            }
        }
    }

    private async Task<Dictionary<RelativePath, T>> GatheringExtractWithOMOD<T>
    (Stream archive, Predicate<RelativePath> shouldExtract, Func<RelativePath, IExtractedFile, ValueTask<T>> mapfn,
        CancellationToken token)
    {
        var tmpFile = _manager.CreateFile();
        await tmpFile.Path.WriteAllAsync(archive, CancellationToken.None);
        var dest = _manager.CreateFolder();

        using var omod = new OMOD(tmpFile.Path.ToString());

        var results = new Dictionary<RelativePath, T>();

        omod.ExtractFilesParallel(dest.Path.ToString(), 4, cancellationToken: token);
        if (omod.HasEntryFile(OMODEntryFileType.PluginsCRC))
            omod.ExtractFiles(false, dest.Path.ToString());

        // Fix OMOD files with backslashes in names (Linux path issue)
        await MoveFilesWithBackslashesToSubdirs(dest.Path.ToString());

        var files = omod.GetDataFiles();
        if (omod.HasEntryFile(OMODEntryFileType.PluginsCRC))
            files.UnionWith(omod.GetPluginFiles());

        foreach (var compressedFile in files)
        {
            var abs = compressedFile.Name.ToRelativePath().RelativeTo(dest.Path);
            var rel = abs.RelativeTo(dest.Path);
            if (!shouldExtract(rel)) continue;

            var result = await mapfn(rel, new ExtractedNativeFile(abs));
            results.Add(rel, result);
        }

        return results;
    }

    public async Task<Dictionary<RelativePath, T>> GatheringExtractWithBSA<T>(IStreamFactory sFn,
        FileType sig,
        Predicate<RelativePath> shouldExtract,
        Func<RelativePath, IExtractedFile, ValueTask<T>> mapFn,
        CancellationToken token)
    {
        var archive = await BSADispatch.Open(sFn, sig);
        var results = new Dictionary<RelativePath, T>();
        foreach (var entry in archive.Files)
        {
            if (token.IsCancellationRequested) break;

            if (!shouldExtract(entry.Path))
                continue;

            var result = await mapFn(entry.Path, new ExtractedMemoryFile(await entry.GetStreamFactory(token)));
            results.Add(entry.Path, result);
        }
        
        _logger.LogDebug("Finished extracting {Name}", sFn.Name);
        return results;
    }

    public async Task<IDictionary<RelativePath, T>> GatheringExtractWithInnoExtract<T>(IStreamFactory sf,
        Predicate<RelativePath> shouldExtract,
        Func<RelativePath, IExtractedFile, ValueTask<T>> mapfn,
        IReadOnlyCollection<RelativePath>? onlyFiles,
        CancellationToken token,
        Action<Percent>? progressFunction = null)
    {
        TemporaryPath? tmpFile = null;
        await using var dest = _manager.CreateFolder();

        TemporaryPath? spoolFile = null;
        AbsolutePath source;
        
        var job = await _limiter.Begin($"Extracting {sf.Name}", 0, token);
        try
        {
            if (sf.Name is AbsolutePath abs)
            {
                source = abs;
            }
            else
            {
                spoolFile = _manager.CreateFile(sf.Name.FileName.Extension);
                await using var s = await sf.GetStream();
                await spoolFile.Value.Path.WriteAllAsync(s, token);
                source = spoolFile.Value.Path;
            }

            var initialPath = "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                initialPath = @"Extractors\windows-x64\innoextract.exe";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                initialPath = @"Extractors\linux-x64\innoextract";

            // This might not be the best way to do it since it forces a full extraction
            // of the full .exe file, but the other method that would tell WJ to only extract specific files was bugged

            var processScan = new ProcessHelper
            {
                Path = initialPath.ToRelativePath().RelativeTo(KnownFolders.EntryPoint),
                Arguments = [$"\"{source}\"", "--list-sizes", "-m", "--collisions \"rename-all\""]
            };

            var processExtract = new ProcessHelper
            {
                Path = initialPath.ToRelativePath().RelativeTo(KnownFolders.EntryPoint),
                Arguments = [$"\"{source}\"", "-e", "-m", "--list-sizes", "--collisions \"rename-all\"", $"-d \"{dest}\""]
            };
            
            _logger.LogTrace("{prog} {args}", processExtract.Path, processExtract.Arguments);

            // We skip the first and last lines since they don't contain any info about the files, it's just a header and a footer from InnoExtract
            // First do a scan so we know the total size of the operation
            var scanResult = processScan.Output.Where(d => d.Type == ProcessHelper.StreamType.Output).Skip(1).SkipLast(1)
                .ForEachAsync(p =>
                {
                    var (_, line) = p;
                    job.Size += InnoHelper.GetExtractedFileSize(line);
                });

            Task<int> scanExitCode = Task.Run(() => processScan.Start());

            var extractResult = processExtract.Output.Where(d => d.Type == ProcessHelper.StreamType.Output).Skip(1).SkipLast(1)
                .ForEachAsync(p =>
                {
                    var (_, line) = p;
                    job.ReportNoWait(InnoHelper.GetExtractedFileSize(line));
                }, token);
            
            var extractErrorResult = processExtract.Output.Where(d => d.Type == ProcessHelper.StreamType.Error)
                .ForEachAsync(p =>
                {
                    var (_, line) = p;
                    _logger.LogError("While extracting InnoSetup archive {fileName} at {path}: {line}", source.FileName, processExtract.Path, line);
                }, token);

            // Wait for the job size to be calculated before actually starting the extraction operation, should be very fast
            await scanExitCode;

            var exitCode = await processExtract.Start();
            
            
            if (exitCode != 0)
            {
                // Commented out because there are more .exe binaries in the average setup that this logging might confuse people more than it helps.
                // _logger.LogDebug($"Can not extract {source.FileName} with Innoextract - Exit code: {exitCode}");
            }
            else
            {
                _logger.LogDebug($"Extracting {source.FileName} - done");
            }
            
            job.Dispose();
            var results = await dest.Path.EnumerateFiles()
                .SelectAsync(async f =>
                {
                    var path = f.RelativeTo(dest.Path);
                    if (!shouldExtract(path)) return ((RelativePath, T)) default;
                    var file = new ExtractedNativeFile(f);
                    var mapResult = await mapfn(path, file);
                    f.Delete();
                    return (path, mapResult);
                })
                .Where(d => d.Item1 != default)
                .ToDictionary(d => d.Item1, d => d.Item2);
            
            return results;
        }
        finally
        {
            job.Dispose();
            
            if (tmpFile != null) await tmpFile.Value.DisposeAsync();

            if (spoolFile != null) await spoolFile.Value.DisposeAsync();
        }
    }

    public async Task ExtractAll(AbsolutePath src, AbsolutePath dest, CancellationToken token,
        Predicate<RelativePath>? filterFn = null, Action<Percent>? updateProgress = null)
    {
        filterFn ??= _ => true;
        await GatheringExtract(new NativeFileStreamFactory(src), filterFn, async (path, factory) =>
        {
            var abs = path.RelativeTo(dest);
            abs.Parent.CreateDirectory();
            await using var stream = await factory.GetStream();
            await abs.WriteAllAsync(stream, token);
            return 0;
        }, token, progressFunction: updateProgress);
    }
    


    /// <summary>
    /// Moves any files with backslashes in their names to the correct subdirectory structure.
    /// This handles the case where 7zip on Linux creates files with backslashes in their names
    /// instead of proper directory structures.
    /// </summary>
    private async Task MoveFilesWithBackslashesToSubdirs(string extractionDir)
    {
        if (!Directory.Exists(extractionDir))
        {
            _logger.LogWarning("[POST-PROCESS] Extraction directory does not exist: {ExtractionDir}", extractionDir);
            return;
        }
        
        var files = Directory.GetFiles(extractionDir, "*", SearchOption.AllDirectories);
        var backslashFiles = new List<string>();
        
        // Find all files with backslashes in their names
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Contains("\\"))
            {
                backslashFiles.Add(file);
                _logger.LogDebug("[POST-PROCESS] Found file with backslashes in name: {FileName}", fileName);
            }
        }
        
        if (backslashFiles.Count == 0)
        {
            _logger.LogDebug("[POST-PROCESS] No files with backslashes found in {ExtractionDir}", extractionDir);
            return;
        }
        
        _logger.LogDebug("[POST-PROCESS] Found {Count} files with backslashes in names, fixing directory structure", backslashFiles.Count);
        
        foreach (var file in backslashFiles)
        {
            var fileName = Path.GetFileName(file);
            var parts = fileName.Split(new[] {'\\'}, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length < 2)
            {
                _logger.LogDebug("[POST-PROCESS] File {FileName} has backslashes but insufficient path parts", fileName);
                continue;
            }
            
            // Create the proper directory structure
            var fileNameOnly = parts.Last();
            var dirParts = parts.Take(parts.Length - 1).ToArray();
            var newDir = Path.Combine(Path.GetDirectoryName(file)!, Path.Combine(dirParts));
            var newPath = Path.Combine(newDir, fileNameOnly);
            
            try
            {
                // Create the directory structure
                Directory.CreateDirectory(newDir);
                
                // Move the file to the correct location
                if (File.Exists(newPath))
                {
                    _logger.LogDebug("[POST-PROCESS] Target file already exists, overwriting: {NewPath}", newPath);
                }
                
                File.Move(file, newPath, overwrite: true);
                _logger.LogDebug("[POST-PROCESS] Moved {OldFile} -> {NewPath}", file, newPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[POST-PROCESS] Failed to move {File} to {NewPath}", file, newPath);
                // Don't rethrow - continue processing other files
            }
        }
        
        _logger.LogDebug("[POST-PROCESS] Completed backslash path correction for {Count} files", backslashFiles.Count);
        await Task.CompletedTask;
    }
}