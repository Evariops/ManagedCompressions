# silesia.tar with BenchmarkDotNet

`bench/ManagedCompressions.Benchmarks`, after NativeCompressions' own (sandbox/Benchmark/AllCompressDecompress.cs):
silesia.tar (211,948,032 bytes) compressed or decompressed whole by one call. Ratio is a case's mean over
its group's NativeCompressions case: below 1.00 is faster. Payload is the compressed size, or the
content's. Every decoder reads NativeCompressions' output, which the managed compressors write too.

**Machine: Apple M4 Pro, arm64 (AArch64), macOS.** The x64 results are in
[`../x64/benchmarkdotnet-silesia.md`](../x64/benchmarkdotnet-silesia.md).

- **Zstandard:** levels 1, 3 (the default), 9 and 19, against NativeCompressions and .NET 11's
  `ZstandardEncoder` and `ZstandardDecoder`.
- **LZ4:** levels 1 (`LZ4_compress_default`), 9 (lz4's default high-compression level) and 12. The block
  API of NativeCompressions has no level, so its levels 9 and 12 call `LZ4_compress_HC` through its public
  interop, `LZ4NativeMethods`.
- **Brotli and GZip,** at their default levels, for scale against Zstandard's default.

Each case runs 3 warm-up and 10 measured calls, except Zstandard's level 19 and LZ4's level 12, which take
12 to 45 seconds a call: 1 warm-up and 3 measured calls.

```
BenchmarkDotNet v0.16.0-preview.2, macOS Tahoe 26.7.1 (25G241) [Darwin 25.6.0]
Apple M4 Pro, 1 CPU, 14 logical and 14 physical cores
Memory: 48 GB Total, 2.13 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]           : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), Arm64 RyuJIT armv8.0-a
  JIT, dynamic PGO : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), Arm64 RyuJIT armv8.0-a
  Native AOT       : .NET 11.0.0-rc.1.26425.128, Arm64 NativeAOT armv8.0-a
```

## JIT, dynamic PGO

The runtime's default. A `[GlobalSetup]` first runs the managed codec over the first MiB of silesia.tar, in
four passes of 40 rounds with a pause after each, as a running service would have it. The tiered JIT
recompiles a method twice with dynamic PGO, instrumented and then optimized, after 30 calls each time;
BenchmarkDotNet makes 7 to 16 calls here, so a method that one call runs once, as LZ4's compressor is,
would otherwise be measured instrumented.

| Method | Level | Mean | Ratio | Payload | Allocated |
|:--|--:|--:|--:|--:|--:|
| NativeCompressions Zstandard Compress | 1 | 279.24 ms | 1.00 | 69.8MB | - |
| ManagedCompressions.Zstd Compress | 1 | 278.92 ms | 1.00 | 69.8MB | - |
| .NET ZstandardEncoder Compress | 1 | 275.23 ms | 0.99 | 69.8MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 3 | 468.60 ms | 1.00 | 63.07MB | - |
| ManagedCompressions.Zstd Compress | 3 | 459.41 ms | 0.98 | 63.07MB | - |
| .NET ZstandardEncoder Compress | 3 | 473.21 ms | 1.01 | 63.07MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 9 | 1,847.62 ms | 1.00 | 56.34MB | - |
| ManagedCompressions.Zstd Compress | 9 | 1,948.40 ms | 1.05 | 56.34MB | - |
| .NET ZstandardEncoder Compress | 9 | 1,872.74 ms | 1.01 | 56.34MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 1 | 116.16 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 1 | 89.13 ms | 0.77 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 1 | 115.45 ms | 0.99 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 3 | 127.42 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 3 | 87.85 ms | 0.69 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 3 | 126.67 ms | 0.99 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 9 | 113.16 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 9 | 78.92 ms | 0.70 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 9 | 112.18 ms | 0.99 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 19 | 43,048.52 ms | 1.00 | 50.4MB | 360 B |
| ManagedCompressions.Zstd Compress | 19 | 45,630.14 ms | 1.06 | 50.4MB | 360 B |
| .NET ZstandardEncoder Compress | 19 | 46,402.42 ms | 1.08 | 50.4MB | 360 B |
| | | | | | |
| NativeCompressions Zstandard Decompress | 19 | 122.68 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 19 | 82.59 ms | 0.67 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 19 | 120.49 ms | 0.98 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 1 | 222.74 ms | 1.00 | 96.21MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 1 | 232.09 ms | 1.04 | 96.21MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 9 | 4,071.60 ms | 1.00 | 74.28MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 9 | 4,382.46 ms | 1.08 | 74.28MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 1 | 35.62 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 1 | 34.33 ms | 0.96 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 9 | 37.27 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 9 | 36.10 ms | 0.97 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 12 | 11,933.05 ms | 1.00 | 73.68MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 12 | 13,685.70 ms | 1.15 | 73.68MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 12 | 36.46 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 12 | 35.07 ms | 0.96 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress(Default) | – | 467.8 ms | 1.00 | 63.07MB | - |
| BrotliEncoder TryCompress(Default) | – | 1,161.6 ms | 2.48 | 61.18MB | - |
| GZipStream Compress(Level: Optimal) | – | 1,649.1 ms | 3.53 | 65.72MB | 296 B |
| | | | | | |
| NativeCompressions Zstandard Decompress(Default) | – | 128.4 ms | 1.00 | 202.13MB | - |
| BrotliDecoder TryDecompress(Default) | – | 309.4 ms | 2.41 | 202.13MB | - |
| GZipStream Decompress(Level: Optimal) | – | 269.7 ms | 2.10 | 202.13MB | 432 B |

## Native AOT

The same code compiled ahead of time: no tiers, no profile.

| Method | Level | Mean | Ratio | Payload | Allocated |
|:--|--:|--:|--:|--:|--:|
| NativeCompressions Zstandard Compress | 1 | 278.91 ms | 1.00 | 69.8MB | - |
| ManagedCompressions.Zstd Compress | 1 | 275.03 ms | 0.99 | 69.8MB | - |
| .NET ZstandardEncoder Compress | 1 | 275.98 ms | 0.99 | 69.8MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 3 | 468.68 ms | 1.00 | 63.07MB | - |
| ManagedCompressions.Zstd Compress | 3 | 455.54 ms | 0.97 | 63.07MB | - |
| .NET ZstandardEncoder Compress | 3 | 468.31 ms | 1.00 | 63.07MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 9 | 1,819.34 ms | 1.00 | 56.34MB | - |
| ManagedCompressions.Zstd Compress | 9 | 1,900.84 ms | 1.04 | 56.34MB | - |
| .NET ZstandardEncoder Compress | 9 | 1,850.14 ms | 1.02 | 56.34MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 1 | 115.28 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 1 | 88.66 ms | 0.77 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 1 | 115.29 ms | 1.00 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 3 | 127.40 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 3 | 87.09 ms | 0.68 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 3 | 126.93 ms | 1.00 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 9 | 113.25 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 9 | 78.76 ms | 0.70 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 9 | 114.29 ms | 1.01 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 19 | 45,453.24 ms | 1.00 | 50.4MB | - |
| ManagedCompressions.Zstd Compress | 19 | 44,623.78 ms | 0.98 | 50.4MB | - |
| .NET ZstandardEncoder Compress | 19 | 46,128.87 ms | 1.01 | 50.4MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 19 | 120.98 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 19 | 82.56 ms | 0.68 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 19 | 121.02 ms | 1.00 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 1 | 221.14 ms | 1.00 | 96.21MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 1 | 230.61 ms | 1.04 | 96.21MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 9 | 4,065.67 ms | 1.00 | 74.28MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 9 | 4,424.30 ms | 1.09 | 74.28MB | 64 B |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 1 | 35.49 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 1 | 34.37 ms | 0.97 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 9 | 37.14 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 9 | 35.99 ms | 0.97 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 12 | 11,697.24 ms | 1.00 | 73.68MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 12 | 13,820.99 ms | 1.18 | 73.68MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 12 | 36.41 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 12 | 34.96 ms | 0.96 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress(Default) | – | 469.0 ms | 1.00 | 63.07MB | - |
| BrotliEncoder TryCompress(Default) | – | 1,166.0 ms | 2.49 | 61.18MB | - |
| GZipStream Compress(Level: Optimal) | – | 1,668.8 ms | 3.56 | 65.72MB | 296 B |
| | | | | | |
| NativeCompressions Zstandard Decompress(Default) | – | 128.0 ms | 1.00 | 202.13MB | - |
| BrotliDecoder TryDecompress(Default) | – | 308.1 ms | 2.41 | 202.13MB | - |
| GZipStream Decompress(Level: Optimal) | – | 270.0 ms | 2.11 | 202.13MB | 432 B |
