# SSE BSA compression benchmark

The SSE BSA builder uses K4os LZ4 stream compression at `L12_MAX` for each compressed entry. This benchmark isolates that operation to measure the CPU/size tradeoff of lower levels. It uses the same `LZ4Stream.Encode` settings and `CopyToWithStatusAsync` path as the builder.

## Reproduce

From the repository root, with the .NET SDK specified by `global.json`:

```sh
dotnet build benchmarks/SseBsaCompression -c Release
dotnet run --no-build --project benchmarks/SseBsaCompression -c Release -- . 7 > compression-results.json
dotnet test Wabbajack.Compression.BSA.Test -c Release
dotnet build jackify-engine -c Release
```

Package restore needs network access on the first run. The benchmark itself needs no game installation, credentials, downloads, or external assets. Progress goes to stderr; stdout contains JSON with timings, compressed sizes, environment details, and SHA-256 hashes of every extracted input.

## Measured result

Apple M1 Pro, 16 GiB RAM, macOS arm64, .NET SDK 8.0.108 / runtime 8.0.8, K4os.Compression.LZ4.Streams 1.3.8. Measured on 2026-09-06 against the encoding path at source revision `c9e3561cd3b9f0a91a3702729633d49dc1f57326`.

| Mode | Median encoding time | Speedup vs maximum | Compressed bytes | Size increase |
| --- | ---: | ---: | ---: | ---: |
| maximum (`L12_MAX`) | 7,974.24 ms | 1.00x | 13,380,856 | — |
| balanced (`L06_HC`) | 488.59 ms | 16.32x | 13,461,063 | 0.60% |
| fast (`L00_FAST`) | 95.38 ms | 83.61x | 15,089,766 | 12.77% |

Seven measured rounds per mode, after one full warmup per mode. Execution order rotates each round. All 775 files are separately encoded and decoded in every measured round: **16,275 byte-for-byte round-trip checks passed**. Input loading and verification are outside the timer. Timing includes stream allocation and collecting the encoded byte arrays. Output sizes were identical across rounds for each mode.

Ranges were 7,478.60–11,475.61 ms (maximum), 471.47–552.57 ms (balanced), and 79.55–423.10 ms (fast). These are medians, not best-case runs; the raw rounds are retained in [results/macos-arm64-m1-pro.json](results/macos-arm64-m1-pro.json). That compact record replaces the generated per-file manifest with hashes of the six repository fixture inputs and adds hardware/source metadata.

The corpus is **775 files / 32,746,506 uncompressed bytes**: all entries in `Wabbajack.Compression.BSA.Test/TestFiles/sse_compressed.bsa`, plus the five DDS files in `Wabbajack.Hashing.PHash.Test/TestData`. It contains 98 DDS textures, 211 NIF meshes, and 466 PEX scripts. These are existing repository fixtures, not a sample taken from a Tuxborn installation.

## Meaning and limits

Balanced mode is a promising opt-in for CPU-bound archive rebuilding: in this corpus, maximum compression spends about 16 times as long to save another 80,207 bytes. Both modes are lossless; this does not lower texture resolution or visual quality.

This is **not an end-to-end installation benchmark**. It runs sequentially in memory on an ARM Mac, while the installer compresses entries concurrently and also extracts, writes, hashes, verifies, and converts textures. The benchmark excludes disk I/O, archive headers, the slab allocator, and the extra archive hash pass used by alternate compression modes. CPU architecture, file contents, memory pressure, SD-card performance, and parallel scheduling can change the result. No Steam Deck speedup, installation ETA, game-loading result, or total-install size increase has been measured.

Before considering a default change, measure a complete installation on Linux/Steam Deck, compare archive-building and total elapsed time, verify the resulting files, launch the game, and exercise update/reinstall behavior. A 16.32x speedup in this operation cannot be applied to the whole installation: even if it accounted for half of total elapsed time, the idealized overall speedup would only be about 1.88x, before additional overhead.

## Installer option and compatibility

The engine's `install` command accepts `--sse-bsa-compression maximum|balanced|fast`. Omission retains `maximum`; the Jackify GUI is unchanged. The option changes only compressed entries in SSE-format BSAs. Existing non-SSE archive compression and texture conversion paths remain unchanged.

Alternate modes change compressed archive bytes and sizes. The installer retains its existing extracted-file verification and stores the actual rebuilt archive hash instead of the modlist's expected hash. This adds one sequential read of each alternate-mode SSE BSA. **A later install/update may rebuild these archives** because their hashes no longer match the modlist. Do not use alternate modes when exact archive-byte reproduction is required. Existing completed archives are not proactively recompressed by selecting an option.

The compression test suite rebuilds complete SSE fixtures at all three levels, compares every extracted path, size and byte, exercises uncompressed SSE and compressed TES5 fixtures, and verifies that omitted/default settings produce identical bytes to explicit maximum compression.

Validation on this Mac covers the archive suite, engine compilation and CLI help. A full CLI install invocation is blocked by the engine's existing native SQLite dependency (`SQLite.Interop.dll` is unavailable on this macOS arm64 setup); Linux installation and runtime validation remain outstanding.
