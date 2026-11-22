# silesia.tar with BenchmarkDotNet

`bench/ManagedCompressions.Benchmarks`, after NativeCompressions' own (sandbox/Benchmark/AllCompressDecompress.cs):
silesia.tar (211,948,032 bytes) compressed or decompressed whole by one call. Ratio is a case's mean over
its group's NativeCompressions case: below 1.00 is faster. Payload is the compressed size, or the
content's. Every decoder reads NativeCompressions' output, which the managed compressors write too.

**Machine: AMD Ryzen 9 7950X, x64 (Zen 4, AVX-512), Windows 11.** The arm64 results are in
[`../arm64/benchmarkdotnet-silesia.md`](../arm64/benchmarkdotnet-silesia.md).

- **Zstandard:** levels 1, 3 (the default), 9 and 19, against NativeCompressions and .NET 11's
  `ZstandardEncoder` and `ZstandardDecoder`.
- **LZ4:** levels 1 (`LZ4_compress_default`), 9 (lz4's default high-compression level) and 12. The block
  API of NativeCompressions has no level, so its levels 9 and 12 call `LZ4_compress_HC` through its public
  interop, `LZ4NativeMethods`.
- **Brotli and GZip,** at their default levels, for scale against Zstandard's default.

Each case runs 3 warm-up and 10 measured calls, except Zstandard's level 19 and LZ4's level 12, which take
11 to 40 seconds a call: 1 warm-up and 3 measured calls.

NativeCompressions.Zstandard 1.3.0 and NativeCompressions.LZ4 1.2.0 ship the win-x64 libraries of its
1.1.0, built with GCC 16.2 (MSYS2); its libzstd has zstd's assembly Huffman decoder and its BMI2 code. The
Native AOT job targets `x86-64-v4` instead of `native`: ILC with `native` on this CPU, from 11.0.0-rc.1 to
11.0.0, compiles `AvxVnni.IsSupported` in a branch into a jump to itself, and BenchmarkDotNet's CPU
detection never returns ([dotnet/runtime#135603](https://github.com/dotnet/runtime/issues/135603), and
`BenchmarkConfig`). The codecs use nothing past AVX-512 F, BW, CD, DQ and VL.

The run was confined to the processor's second CCD, logical processors 16 to 31
(`start /affinity FFFF0000`), while the level-by-level benchmark ran on the first. Its native times came
out at 0.99 to 1.02 times those of the previous run, made the same way, 1.00 on average; each ratio
compares cases run on the same cores.

LZ4's rows are those of its safe code, which indexes spans and has no unsafe context: a run of the LZ4
classes alone (`--filter *Lz4*`), on the same cores, while builds and tests ran on the first CCD. The
native times came out at 1.00 to 1.03 times those of the run above.

```
BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26300.9911)
AMD Ryzen 9 7950X 4.49GHz, 1 CPU, 32 logical and 16 physical cores
Memory: 63.11 GB Total, 41.55 GB Available
.NET SDK 11.0.100
  [Host]           : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  JIT, dynamic PGO : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v4
  Native AOT       : .NET 11.0.0, X64 NativeAOT x86-64-v4
```

## JIT, dynamic PGO

The runtime's default. A `[GlobalSetup]` first runs the managed codec over the first MiB of silesia.tar, in
four passes of 40 rounds with a pause after each, as a running service would have it. The tiered JIT
recompiles a method twice with dynamic PGO, instrumented and then optimized, after 30 calls each time;
BenchmarkDotNet makes 7 to 16 calls here, so a method that one call runs once, as LZ4's compressor is,
would otherwise be measured instrumented.

| Method | Level | Mean | Ratio | Payload | Allocated |
|:--|--:|--:|--:|--:|--:|
| NativeCompressions Zstandard Compress | 1 | 307.07 ms | 1.00 | 69.8MB | - |
| ManagedCompressions.Zstd Compress | 1 | 299.11 ms | 0.97 | 69.8MB | - |
| .NET ZstandardEncoder Compress | 1 | 347.37 ms | 1.13 | 69.8MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 3 | 486.88 ms | 1.00 | 63.07MB | - |
| ManagedCompressions.Zstd Compress | 3 | 498.58 ms | 1.02 | 63.07MB | - |
| .NET ZstandardEncoder Compress | 3 | 503.38 ms | 1.03 | 63.07MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 9 | 1,897.21 ms | 1.00 | 56.34MB | - |
| ManagedCompressions.Zstd Compress | 9 | 1,953.28 ms | 1.03 | 56.34MB | - |
| .NET ZstandardEncoder Compress | 9 | 2,132.92 ms | 1.12 | 56.34MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 1 | 100.70 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 1 | 98.61 ms | 0.98 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 1 | 111.25 ms | 1.10 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 3 | 115.48 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 3 | 106.96 ms | 0.93 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 3 | 122.26 ms | 1.06 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 9 | 106.55 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 9 | 97.41 ms | 0.91 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 9 | 111.81 ms | 1.05 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 19 | 39,231.4 ms | 1.00 | 50.4MB | - |
| ManagedCompressions.Zstd Compress | 19 | 36,901.0 ms | 0.94 | 50.4MB | - |
| .NET ZstandardEncoder Compress | 19 | 40,199.1 ms | 1.02 | 50.4MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 19 | 115.8 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 19 | 106.6 ms | 0.92 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 19 | 119.1 ms | 1.03 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 1 | 212.35 ms | 1.00 | 96.21MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 1 | 279.63 ms | 1.32 | 96.21MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 9 | 3,826.80 ms | 1.00 | 74.28MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 9 | 4,476.24 ms | 1.17 | 74.28MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 1 | 33.93 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 1 | 44.78 ms | 1.32 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 9 | 35.56 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 9 | 47.19 ms | 1.33 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 12 | 11,799.66 ms | 1.00 | 73.68MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 12 | 13,426.25 ms | 1.14 | 73.68MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 12 | 34.58 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 12 | 46.48 ms | 1.34 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress(Default) | – | 487.1 ms | 1.00 | 63.07MB | - |
| BrotliEncoder TryCompress(Default) | – | 1,451.9 ms | 2.98 | 61.18MB | - |
| GZipStream Compress(Level: Optimal) | – | 2,183.4 ms | 4.48 | 65.72MB | 296 B |
| | | | | | |
| NativeCompressions Zstandard Decompress(Default) | – | 115.6 ms | 1.00 | 202.13MB | - |
| BrotliDecoder TryDecompress(Default) | – | 295.5 ms | 2.56 | 202.13MB | - |
| GZipStream Decompress(Level: Optimal) | – | 192.0 ms | 1.66 | 202.13MB | 432 B |

## Native AOT

The same code compiled ahead of time: no tiers, no profile.

| Method | Level | Mean | Ratio | Payload | Allocated |
|:--|--:|--:|--:|--:|--:|
| NativeCompressions Zstandard Compress | 1 | 306.64 ms | 1.00 | 69.8MB | - |
| ManagedCompressions.Zstd Compress | 1 | 304.46 ms | 0.99 | 69.8MB | - |
| .NET ZstandardEncoder Compress | 1 | 374.78 ms | 1.22 | 69.8MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 3 | 485.73 ms | 1.00 | 63.07MB | - |
| ManagedCompressions.Zstd Compress | 3 | 503.30 ms | 1.04 | 63.07MB | - |
| .NET ZstandardEncoder Compress | 3 | 503.45 ms | 1.04 | 63.07MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 9 | 1,946.24 ms | 1.00 | 56.34MB | - |
| ManagedCompressions.Zstd Compress | 9 | 1,894.18 ms | 0.97 | 56.34MB | - |
| .NET ZstandardEncoder Compress | 9 | 2,064.32 ms | 1.06 | 56.34MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 1 | 100.78 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 1 | 97.62 ms | 0.97 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 1 | 114.47 ms | 1.14 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 3 | 115.69 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 3 | 104.80 ms | 0.91 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 3 | 126.49 ms | 1.09 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 9 | 107.17 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 9 | 96.35 ms | 0.90 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 9 | 115.61 ms | 1.08 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 19 | 39,699.8 ms | 1.00 | 50.4MB | - |
| ManagedCompressions.Zstd Compress | 19 | 36,896.5 ms | 0.93 | 50.4MB | - |
| .NET ZstandardEncoder Compress | 19 | 38,592.4 ms | 0.97 | 50.4MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 19 | 115.0 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 19 | 104.3 ms | 0.91 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 19 | 123.6 ms | 1.07 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 1 | 212.73 ms | 1.00 | 96.21MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 1 | 282.95 ms | 1.33 | 96.21MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 9 | 3,807.50 ms | 1.00 | 74.28MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 9 | 4,690.92 ms | 1.23 | 74.28MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 1 | 33.99 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 1 | 45.10 ms | 1.33 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 9 | 35.61 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 9 | 47.36 ms | 1.33 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 12 | 11,496.80 ms | 1.00 | 73.68MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 12 | 14,580.65 ms | 1.27 | 73.68MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 12 | 34.60 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 12 | 46.10 ms | 1.33 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress(Default) | – | 486.5 ms | 1.00 | 63.07MB | - |
| BrotliEncoder TryCompress(Default) | – | 1,481.9 ms | 3.05 | 61.18MB | - |
| GZipStream Compress(Level: Optimal) | – | 2,151.6 ms | 4.42 | 65.72MB | 296 B |
| | | | | | |
| NativeCompressions Zstandard Decompress(Default) | – | 115.2 ms | 1.00 | 202.13MB | - |
| BrotliDecoder TryDecompress(Default) | – | 303.1 ms | 2.63 | 202.13MB | - |
| GZipStream Decompress(Level: Optimal) | – | 190.3 ms | 1.65 | 202.13MB | 432 B |
