# ManagedCompressions.Brotli

Brotli ([RFC 7932](https://www.rfc-editor.org/rfc/rfc7932)) compression and decompression for .NET, in
fully managed C#.

- It contains no native code: no bundled library and no P/Invoke, so it runs wherever .NET runs on a
  little-endian processor, and it is compatible with trimming and Native AOT.
- Its compressor writes the bytes brotli 1.1.1 writes, the version .NET 10 and .NET 11 ship, those of
  `BrotliEncoder.TryCompress`: at every quality from 0 to 11, every window and every mode, so its ratios
  are brotli's.
- Its decompressor accepts and refuses the streams brotli 1.1.1 does, and reports why it refuses one
  with brotli's own error codes.
- Both are faster than the brotli .NET ships (see [Performance](#performance)): the compressor 1.00 to
  1.60× on a sample of the Silesia corpus at qualities 1 to 11, and up to 21× on small inputs, quality 0
  aside; the decompressor 1.07 to 1.37× on the Silesia corpus and 1.06 to 1.10× on JSON records of 800
  bytes.
- A warm compressor or decompressor allocates nothing.

ManagedCompressions.Brotli is an independent implementation of the format. It is not affiliated with the
brotli project or with Google.

## Requirements

ManagedCompressions.Brotli targets .NET 10 (`net10.0`) and .NET 11 (`net11.0`), and has no dependency.

```sh
dotnet add package ManagedCompressions.Brotli
```

From source, reference `src/ManagedCompressions.Brotli/ManagedCompressions.Brotli.csproj`.

## Usage

### Compression

```csharp
using System.Buffers;
using ManagedCompressions.Brotli;

var compressor = new BrotliCompressor(quality: 4);   // reuse it: it keeps its tables and buffers

byte[] stream = new byte[BrotliCompressor.GetMaxCompressedLength(content.Length)];
OperationStatus status = compressor.Compress(content, stream, out int consumed, out int written);
```

`Compress` compresses the whole source into one stream per call, the stream `BrotliEncoder.TryCompress`
writes with the same quality and window. It returns `Done`, or `DestinationTooSmall` without writing
anything when the stream does not fit, unless the destination holds `GetMaxCompressedLength` bytes: then,
as brotli does, it stores the source uncompressed. `TryCompress(source, destination, out written)` is the
same call as `BrotliEncoder.TryCompress`, false when the stream does not fit.

| Parameter | Range | Default |
|:--|:--|:--|
| `quality` | 0 (fastest) to 11 (densest) | 4, .NET's (`BrotliEncoder.TryCompress`, `CompressionLevel.Optimal`) |
| `windowLog` | 10 (1 KiB) to 24 (16 MiB) | 22, brotli's and .NET's |
| `mode` | `BrotliMode.Generic`, `Text` (UTF-8) or `Font` (WOFF 2.0) | `Generic` |

A compressor is not thread-safe: use one per thread.

### Decompression

```csharp
using System.Buffers;
using ManagedCompressions.Brotli;

var decompressor = new BrotliDecompressor();   // reuse it: it keeps its tables between streams

byte[] content = new byte[expectedSize];
OperationStatus status = decompressor.Decompress(stream, content, out int consumed, out int written);
```

`Decompress` decodes one whole stream per call, into a destination that must hold all of its content:
a Brotli stream does not record its size, so store it beside the stream, or grow the destination and
call again.

| status | meaning |
|---|---|
| `Done` | The stream was decoded. `consumed` is its size: what follows it is left alone. |
| `NeedMoreData` | The source ends inside the stream. |
| `DestinationTooSmall` | The content does not fit: a meta-block declares more than the room left. |
| `InvalidData` | The source is not a valid stream. |

After a status other than `Done`, nothing has been consumed nor written, so call again with the whole
stream or a larger destination. The bytes of the destination past the content may be overwritten:
the decoder copies 16 and 32 bytes at a time where there is room.

The status says what to do next; `decompressor.LastError` says why, naming the check that refused the
stream as brotli's decoder names it (`BrotliError.HuffmanSpace` for `BROTLI_DECODER_ERROR_FORMAT_HUFFMAN_SPACE`,
and so on). It is `BrotliError.None` after a call that returns `Done`.

`TryDecompress(source, destination, out written)` is the same call as `BrotliDecoder.TryDecompress`,
true when the stream decodes.

The names do not clash with `System.IO.Compression`'s `BrotliEncoder`, `BrotliDecoder` and
`BrotliStream`: both namespaces can be imported at once.

## What it supports

| | |
|:--|:--|
| Format | RFC 7932 streams, windows of 1 KiB to 16 MiB, metadata blocks |
| Compression | qualities 0 to 11, windows 10 to 24, the generic, text and font modes: brotli 1.1.1's bytes at each |
| Large window | not written, and refused, as brotli refuses it by default (`BROTLI_DECODER_PARAM_LARGE_WINDOW`) |
| Shared dictionaries | no (RFC 9841) |
| Streaming | no: a stream is compressed or decoded in one call, from and into memory |
| Validation | accepts and refuses what brotli 1.1.1 does, with its error codes |
| Processors | little-endian: x64 and arm64; big-endian refused |

Two pairs of brotli's error codes, which depend on the state of brotli's ring buffer when it finds the
error, are one value each here: `BrotliError.BlockLength` for `FORMAT_BLOCK_LENGTH_1` and `_2`, and
`BrotliError.Padding` for `FORMAT_PADDING_1` and `_2`.

## How it is fast

### Compression

The compressor computes what brotli computes, in the same order and the same types, floats where brotli
has floats: its bytes are brotli's. The speed comes from how the work is laid out:

- **State kept between streams.** `BrotliEncoder.TryCompress` creates brotli's state, allocates its hash
  tables and ring buffer and frees them at every call; a `BrotliCompressor` keeps them, and clears only
  what a stream uses. On small inputs at qualities 5 to 9, whose tables are large, that is most of
  brotli's time.
- **Bits through an accumulator.** brotli writes each code by loading the byte at the bit position, OR-ing
  the code in and storing 8 bytes. The hot loops here keep the pending bits in a 64-bit register and store
  it once per code, without a branch: the same bytes, with no load on the way.
- **The block splitter and the clustering a vector at a time.** At qualities 10 and 11, the splitter
  weighs every symbol against up to 100 entropy codes, and the clustering weighs pairs of histograms;
  both run on vectors of doubles and counts, with the same additions in the same order, and the same
  first minimum brotli's strict comparisons keep.
- **A search compiled for each hasher.** The hashers are structs and the backward reference search a
  generic method, so that each hasher's bucket sizes, hash lengths and sweeps are constants in code of
  its own, as brotli's templates make them.

### Decompression

brotli decodes every stream into a ring buffer, then copies to the destination, and its decoder is a
state machine that can stop at any bit, since it streams. A one-shot decode has the whole stream and the
whole destination, and ManagedCompressions.Brotli takes advantage of both:

- **Straight into the destination.** The window is the content written so far, so there is no ring
  buffer to wrap, no mask on every copy, and no copy of the output.
- **A loop that checks once per run, not once per symbol.** While enough input remains, literals are
  decoded in runs as long as the next block switch, the command's end and the input left allow, three
  per refill of a 64-bit bit reader; near the end of the input, the same loop compiled with checks at
  every step takes over.
- **Wider tables for literals.** A literal's code is looked up in a root table of 10 bits where brotli's
  is 8, so that codes of 9 and 10 bits need one lookup instead of two; meta-blocks with little content
  for their codes keep 8 bits, which are cheaper to build.
- **One load less per literal.** With context modeling, brotli goes from the context to the context map,
  then to the code; here a table of the 64 contexts' codes, rebuilt at each block switch, gives the
  code at once.
- **Tables built once per entry.** A code's root table is filled by increasing code length, each entry
  written once and the table doubled by copies, where brotli replicates each code with a stride.
- **Dictionary words inline.** A reference to the static dictionary, frequent at qualities 10 and 11, is
  copied in the command loop with wide stores and a table of the 121 transforms unpacked.

## Performance

AMD Ryzen 9 7950X (x64), Windows 11, .NET 11.0.0, one thread. Native is the brotli 1.1.1 .NET ships.
Each × is the managed throughput divided by the native one, paired round by round: above 1.00 is faster.

### Compression

Native is `BrotliEncoder.TryCompress` at window 22, managed a reused `BrotliCompressor`; both write the
same bytes. The Silesia sample is 1 MiB every 16 MiB of silesia.tar, 13.6 MB, in one stream.

| Quality | Ratio | Silesia sample, native MB/s | managed MB/s | × | 800-byte JSON records, native MB/s | managed MB/s | × |
|--:|--:|--:|--:|--:|--:|--:|--:|
| 0 | 2.25 | 565 | 535 | 0.95× | 313 | 282 | 0.90× |
| 1 | 2.44 | 408 | 432 | **1.06×** | 190 | 206 | **1.07×** |
| 2 | 2.60 | 212 | 217 | 1.03× | 208 | 238 | **1.14×** |
| 3 | 2.62 | 182 | 183 | 1.00× | 91.8 | 104 | **1.13×** |
| 4 | 2.66 | 77.8 | 124 | **1.60×** | 59.9 | 65.9 | **1.10×** |
| 5 | 2.88 | 48.6 | 56.5 | **1.16×** | 5.29 | 60.8 | **11.9×** |
| 6 | 2.91 | 31.2 | 39.9 | **1.28×** | 3.99 | 61.5 | **15.1×** |
| 7 | 2.93 | 23.2 | 23.6 | 1.01× | 3.04 | 54.0 | **17.9×** |
| 8 | 2.94 | 15.2 | 17.7 | **1.16×** | 2.76 | 52.6 | **19.7×** |
| 9 | 2.95 | 12.4 | 13.1 | 1.05× | 2.26 | 47.3 | **21.0×** |
| 10 | 3.21 | 2.12 | 2.54 | **1.20×** | 1.82 | 2.04 | **1.13×** |
| 11 | 3.31 | 1.03 | 1.17 | **1.14×** | 1.07 | 1.10 | 1.03× |

The ratio is the Silesia sample's. Qualities 4 to 9 move by up to 20 % from one process to the next, on
both sides. On small inputs, brotli spends qualities 5 to 9 allocating and clearing tables a
`BrotliCompressor` keeps. Quality 0 is the one quality still behind.
[`bench/results/x64/brotli-compression.md`](https://github.com/Evariops/ManagedCompressions/blob/main/bench/results/x64/brotli-compression.md)
has the intervals.

### Decompression

Native is `BrotliDecoder.TryDecompress`, managed `BrotliDecompressor.Decompress`.

| | Quality | Native, MB/s | Managed, MB/s | × |
|:--|--:|--:|--:|--:|
| **silesia.tar**, one stream | 1 | 593 | 793 | **1.34×** |
|  | 4 | 726 | 993 | **1.37×** |
|  | 5 | 723 | 873 | **1.21×** |
|  | 9 | 774 | 969 | **1.25×** |
|  | 11 | 655 | 701 | 1.07× |
| **silesia.tar**, 64 KiB pages | 1 | 548 | 732 | **1.34×** |
|  | 4 | 587 | 746 | **1.27×** |
|  | 5 | 587 | 732 | **1.25×** |
|  | 9 | 583 | 722 | **1.24×** |
|  | 11 | 464 | 502 | 1.08× |
| **500 JSON records** of 800 bytes | 1 | 394 | 435 | **1.10×** |
|  | 4 | 431 | 474 | 1.10× |
|  | 5 | 432 | 474 | 1.10× |
|  | 9 | 420 | 453 | 1.08× |
|  | 11 | 314 | 337 | 1.06× |

Quality 4 is .NET's default (`BrotliEncoder.TryCompress`, `CompressionLevel.Optimal`), 11 the highest.
`bench/ManagedCompressions.Bench --codecs brotli` measures it, against the brotli .NET ships.

## Tests

The tests check the compressor and the decoder against brotli 1.1.1. The compressor must write brotli's
stream byte for byte at every quality, at windows of 10 to 24 bits and in every mode, on ten kinds of data
of 1 byte to 1 MiB, refuse the destinations brotli refuses, and its streams must decode back. The decoder
must decode every stream brotli writes, brotli's own test streams, written by other encoders too, and give
tens of thousands of corrupted, truncated and random streams brotli's verdict, content and error code. The
fuzzing runs both comparisons with each buffer against an inaccessible memory page.
