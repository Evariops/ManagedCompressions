# ManagedCompressions

**Zstandard and LZ4 written entirely in C#.** No native library and no P/Invoke: the code runs wherever
.NET runs on a little-endian processor, trims, and compiles with Native AOT.

- **Same bytes as the native libraries.** Every frame and block is byte-for-byte what libzstd 1.5.7 and
  lz4 1.10.0 write, at every level, so the compression ratios are theirs. The tests check this.
- **Faster where it counts.** Zstandard decompresses **1.3 to 1.5× faster** than native libzstd on arm64,
  and 1.0 to 1.2× on x64, where native libzstd has hand-written assembly. LZ4 decompresses 2 to 4 % faster
  than native lz4 on arm64, and within 2 % of it on x64. On small records, compression is up to **1.5×
  faster** with Zstandard and up to **5.7×** with LZ4.
- **At native speed elsewhere.** Zstandard compresses large inputs from 6 % slower to 10 % faster than
  libzstd. LZ4 compresses 2 to 6 % slower at level 1, and up to 15 % slower at its high-compression levels.
- **No allocation per call.** A compressor or decompressor is created once and reused.

The speeds are measured against [NativeCompressions](https://github.com/Cysharp/NativeCompressions),
Cysharp's binding of the native libraries, and against .NET 11's own `System.IO.Compression` Zstandard.

> [!NOTE]
> **The figures in this README come from two machines:** an Apple M4 Pro (arm64) under macOS, and an AMD
> Ryzen 9 7950X (x64) under Windows 11. The relative speeds differ between them: on x64, native libzstd
> decodes with hand-written assembly, and the managed decompressor's lead is smaller.

## At a glance

Apple M4 Pro (arm64) and AMD Ryzen 9 7950X (x64). silesia.tar (211 MB) compressed or decompressed in one
call, on one thread, with BenchmarkDotNet. Native is NativeCompressions; JIT and AOT are the managed code
under the JIT and under Native AOT. Throughputs are in MB/s of uncompressed data, and × is the managed
speed divided by the native speed on the same machine: **above 1.00 is faster**, and bold is at least
10 % faster.

The levels are the ones in use. For Zstandard: 1, the fastest of the regular levels; 3, the default of
zstd, NativeCompressions and .NET; 9; and 19, the highest before zstd's `--ultra` levels. For LZ4: 1,
`LZ4_compress_default`; 9, lz4's default high-compression level; and 12, its highest.

### Zstandard

**Apple M4 Pro, arm64**

| | Level | Native, MB/s | JIT, MB/s | JIT × | AOT, MB/s | AOT × |
|:--|--:|--:|--:|--:|--:|--:|
| **Decompression** | 1 | 1,825 | 2,378 | **1.30×** | 2,391 | **1.30×** |
|  | 3 | 1,663 | 2,413 | **1.45×** | 2,434 | **1.46×** |
|  | 9 | 1,873 | 2,686 | **1.43×** | 2,691 | **1.44×** |
|  | 19 | 1,728 | 2,566 | **1.49×** | 2,567 | **1.47×** |
| **Compression** | 1 | 759 | 760 | 1.00× | 771 | 1.01× |
|  | 3 | 452 | 461 | 1.02× | 465 | 1.03× |
|  | 9 | 115 | 109 | 0.95× | 112 | 0.96× |
|  | 19 | 4.9 | 4.6 | 0.94× | 4.7 | 1.02× |

**AMD Ryzen 9 7950X, x64**

| | Level | Native, MB/s | JIT, MB/s | JIT × | AOT, MB/s | AOT × |
|:--|--:|--:|--:|--:|--:|--:|
| **Decompression** | 1 | 2,109 | 2,151 | 1.02× | 2,132 | 1.01× |
|  | 3 | 1,841 | 1,982 | 1.08× | 2,022 | **1.10×** |
|  | 9 | 2,000 | 2,192 | **1.10×** | 2,206 | **1.11×** |
|  | 19 | 1,840 | 1,992 | 1.08× | 2,058 | **1.12×** |
| **Compression** | 1 | 691 | 703 | 1.02× | 698 | 1.01× |
|  | 3 | 438 | 424 | 0.97× | 423 | 0.97× |
|  | 9 | 112 | 106 | 0.94× | 111 | 1.00× |
|  | 19 | 5.4 | 5.9 | 1.09× | 5.8 | 1.07× |

### LZ4

**Apple M4 Pro, arm64**

| | Level | Native, MB/s | JIT, MB/s | JIT × | AOT, MB/s | AOT × |
|:--|--:|--:|--:|--:|--:|--:|
| **Decompression** | 1 | 5,950 | 6,174 | 1.04× | 6,167 | 1.03× |
|  | 9 | 5,687 | 5,871 | 1.03× | 5,889 | 1.03× |
|  | 12 | 5,813 | 6,044 | 1.04× | 6,063 | 1.04× |
| **Compression** | 1 | 952 | 913 | 0.96× | 919 | 0.96× |
|  | 9 | 52 | 48 | 0.93× | 48 | 0.92× |
|  | 12 | 17.8 | 15.5 | 0.87× | 15.3 | 0.85× |

**AMD Ryzen 9 7950X, x64**

| | Level | Native, MB/s | JIT, MB/s | JIT × | AOT, MB/s | AOT × |
|:--|--:|--:|--:|--:|--:|--:|
| **Decompression** | 1 | 6,241 | 6,394 | 1.02× | 6,254 | 1.00× |
|  | 9 | 5,967 | 6,057 | 1.02× | 5,922 | 0.99× |
|  | 12 | 6,129 | 6,167 | 1.01× | 6,082 | 0.99× |
| **Compression** | 1 | 1,000 | 952 | 0.95× | 955 | 0.96× |
|  | 9 | 55 | 53 | 0.95× | 51 | 0.92× |
|  | 12 | 18.4 | 17.1 | 0.93× | 16.5 | 0.89× |

.NET 11's own Zstandard (`ZstandardEncoder`, `ZstandardDecoder`) is the same libzstd. On arm64 it runs at
NativeCompressions' speed: 0.98× to 1.02× of it, except for level 19's compression under the JIT (0.93×).
That exception is noise. Zstandard's level 19 and LZ4's level 12 take 11 to 45 seconds a call, so they are
measured on three calls instead of ten, and at level 19 NativeCompressions' own time varied by 5 % from one
job to the other (43.0 s, then 45.5 s) where .NET's and the managed code's varied by 1 to 2 %: read level
19's compression row as on par. On x64, .NET's libzstd is slower than NativeCompressions': 0.82× to 1.03×
of it. At level 19 there, NativeCompressions took 39.4 s, then 39.2 s, and .NET 39.9 s, then 38.2 s. The
full BenchmarkDotNet tables, with Brotli and GZip for scale, are in
[`bench/results/arm64/benchmarkdotnet-silesia.md`](https://github.com/Evariops/ManagedCompressions/blob/main/bench/results/arm64/benchmarkdotnet-silesia.md) and
[`bench/results/x64/benchmarkdotnet-silesia.md`](https://github.com/Evariops/ManagedCompressions/blob/main/bench/results/x64/benchmarkdotnet-silesia.md).

## Quick start

The packages target .NET 10 and .NET 11:

```sh
dotnet add package ManagedCompressions.Zstd
dotnet add package ManagedCompressions.Lz4
```

### Zstandard

```csharp
using ManagedCompressions.Zstd;

var compressor = new ZstdCompressor(level: 3);        // -131072 to 22; reuse it, it keeps its tables
byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
compressor.Compress(data, frame, out _, out int frameLength);

var decompressor = new ZstdDecompressor();           // reuse it too
ZstdDecompressor.TryGetFrameContentSize(frame, out ulong contentSize);
byte[] content = new byte[contentSize];
decompressor.Decompress(frame.AsSpan(0, frameLength), content, out _, out _);
```

Dictionaries (`new ZstdCompressor(level, dictionary)`), checksums (`AppendChecksum = true`) and parallel
compression (`CompressAsync`, which writes the same frames as libzstd with worker threads) are covered in
[`src/ManagedCompressions.Zstd/README.md`](https://github.com/Evariops/ManagedCompressions/blob/main/src/ManagedCompressions.Zstd/README.md).

### LZ4

```csharp
using ManagedCompressions.Lz4;

var compressor = new Lz4Compressor(level: 1);        // 1 = LZ4_compress_default, below 1 = faster, 2-12 = high compression
byte[] block = new byte[Lz4Compressor.GetMaxCompressedLength(data.Length)];
int blockLength = compressor.Compress(data, block);

byte[] content = new byte[data.Length];              // a block does not record its size: store it beside
Lz4Block.Decompress(block.AsSpan(0, blockLength), content);
```

`Lz4Block.TryDecompress` reads into a larger destination and returns false on a corrupt block.
`Lz4Compressor.TryCompress` returns false when the destination is too small.

## Performance, level by level

Under the JIT, on two machines: an Apple M4 Pro (arm64, macOS) and an AMD Ryzen 9 7950X (x64,
Windows). Each cell is the managed speed divided by the native speed (NativeCompressions at its fastest,
with its context reused), on that machine. **Bold** means at least 10 % faster. The workloads are:

- **silesia.tar** as one 211 MB frame or block;
- the same file cut into **64 KiB pages**, each its own frame or block, as a columnar file or a network
  protocol compresses data;
- **500 JSON records** of about 800 bytes each, zstd's own regression data, each its own frame or block;
- zstd only: the same records **with their 110 KiB dictionary**.

### Zstandard

| Decompression | | 1 | 3 | 5 | 7 | 9 | 13 | 16 | 19 |
|:--|:--|--:|--:|--:|--:|--:|--:|--:|--:|
| silesia.tar, one frame | arm64 | **1.29×** | **1.47×** | **1.48×** | **1.46×** | **1.44×** | **1.43×** | **1.39×** | **1.47×** |
|  | x64 | 1.01× | 1.09× | **1.10×** | 1.09× | 1.09× | 1.08× | 1.07× | 1.09× |
| silesia.tar, 64 KiB pages | arm64 | **1.26×** | **1.36×** | **1.46×** | **1.42×** | **1.42×** | **1.40×** | **1.47×** | **1.48×** |
|  | x64 | 1.02× | 1.05× | 1.08× | 1.06× | 1.07× | 1.06× | 1.07× | 1.08× |
| JSON records | arm64 | **1.49×** | **1.50×** | **1.51×** | **1.50×** | **1.51×** | **1.50×** | **1.48×** | **1.50×** |
|  | x64 | **1.19×** | **1.12×** | **1.12×** | **1.12×** | **1.12×** | **1.11×** | **1.12×** | **1.13×** |
| JSON records + dictionary | arm64 | **1.44×** | **1.46×** | **1.49×** | **1.42×** | **1.42×** | **1.41×** | **1.53×** | **1.52×** |
|  | x64 | **1.13×** | 1.04× | **1.16×** | **1.13×** | **1.12×** | **1.12×** | **1.16×** | **1.16×** |

| Compression | | 1 | 3 | 5 | 7 | 9 | 13 | 16 | 19 |
|:--|:--|--:|--:|--:|--:|--:|--:|--:|--:|
| silesia.tar, one frame | arm64 | 1.00× | 1.01× | 0.96× | 0.96× | 0.98× | 0.97× | 1.01× | 1.08× |
|  | x64 | 1.01× | 0.97× | 0.99× | 1.00× | 1.00× | 1.03× | 1.08× | 0.98× |
| silesia.tar, 64 KiB pages | arm64 | 1.01× | 1.01× | 0.96× | 0.96× | 0.95× | 1.04× | 1.00× | 0.98× |
|  | x64 | 1.02× | 0.96× | 0.96× | 0.98× | 0.99× | 1.01× | 1.09× | **1.10×** |
| JSON records | arm64 | **1.29×** | **1.19×** | **1.12×** | 1.09× | **1.17×** | **1.24×** | **1.16×** | **1.14×** |
|  | x64 | **1.28×** | **1.16×** | 1.06× | 1.08× | **1.18×** | 1.08× | **1.10×** | **1.10×** |
| JSON records + dictionary | arm64 | **1.31×** | **1.35×** | 0.97× | 0.93× | 0.93× | **1.39×** | **1.33×** | **1.31×** |
|  | x64 | **1.43×** | **1.46×** | 0.97× | 1.00× | 0.99× | **1.21×** | **1.14×** | **1.13×** |

Decompression is faster everywhere: 1.3 to 1.5× on arm64, 1.0 to 1.2× on x64. The native side is
stronger on x64. NativeCompressions builds its win-x64 libzstd with GCC, which compiles zstd's
hand-written assembly Huffman decoder and its BMI2 code paths. Both exist for x86-64 only, so the arm64
build has no counterpart. Compression of large inputs runs at the native speed, give or take 5 %, and
up to 10 % faster at levels 16 and 19 on x64: the compressor runs libzstd's algorithms step for step to
write its exact bytes, so it does the same work. Small records compress faster because the managed code
has a smaller fixed cost per frame.

### LZ4

| Decompression | | 1 | 2 | 3 | 6 | 9 | 12 |
|:--|:--|--:|--:|--:|--:|--:|--:|
| silesia.tar, one block | arm64 | 1.03× | 1.04× | 1.03× | 1.04× | 1.04× | 1.03× |
|  | x64 | 1.02× | 1.02× | 1.02× | 1.02× | 1.02× | 1.01× |
| silesia.tar, 64 KiB pages | arm64 | 1.04× | 1.03× | 1.02× | 1.02× | 1.02× | 1.02× |
|  | x64 | 1.00× | 0.98× | 0.99× | 0.99× | 0.99× | 0.99× |
| JSON records | arm64 | **1.10×** | **1.11×** | **1.10×** | **1.10×** | **1.10×** | 1.08× |
|  | x64 | 1.06× | 1.05× | 1.04× | 1.03× | 1.04× | 1.04× |

| Compression | | 1 | 2 | 3 | 6 | 9 | 12 |
|:--|:--|--:|--:|--:|--:|--:|--:|
| silesia.tar, one block | arm64 | 0.96× | 0.92× | 1.00× | 0.93× | 0.88× | 1.00× |
|  | x64 | 0.98× | 1.00× | 0.95× | 0.95× | 0.95× | 0.93× |
| silesia.tar, 64 KiB pages | arm64 | 0.95× | 0.92× | 0.95× | 0.92× | 0.87× | 0.86× |
|  | x64 | 0.94× | 1.01× | 0.96× | 0.95× | 0.92× | 0.93× |
| JSON records | arm64 | **1.24×** | **5.66×** | **3.87×** | **3.88×** | **3.38×** | **1.51×** |
|  | x64 | **1.16×** | **3.40×** | **2.20×** | **2.18×** | **1.72×** | 1.06× |

lz4 clears its whole state before every block: 16 KiB for levels 1 and below, 256 KiB for levels 2 to 12.
The managed compressor keeps its state from one block to the next, and numbers each block's positions
past the last block's, so that whatever earlier blocks left reads exactly as a cleared entry. The bytes
are unchanged, and small blocks skip that cost. The gain is smaller on x64, where native lz4 compresses
these records up to 1.7× as fast as on arm64, and the managed code at the same speed.
On large blocks the high-compression levels are slower, by up to 14 % on arm64 and 8 % on x64: their
search through the hash chains is bound by memory latency.

### Compression ratios

The managed compressors write the native bytes, so the ratios below hold for both sides, on arm64 and
x64 alike.

| Zstandard level | 1 | 3 | 5 | 7 | 9 | 13 | 16 | 19 |
|:--|--:|--:|--:|--:|--:|--:|--:|--:|
| silesia.tar | 2.90 | 3.20 | 3.38 | 3.52 | 3.59 | 3.67 | 3.83 | 4.01 |
| JSON records + dictionary | 9.89 | 9.92 | 10.53 | 10.52 | 10.34 | 10.22 | 10.76 | 10.76 |

| LZ4 level | 1 | 2 | 3 | 6 | 9 | 12 |
|:--|--:|--:|--:|--:|--:|--:|
| silesia.tar | 2.10 | 2.38 | 2.61 | 2.70 | 2.72 | 2.74 |

## What it supports

| | Zstandard (`ManagedCompressions.Zstd`) | LZ4 (`ManagedCompressions.Lz4`) |
|:--|:--|:--|
| Format | RFC 8878 frames, skippable frames | the LZ4 block format (Parquet's `LZ4_RAW`) |
| Compression levels | -131072 to 22, libzstd's bytes at each | acceleration 65537 to level 12, lz4's bytes at each |
| Dictionaries | yes, at compression and decompression | no |
| Parameters | the level, the window, the long-distance matcher, the header's flags (`ZstdCompressionOptions`) | the level |
| Checksums | XXH64, written on request, always verified | not in the block format |
| Parallel compression | `CompressAsync`, libzstd's multithreaded frames | no |
| Streaming | decompression (`ZstdDecoder`), any size, its memory bounded by the window; compression (`ZstdEncoder`), libzstd's streamed frames; `ZstdStream`, both ways | no |
| Validation | accepts and refuses what libzstd does | refuses any literal or match past either end |
| Processors | little-endian: x64 and arm64; 32-bit x86, checked by hand; big-endian refused | the same |

Not supported: LZ4 streaming (a block is compressed in one call, from and into memory), one-shot
inputs over 2 GiB (`ZstdEncoder` streams them), the LZ4 frame format, libzstd's finer parameters
(strategy, hash and chain logs, target block size), and big-endian processors (IBM Z), where the
constructors throw `PlatformNotSupportedException`. WebAssembly has not been tried.

## How it was measured

- **Machines:** an Apple M4 Pro (arm64) under macOS 26.7, on .NET 11.0.0-rc.1, and an AMD Ryzen 9 7950X
  (x64, Zen 4) under Windows 11 (build 26300), on .NET 11.0.0, both on one thread. On Windows the
  benchmarks run at a high priority, and the level-by-level one keeps its timed thread on one logical
  processor (`--core 2`), as macOS keeps it on a performance core.
- **.NET 10:** the figures are .NET 11's, and .NET 10 runs the managed codecs at nearly the same speed.
  On the Ryzen 9 7950X, at levels 1, 3 and 9 of every level-by-level workload, its speed against the
  native side is 0.99 of .NET 11's on average, and one case is more than 5 % slower, by 10 %
  ([`bench/results/x64/dotnet10`](https://github.com/Evariops/ManagedCompressions/blob/main/bench/results/x64/dotnet10/README.md)).
  The arm64 machine has yet to measure it.
- **Native side:** NativeCompressions.Zstandard 1.3.0 and NativeCompressions.LZ4 1.2.0, which ship zstd
  1.5.7 and lz4 1.10.0 built at `-O3`: the same libraries as NativeCompressions 1.1.0, which the arm64
  figures were measured with. On
  arm64, its libzstd decompresses at the speed of the one .NET ships. A libzstd built from source with
  Apple clang 21 decompresses some 20 % faster: against that one, Zstandard decompression is 1.11–1.21×
  as fast, as [`src/ManagedCompressions.Zstd/README.md`](https://github.com/Evariops/ManagedCompressions/blob/main/src/ManagedCompressions.Zstd/README.md#performance)
  shows. On x64, NativeCompressions' libraries are built with GCC 16.2 (MSYS2). That libzstd has zstd's
  assembly Huffman decoder and its BMI2 code, which only exist for x86-64.
- **Level-by-level tables:** `bench/ManagedCompressions.Bench` times the native and the managed side in
  alternating rounds and reports each one's median, after a warm-up that gives the JIT its final, profiled
  code. Its first round checks that the managed output matches the native output byte for byte, and that
  everything decodes back. The raw results are in
  [`bench/results/arm64/levels.md`](https://github.com/Evariops/ManagedCompressions/blob/main/bench/results/arm64/levels.md) and
  [`bench/results/x64/levels.md`](https://github.com/Evariops/ManagedCompressions/blob/main/bench/results/x64/levels.md).
- **At-a-glance tables:** `bench/ManagedCompressions.Benchmarks` runs BenchmarkDotNet the way
  NativeCompressions' README does, under the JIT and under Native AOT. Each case first runs the managed
  codec on the first MiB of silesia.tar, so that the JIT has given it its final, profiled code, as a
  running service would have it: a single call of 211 MB would otherwise be measured in an earlier tier.
  Native AOT compiles for the machine's instruction set: `native` on arm64, `x86-64-v4` on x64, where
  ILC with `native`, from 11.0.0-rc.1 to 11.0.0, compiles a branch on `AvxVnni.IsSupported` into an
  infinite loop and BenchmarkDotNet's CPU detection hangs ([dotnet/runtime#135603](https://github.com/dotnet/runtime/issues/135603)).

To reproduce, start from the data and the native test tools:

```sh
bench/zstd-corpus.sh                 # silesia.zip and zstd's JSON records, downloaded and checked
tools/native-ref/testdata.sh         # zstd's golden files, for the tests
tools/native-ref/build.sh mt         # libzstd with threads, the oracle of the parallel compression tests
```

Then run the benchmarks:

```sh
dotnet run -c Release -f net11.0 --project bench/ManagedCompressions.Bench -- --codecs zstd,lz4 --seconds 3
dotnet run -c Release --project bench/ManagedCompressions.Benchmarks -- --filter '*'
```

On Windows, add `--core 2` to the first command to keep its timed thread on one logical processor. With
`-f net10.0`, it measures the managed codecs on .NET 10.

Finally, run the tests:

```sh
dotnet test -c Release --solution ManagedCompressions.slnx
```

The tests check the compressors against the native libraries byte for byte, at every level. They also
check the decoders against every frame and block the native libraries write, and against zstd's golden
files and decodecorpus frames. Fuzzing runs with each buffer placed against an inaccessible memory page,
so that a single byte read or written out of bounds faults.

CI builds everything and runs the tests on every pull request, the benchmarks excepted: their figures
mean something on a quiet machine only. It also runs the repository's [prek](https://github.com/j178/prek)
hooks, betterleaks for secrets and zizmor for the workflows; `prek install` runs them before each commit.
Each merged pull request updates a draft GitHub release; publishing it runs the same checks on its tag,
then pushes the packages to nuget.org.

## Repository

| Path | Contents |
|:--|:--|
| `src/ManagedCompressions.Zstd` | Zstandard: decompressor, compressor, dictionaries, parallel compression |
| `src/ManagedCompressions.Lz4` | LZ4 block format: `Lz4Block` (decoder, default compressor), `Lz4Compressor` (every level) |
| `tests/` | differential tests against the native libraries, golden files, fuzzing |
| `bench/ManagedCompressions.Bench` | level-by-level comparison with NativeCompressions |
| `bench/ManagedCompressions.Benchmarks` | BenchmarkDotNet on silesia.tar, JIT and Native AOT (`--check` verifies every case once) |
| `bench/ManagedCompressions.Zstd.Perf` | Zstandard against libzstd built from source, and micro-benchmarks |
| `bench/jit-profile.sh`, `bench/*-pcprofile.py` | instruction-level profilers, for the JIT and for Native AOT |
| `bench/results/arm64`, `bench/results/x64` | the results above, for each machine |
| `tools/native-ref` | libzstd 1.5.7 built from source, and the generator of the test data |

## License

ManagedCompressions is licensed under the
[MIT License](https://github.com/Evariops/ManagedCompressions/blob/main/LICENSE). The compressors are
derived from libzstd (BSD-3-Clause) and lz4 (BSD-2-Clause), and those parts remain under their licenses:
see [NOTICE](https://github.com/Evariops/ManagedCompressions/blob/main/NOTICE), which each package
carries. This project is not affiliated with the zstd or lz4 projects, nor with Cysharp.

What the decoders guarantee on untrusted input, and how to report a vulnerability, is in
[SECURITY.md](https://github.com/Evariops/ManagedCompressions/blob/main/SECURITY.md).
