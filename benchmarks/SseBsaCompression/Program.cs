using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using Wabbajack.Common;
using Wabbajack.Compression.BSA;
using Wabbajack.Paths;

// Run from the repository root. No game install or network access is needed.
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var rounds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5;
if (rounds < 3) throw new ArgumentException("Use at least three measured rounds.");
var samples = new List<Sample>();
var archivePath = Path.Combine(root, "Wabbajack.Compression.BSA.Test/TestFiles/sse_compressed.bsa");
var reader = await BSADispatch.Open(archivePath.ToAbsolutePath());
foreach (var file in reader.Files)
{
    using var data = new MemoryStream();
    await file.CopyDataTo(data, CancellationToken.None);
    samples.Add(new Sample("sse_compressed.bsa/" + file.Path, data.ToArray()));
}
foreach (var path in Directory.GetFiles(Path.Combine(root, "Wabbajack.Hashing.PHash.Test/TestData"), "*.dds").Order())
    samples.Add(new Sample(Path.GetRelativePath(root, path), await File.ReadAllBytesAsync(path)));

Console.Error.WriteLine($"Loaded {samples.Count} files, {samples.Sum(s => s.Data.LongLength):N0} bytes; warming all modes.");
var levels = new[] { LZ4Level.L12_MAX, LZ4Level.L06_HC, LZ4Level.L00_FAST };
foreach (var level in levels) await Compress(samples, level);
var results = new List<Result>();
for (var round = 0; round < rounds; round++)
{
    // Rotate execution order to reduce order/thermal bias. Verification is untimed.
    for (var index = 0; index < levels.Length; index++)
    {
        var level = levels[(index + round) % levels.Length];
        var watch = Stopwatch.StartNew();
        var output = await Compress(samples, level);
        watch.Stop();
        for (var i = 0; i < output.Count; i++)
        {
            using var encoded = new MemoryStream(output[i], writable: false);
            await using var decoded = LZ4Stream.Decode(encoded);
            using var restored = new MemoryStream();
            await decoded.CopyToAsync(restored);
            if (!restored.ToArray().AsSpan().SequenceEqual(samples[i].Data))
                throw new InvalidDataException($"Round-trip mismatch: {samples[i].Name}, {level}");
        }
        results.Add(new Result(round + 1, level.ToString(), watch.Elapsed.TotalMilliseconds, output.Sum(b => b.LongLength)));
        Console.Error.WriteLine($"Round {round + 1}: {level}, {watch.Elapsed.TotalMilliseconds:F1} ms, {output.Sum(b => b.LongLength):N0} bytes; verified.");
    }
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    logicalProcessors = Environment.ProcessorCount,
    library = typeof(LZ4Stream).Assembly.GetName().Version?.ToString(),
    scope = "Sequential in-memory LZ4Stream encoding using the SSE builder's CopyToWithStatusAsync; excludes input loading, verification, disk I/O, BSA headers, extraction and texture conversion.",
    samples = samples.Select(s => new { s.Name, bytes = s.Data.Length, sha256 = Convert.ToHexString(SHA256.HashData(s.Data)).ToLowerInvariant() }),
    results
}, new JsonSerializerOptions { WriteIndented = true }));

static async Task<List<byte[]>> Compress(List<Sample> samples, LZ4Level level)
{
    var results = new List<byte[]>(samples.Count);
    foreach (var sample in samples)
    {
        using var input = new MemoryStream(sample.Data, writable: false);
        using var output = new MemoryStream();
        await using (var encoder = LZ4Stream.Encode(output, new LZ4EncoderSettings { CompressionLevel = level }, leaveOpen: true))
            await input.CopyToWithStatusAsync(input.Length, encoder, CancellationToken.None);
        results.Add(output.ToArray());
    }
    return results;
}

record Sample(string Name, byte[] Data);
record Result(int Round, string Level, double Milliseconds, long OutputBytes);
