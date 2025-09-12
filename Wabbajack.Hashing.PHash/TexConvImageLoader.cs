using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shipwreck.Phash;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Wabbajack.Common;
using Wabbajack.Common.FileSignatures;
using Wabbajack.DTOs.Texture;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Microsoft.Extensions.Logging;


namespace Wabbajack.Hashing.PHash;

public class TexConvImageLoader : IImageLoader, IDisposable
{
    private readonly SignatureChecker _sigs;
    private readonly TemporaryFileManager _tempManager;

    private readonly ProtonPrefixManager _protonManager;
    private readonly ILogger<TexConvImageLoader> _logger;
    private readonly Dictionary<string, string> _tempFileMapping = new(); // temp file -> original file mapping
    private readonly object _mappingLock = new();

    public TexConvImageLoader(TemporaryFileManager manager, ILogger<TexConvImageLoader> logger)
    {
        _tempManager = manager;
        _logger = logger;
        _sigs = new SignatureChecker(FileType.DDS, FileType.PNG, FileType.JPG, FileType.BMP);
        _protonManager = new ProtonPrefixManager(logger);
    }
    
    public async ValueTask<ImageState> Load(AbsolutePath path)
    {
        return await GetState(path);
    }

    public async ValueTask<ImageState> Load(Stream stream)
    {

        var ext = await DetermineType(stream);
        var temp = _tempManager.CreateFile(ext);
        await using var fs = temp.Path.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fs);
        fs.Close();
        return await GetState(temp.Path);
    }

    private async Task<Extension> DetermineType(Stream stream)
    {
        var sig = await _sigs.MatchesAsync(stream);

        var ext = new Extension(".tga");
        if (sig != null)
            ext = new Extension("." + Enum.GetName(sig.Value));

        stream.Position = 0;
        return ext;
    }

    public async Task Recompress(AbsolutePath input, int width, int height, int mipMaps, DXGI_FORMAT format, AbsolutePath output,
        CancellationToken token)
    {
        var outFolder = _tempManager.CreateFolder();
        try
        {
            var outFile = input.FileName.RelativeTo(outFolder.Path);
            
            // Add mapping for debugging
            lock (_mappingLock)
            {
                _tempFileMapping[outFile.FileName.ToString()] = input.FileName.ToString();
            }
            
            await ConvertImage(input, outFolder.Path, width, height, mipMaps, format, input.Extension);
            await outFile.MoveToAsync(output, token: token, overwrite:true);
        }
        finally
        {
            await outFolder.DisposeAsync();
        }
    }

    public async Task Recompress(Stream input, int width, int height, int mipMaps, DXGI_FORMAT format, Stream output, CancellationToken token,
        bool leaveOpen = false)
    {
        var type = await DetermineType(input);
        var toFolder = _tempManager.CreateFolder();
        var fromFile = _tempManager.CreateFile(type);
        
        try
        {
            await input.CopyToAsync(fromFile.Path, token);
            var toFile = fromFile.Path.FileName.RelativeTo(toFolder.Path);
            
            // Add mapping for debugging (using temp file name as key since we don't have original)
            lock (_mappingLock)
            {
                _tempFileMapping[toFile.FileName.ToString()] = $"STREAM_INPUT_{fromFile.Path.FileName}";
            }
            
            await ConvertImage(fromFile.Path, toFolder.Path, width, height, mipMaps, format, type);
            
            // Handle case sensitivity issue - texconv.exe might create files with different case extensions
            if (!toFile.FileExists())
            {
                // Try to find the actual output file created by texconv.exe (case insensitive)
                var expectedBaseName = toFile.FileName.FileNameWithoutExtension;
                var actualFile = toFolder.Path.EnumerateFiles()
                    .FirstOrDefault(f => string.Equals(f.FileName.FileNameWithoutExtension.ToString(), expectedBaseName.ToString(), StringComparison.OrdinalIgnoreCase));
                
                if (actualFile != null)
                {
                    _logger.LogDebug("Using actual output file: {ActualFile} (expected: {ExpectedFile})", actualFile, toFile);
                    toFile = actualFile;
                }
                else
                {
                    var availableFiles = toFolder.Path.EnumerateFiles().Select(f => f.FileName).ToArray();
                    throw new FileNotFoundException($"TexConv failed to create output file. Expected: {toFile.FileName}, Available: [{string.Join(", ", availableFiles)}]");
                }
            }
            
            await using var fs = toFile.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            await fs.CopyToAsync(output, token);
        }
        finally
        {
            // Manually dispose temp resources after all operations complete
            await fromFile.DisposeAsync();
            await toFolder.DisposeAsync();
        }
    }
    
    


    public async Task ConvertImage(AbsolutePath from, AbsolutePath toFolder, int w, int h, int mipMaps, DXGI_FORMAT format, Extension fileFormat)
    {
        // Convert Linux paths to Wine format for Proton
        var wineFromPath = ProtonDetector.ConvertToWinePath(from);
        var wineToFolderPath = ProtonDetector.ConvertToWinePath(toFolder);
        
        object[] args;
        if (mipMaps != 0)
        {
            args = new object[]
            {
                wineFromPath, "-ft", fileFormat.ToString()[1..], "-f", format, "-o", wineToFolderPath, "-w", w, "-h", h, "-m", mipMaps,
                "-if", "CUBIC", "-singleproc"
            };
        }
        else
        {
            args = new object[]
            {
                wineFromPath, "-ft", fileFormat.ToString()[1..], "-f", format, "-o", wineToFolderPath, "-w", w, "-h", h,
                "-if", "CUBIC", "-singleproc"
            };
        }

        // Use Proton prefix manager for texconv.exe execution
        var ph = await _protonManager.CreateTexConvProcess(args);
        
        // Log the actual texconv command being executed for debugging
        var commandString = $"proton run Tools\\texconv.exe {string.Join(" ", args.Select(arg => arg.ToString()))}";
        
        // Get original file name from mapping if available
        string originalFileName = "UNKNOWN";
        lock (_mappingLock)
        {
            if (_tempFileMapping.TryGetValue(from.FileName.ToString(), out var original))
            {
                originalFileName = original;
            }
        }
        
        _logger.LogDebug("Executing texconv command: {Command}", commandString);
        _logger.LogDebug("TEXTURE_PROCESSING: {TempFile} (original: {OriginalFile}) -> {Format} {Width}x{Height} {MipMaps}mips", 
            from.FileName, originalFileName, format, w, h, mipMaps);
        
        await ph.Start();
        
        _logger.LogDebug("TEXTURE_COMPLETED: {TempFile} -> {Format} {Width}x{Height}", 
            from.FileName, format, w, h);
    }

    public async Task ConvertImage(Stream from, ImageState state, Extension ext, AbsolutePath to)
    {
        var tmpFile = _tempManager.CreateFolder();
        try
        {
            var inFile = to.FileName.RelativeTo(tmpFile.Path);
            await inFile.WriteAllAsync(from, CancellationToken.None);
            await ConvertImage(inFile, to.Parent, state.Width, state.Height, state.MipLevels, state.Format, ext);
        }
        finally
        {
            await tmpFile.DisposeAsync();
        }
    }
    
    // Internals
    public async Task<ImageState> GetState(AbsolutePath path)
    {
        try
        {
            // Convert Linux path to Wine format for Proton
            var winePathArg = ProtonDetector.ConvertToWinePath(path);
            var ph = await _protonManager.CreateTexDiagProcess(new object[] { "info", winePathArg, "-nologo" });
            var lines = new ConcurrentStack<string>();
            using var _ = ph.Output.Where(p => p.Type == ProcessHelper.StreamType.Output)
                .Select(p => p.Line)
                .Where(p => p.Contains(" = "))
                .Subscribe(l => lines.Push(l));
            await ph.Start();

            var data = lines.Select(l =>
            {
                var split = l.Split(" = ");
                return (split[0].Trim(), split[1].Trim());
            }).ToDictionary(p => p.Item1, p => p.Item2);

            return new ImageState
            {
                Width = int.Parse(data["width"]),
                Height = int.Parse(data["height"]),
                Format = Enum.Parse<DXGI_FORMAT>(data["format"]),
                PerceptualHash = await GetPHash(path),
                MipLevels = byte.Parse(data["mipLevels"])
            };
        }
        catch (Exception ex)
        {
            throw;
        }
    }
    

    public async Task<DTOs.Texture.PHash> GetPHash(AbsolutePath path)
    {
        if (!path.FileExists())
            throw new FileNotFoundException($"Can't hash non-existent file {path}");
            
        await using var tmp = _tempManager.CreateFolder();
        await ConvertImage(path, tmp.Path, 512, 512, 1, DXGI_FORMAT.R8G8B8A8_UNORM, Ext.Png);
            
        using var img = await Image.LoadAsync(path.FileName.RelativeTo(tmp.Path).ReplaceExtension(Ext.Png).ToString());
        img.Mutate(x => x.Resize(512, 512, KnownResamplers.Welch).Grayscale(GrayscaleMode.Bt601));

        return new DTOs.Texture.PHash(ImagePhash.ComputeDigest(new CrossPlatformImageLoader.ImageBitmap((Image<Rgba32>)img)).Coefficients);
    }

    public void Dispose()
    {
        _protonManager?.Dispose();
    }

}