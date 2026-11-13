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
out at 0.91 to 1.04 times those of the previous run, made alone, 1.01 on average; each ratio compares
cases run on the same cores.

```
BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26300.9711)
AMD Ryzen 9 7950X 4.49GHz, 1 CPU, 32 logical and 16 physical cores
Memory: 63.11 GB Total, 46.26 GB Available
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
| NativeCompressions Zstandard Compress | 1 | 306.51 ms | 1.00 | 69.8MB | - |
| ManagedCompressions.Zstd Compress | 1 | 301.66 ms | 0.98 | 69.8MB | - |
| .NET ZstandardEncoder Compress | 1 | 353.32 ms | 1.15 | 69.8MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 3 | 483.72 ms | 1.00 | 63.07MB | - |
| ManagedCompressions.Zstd Compress | 3 | 499.68 ms | 1.03 | 63.07MB | - |
| .NET ZstandardEncoder Compress | 3 | 501.55 ms | 1.04 | 63.07MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 9 | 1,890.08 ms | 1.00 | 56.34MB | - |
| ManagedCompressions.Zstd Compress | 9 | 2,001.38 ms | 1.06 | 56.34MB | - |
| .NET ZstandardEncoder Compress | 9 | 2,135.34 ms | 1.13 | 56.34MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 1 | 100.49 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 1 | 98.54 ms | 0.98 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 1 | 110.95 ms | 1.10 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 3 | 115.11 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 3 | 106.95 ms | 0.93 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 3 | 121.78 ms | 1.06 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 9 | 105.99 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 9 | 96.70 ms | 0.91 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 9 | 111.05 ms | 1.05 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 19 | 39,354.2 ms | 1.00 | 50.4MB | - |
| ManagedCompressions.Zstd Compress | 19 | 36,123.1 ms | 0.92 | 50.4MB | - |
| .NET ZstandardEncoder Compress | 19 | 39,927.5 ms | 1.01 | 50.4MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 19 | 115.2 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 19 | 106.4 ms | 0.92 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 19 | 119.1 ms | 1.03 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 1 | 211.98 ms | 1.00 | 96.21MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 1 | 222.62 ms | 1.05 | 96.21MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 9 | 3,829.75 ms | 1.00 | 74.28MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 9 | 4,015.90 ms | 1.05 | 74.28MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 1 | 33.96 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 1 | 33.15 ms | 0.98 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 9 | 35.52 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 9 | 34.99 ms | 0.99 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 12 | 11,535.78 ms | 1.00 | 73.68MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 12 | 12,401.69 ms | 1.08 | 73.68MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 12 | 34.58 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 12 | 34.37 ms | 0.99 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress(Default) | – | 483.7 ms | 1.00 | 63.07MB | - |
| BrotliEncoder TryCompress(Default) | – | 1,451.5 ms | 3.00 | 61.18MB | - |
| GZipStream Compress(Level: Optimal) | – | 2,155.9 ms | 4.46 | 65.72MB | 296 B |
| | | | | | |
| NativeCompressions Zstandard Decompress(Default) | – | 115.6 ms | 1.00 | 202.13MB | - |
| BrotliDecoder TryDecompress(Default) | – | 296.1 ms | 2.56 | 202.13MB | - |
| GZipStream Decompress(Level: Optimal) | – | 191.9 ms | 1.66 | 202.13MB | 432 B |

## Native AOT

The same code compiled ahead of time: no tiers, no profile.

| Method | Level | Mean | Ratio | Payload | Allocated |
|:--|--:|--:|--:|--:|--:|
| NativeCompressions Zstandard Compress | 1 | 306.94 ms | 1.00 | 69.8MB | - |
| ManagedCompressions.Zstd Compress | 1 | 303.70 ms | 0.99 | 69.8MB | - |
| .NET ZstandardEncoder Compress | 1 | 376.48 ms | 1.23 | 69.8MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 3 | 485.89 ms | 1.00 | 63.07MB | - |
| ManagedCompressions.Zstd Compress | 3 | 501.40 ms | 1.03 | 63.07MB | - |
| .NET ZstandardEncoder Compress | 3 | 503.24 ms | 1.04 | 63.07MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 9 | 1,914.30 ms | 1.00 | 56.34MB | - |
| ManagedCompressions.Zstd Compress | 9 | 1,911.86 ms | 1.00 | 56.34MB | - |
| .NET ZstandardEncoder Compress | 9 | 2,064.12 ms | 1.08 | 56.34MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 1 | 100.10 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 1 | 99.39 ms | 0.99 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 1 | 113.43 ms | 1.13 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 3 | 115.02 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 3 | 104.81 ms | 0.91 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 3 | 125.36 ms | 1.09 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 9 | 106.89 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 9 | 96.07 ms | 0.90 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 9 | 114.87 ms | 1.07 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 19 | 39,220.9 ms | 1.00 | 50.4MB | - |
| ManagedCompressions.Zstd Compress | 19 | 36,755.1 ms | 0.94 | 50.4MB | - |
| .NET ZstandardEncoder Compress | 19 | 38,225.2 ms | 0.97 | 50.4MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 19 | 115.2 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 19 | 103.0 ms | 0.89 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 19 | 122.5 ms | 1.06 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 1 | 212.10 ms | 1.00 | 96.21MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 1 | 221.94 ms | 1.05 | 96.21MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 9 | 3,832.51 ms | 1.00 | 74.28MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 9 | 4,180.66 ms | 1.09 | 74.28MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 1 | 33.95 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 1 | 33.89 ms | 1.00 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 9 | 35.52 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 9 | 35.79 ms | 1.01 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 12 | 11,460.07 ms | 1.00 | 73.68MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 12 | 12,815.36 ms | 1.12 | 73.68MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 12 | 34.52 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 12 | 34.85 ms | 1.01 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress(Default) | – | 485.4 ms | 1.00 | 63.07MB | - |
| BrotliEncoder TryCompress(Default) | – | 1,473.0 ms | 3.03 | 61.18MB | - |
| GZipStream Compress(Level: Optimal) | – | 2,162.2 ms | 4.45 | 65.72MB | 296 B |
| | | | | | |
| NativeCompressions Zstandard Decompress(Default) | – | 115.6 ms | 1.00 | 202.13MB | - |
| BrotliDecoder TryDecompress(Default) | – | 304.6 ms | 2.63 | 202.13MB | - |
| GZipStream Decompress(Level: Optimal) | – | 200.2 ms | 1.73 | 202.13MB | 432 B |