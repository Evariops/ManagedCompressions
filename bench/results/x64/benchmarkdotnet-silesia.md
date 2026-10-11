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
11 to 44 seconds a call: 1 warm-up and 3 measured calls.

NativeCompressions' win-x64 libraries are built with GCC 16.2 (MSYS2); its libzstd has zstd's assembly
Huffman decoder and its BMI2 code. The Native AOT job targets `x86-64-v4` instead of `native`: ILC
11.0.0-rc.1 with `native` on this CPU compiles `AvxVnni.IsSupported` in a branch into a jump to itself,
and BenchmarkDotNet's CPU detection never returns
([dotnet/runtime#135603](https://github.com/dotnet/runtime/issues/135603), and `BenchmarkConfig`). The
codecs use nothing past AVX-512 F, BW, CD, DQ and VL.

```
BenchmarkDotNet v0.16.0-preview.2, Windows 11 (10.0.26200.9448/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 7950X 4.49GHz, 1 CPU, 32 logical and 16 physical cores
Memory: 63.11 GB Total, 35.88 GB Available
.NET SDK 11.0.100-rc.1.26425.128
  [Host]           : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4
  JIT, dynamic PGO : .NET 11.0.0 (11.0.0-rc.1.26425.128, 11.0.26.42628), X64 RyuJIT x86-64-v4
  Native AOT       : .NET 11.0.0-rc.1.26425.128, X64 NativeAOT x86-64-v4
```

## JIT, dynamic PGO

The runtime's default. A `[GlobalSetup]` first runs the managed codec over the first MiB of silesia.tar, in
four passes of 40 rounds with a pause after each, as a running service would have it. The tiered JIT
recompiles a method twice with dynamic PGO, instrumented and then optimized, after 30 calls each time;
BenchmarkDotNet makes 7 to 16 calls here, so a method that one call runs once, as LZ4's compressor is,
would otherwise be measured instrumented.

| Method | Level | Mean | Ratio | Payload | Allocated |
|:--|--:|--:|--:|--:|--:|
| NativeCompressions Zstandard Compress | 1 | 300.75 ms | 1.00 | 69.8MB | - |
| ManagedCompressions.Zstd Compress | 1 | 305.72 ms | 1.02 | 69.8MB | - |
| .NET ZstandardEncoder Compress | 1 | 337.82 ms | 1.12 | 69.8MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 3 | 473.83 ms | 1.00 | 63.07MB | - |
| ManagedCompressions.Zstd Compress | 3 | 512.26 ms | 1.08 | 63.07MB | - |
| .NET ZstandardEncoder Compress | 3 | 492.50 ms | 1.04 | 63.07MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 9 | 1,852.15 ms | 1.00 | 56.34MB | - |
| ManagedCompressions.Zstd Compress | 9 | 2,000.61 ms | 1.08 | 56.34MB | - |
| .NET ZstandardEncoder Compress | 9 | 2,089.15 ms | 1.13 | 56.34MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 1 | 98.11 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 1 | 96.79 ms | 0.99 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 1 | 108.51 ms | 1.11 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 3 | 113.31 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 3 | 105.42 ms | 0.93 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 3 | 119.82 ms | 1.06 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 9 | 105.00 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 9 | 95.83 ms | 0.91 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 9 | 109.55 ms | 1.04 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 19 | 41,659.8 ms | 1.00 | 50.4MB | - |
| ManagedCompressions.Zstd Compress | 19 | 40,188.7 ms | 0.96 | 50.4MB | - |
| .NET ZstandardEncoder Compress | 19 | 43,425.9 ms | 1.04 | 50.4MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 19 | 121.7 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 19 | 111.7 ms | 0.92 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 19 | 120.1 ms | 0.99 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 1 | 205.60 ms | 1.00 | 96.21MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 1 | 216.66 ms | 1.05 | 96.21MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 9 | 3,710.68 ms | 1.00 | 74.28MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 9 | 3,973.03 ms | 1.07 | 74.28MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 1 | 33.08 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 1 | 33.12 ms | 1.00 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 9 | 34.70 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 9 | 34.20 ms | 0.99 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 12 | 11,062.91 ms | 1.00 | 73.68MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 12 | 12,306.28 ms | 1.11 | 73.68MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 12 | 33.62 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 12 | 33.47 ms | 1.00 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress(Default) | – | 488.7 ms | 1.00 | 63.07MB | - |
| BrotliEncoder TryCompress(Default) | – | 1,458.9 ms | 2.99 | 61.18MB | - |
| GZipStream Compress(Level: Optimal) | – | 2,083.6 ms | 4.26 | 65.72MB | 296 B |
| | | | | | |
| NativeCompressions Zstandard Decompress(Default) | – | 113.2 ms | 1.00 | 202.13MB | - |
| BrotliDecoder TryDecompress(Default) | – | 290.2 ms | 2.56 | 202.13MB | - |
| GZipStream Decompress(Level: Optimal) | – | 186.1 ms | 1.64 | 202.13MB | 432 B |

## Native AOT

The same code compiled ahead of time: no tiers, no profile.

| Method | Level | Mean | Ratio | Payload | Allocated |
|:--|--:|--:|--:|--:|--:|
| NativeCompressions Zstandard Compress | 1 | 299.20 ms | 1.00 | 69.8MB | - |
| ManagedCompressions.Zstd Compress | 1 | 299.94 ms | 1.00 | 69.8MB | - |
| .NET ZstandardEncoder Compress | 1 | 365.80 ms | 1.22 | 69.8MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 3 | 474.40 ms | 1.00 | 63.07MB | - |
| ManagedCompressions.Zstd Compress | 3 | 501.63 ms | 1.06 | 63.07MB | - |
| .NET ZstandardEncoder Compress | 3 | 495.39 ms | 1.04 | 63.07MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 9 | 1,891.33 ms | 1.00 | 56.34MB | - |
| ManagedCompressions.Zstd Compress | 9 | 1,911.12 ms | 1.01 | 56.34MB | - |
| .NET ZstandardEncoder Compress | 9 | 2,078.14 ms | 1.10 | 56.34MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 1 | 97.80 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 1 | 97.03 ms | 0.99 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 1 | 111.90 ms | 1.14 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 3 | 112.39 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 3 | 103.96 ms | 0.92 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 3 | 124.08 ms | 1.10 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 9 | 105.12 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 9 | 96.13 ms | 0.91 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 9 | 114.46 ms | 1.09 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress | 19 | 43,260.0 ms | 1.00 | 50.4MB | - |
| ManagedCompressions.Zstd Compress | 19 | 39,533.1 ms | 0.91 | 50.4MB | - |
| .NET ZstandardEncoder Compress | 19 | 40,821.0 ms | 0.94 | 50.4MB | - |
| | | | | | |
| NativeCompressions Zstandard Decompress | 19 | 116.2 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Zstd Decompress | 19 | 104.8 ms | 0.90 | 202.13MB | - |
| .NET ZstandardDecoder Decompress | 19 | 124.1 ms | 1.07 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 1 | 206.16 ms | 1.00 | 96.21MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 1 | 216.47 ms | 1.05 | 96.21MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 9 | 3,711.38 ms | 1.00 | 74.28MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 9 | 4,052.02 ms | 1.09 | 74.28MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 1 | 35.70 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 1 | 32.96 ms | 0.92 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 9 | 34.60 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 9 | 34.75 ms | 1.00 | 202.13MB | - |
| | | | | | |
| NativeCompressions LZ4 Compress(Block) | 12 | 11,090.43 ms | 1.00 | 73.68MB | - |
| ManagedCompressions.Lz4 Compress(Block) | 12 | 12,574.93 ms | 1.13 | 73.68MB | - |
| | | | | | |
| NativeCompressions LZ4 Decompress(Block) | 12 | 33.63 ms | 1.00 | 202.13MB | - |
| ManagedCompressions.Lz4 Decompress(Block) | 12 | 33.95 ms | 1.01 | 202.13MB | - |
| | | | | | |
| NativeCompressions Zstandard Compress(Default) | – | 493.7 ms | 1.00 | 63.07MB | - |
| BrotliEncoder TryCompress(Default) | – | 1,492.1 ms | 3.02 | 61.18MB | - |
| GZipStream Compress(Level: Optimal) | – | 2,115.3 ms | 4.28 | 65.72MB | 296 B |
| | | | | | |
| NativeCompressions Zstandard Decompress(Default) | – | 112.7 ms | 1.00 | 202.13MB | - |
| BrotliDecoder TryDecompress(Default) | – | 296.8 ms | 2.63 | 202.13MB | - |
| GZipStream Decompress(Level: Optimal) | – | 186.5 ms | 1.65 | 202.13MB | 432 B |
