using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using K4os.Compression.LZ4;
using Wabbajack.Common;
using Wabbajack.Compression.BSA.Interfaces;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Xunit;

namespace Wabbajack.Compression.BSA.Test;

public class SseCompressionLevelTests
{
    [Theory]
    [InlineData("sse_compressed.bsa", LZ4Level.L12_MAX)]
    [InlineData("sse_compressed.bsa", LZ4Level.L06_HC)]
    [InlineData("sse_compressed.bsa", LZ4Level.L00_FAST)]
    [InlineData("sse.bsa", LZ4Level.L06_HC)]
    [InlineData("tes5_compressed.bsa", LZ4Level.L06_HC)]
    public async Task RebuiltArchivePreservesEveryFile(string fixture, LZ4Level level)
    {
        var path = KnownFolders.EntryPoint.Combine("TestFiles", fixture);
        var original = await BSADispatch.Open(path);
        var originalFiles = original.Files.ToArray();
        using var temp = new TemporaryFileManager(Path.Combine(Path.GetTempPath(), "jackify-bsa-test-" + Guid.NewGuid()).ToAbsolutePath());
        var bytes = await Rebuild(original, temp, level);
        using var output = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
        var rebuilt = await BSADispatch.Open(new MemoryStreamFactory(output, path, path.LastModifiedUtc()));
        var rebuiltFiles = rebuilt.Files.ToDictionary(f => f.Path);
        Assert.Equal(originalFiles.Length, rebuiltFiles.Count);
        foreach (var file in originalFiles)
        {
            Assert.True(rebuiltFiles.TryGetValue(file.Path, out var restored));
            using var before = new MemoryStream();
            using var after = new MemoryStream();
            await file.CopyDataTo(before, CancellationToken.None);
            await restored.CopyDataTo(after, CancellationToken.None);
            Assert.Equal(before.ToArray(), after.ToArray());
            Assert.Equal(file.Size, restored.Size);
        }
    }

    [Fact]
    public async Task DefaultStillProducesMaximumCompressionBytes()
    {
        var path = KnownFolders.EntryPoint.Combine("TestFiles", "sse_compressed.bsa");
        var original = await BSADispatch.Open(path);
        using var temp = new TemporaryFileManager(Path.Combine(Path.GetTempPath(), "jackify-bsa-test-" + Guid.NewGuid()).ToAbsolutePath());
        var defaultOutput = await Rebuild(original, temp, null);
        var maximumOutput = await Rebuild(original, temp, LZ4Level.L12_MAX);
        Assert.Equal(defaultOutput, maximumOutput);
    }

    private static async Task<byte[]> Rebuild(IReader original, TemporaryFileManager temp, LZ4Level? level)
    {
        var inputs = new List<MemoryStream>();
        try
        {
            await using var builder = level.HasValue
                ? BSADispatch.CreateBuilder(original.State, temp, level.Value)
                : BSADispatch.CreateBuilder(original.State, temp);
            foreach (var file in original.Files)
            {
                // Uncompressed entries retain their input until Build completes.
                var input = new MemoryStream();
                inputs.Add(input);
                await file.CopyDataTo(input, CancellationToken.None);
                input.Position = 0;
                await builder.AddFile(file.State, input, CancellationToken.None);
            }
            using var output = new MemoryStream();
            await builder.Build(output, CancellationToken.None);
            return output.ToArray();
        }
        finally
        {
            foreach (var input in inputs) input.Dispose();
        }
    }
}
