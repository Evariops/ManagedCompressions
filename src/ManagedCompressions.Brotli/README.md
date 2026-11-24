# ManagedCompressions.Brotli

Brotli ([RFC 7932](https://www.rfc-editor.org/rfc/rfc7932)) compression and decompression for .NET, in
fully managed C#.

- It contains no native code: no bundled library and no P/Invoke, so it runs wherever .NET runs on a
  little-endian processor, and it is compatible with trimming and Native AOT.
- Its compressor writes the bytes brotli 1.1.1 writes, the version .NET 10 and .NET 11 ship, those of
  `BrotliEncoder.TryCompress`: at every quality from 0 to 11, every window and every mode, so its ratios
  are brotli's. At qualities 10 and 11 they are those of the platform's brotli: .NET's brotli computes a
  logarithm otherwise on Windows than on Linux and macOS, which changes a few streams, and so does this one.
- Its decompressor accepts and refuses the streams brotli 1.1.1 does, and reports why it refuses one
  with brotli's own error codes.
- Both are at least as fast as the brotli .NET ships at every quality (see [Performance](#performance)):
  the compressor 1.00 to 1.29× on a sample of the Silesia corpus and 1.03 to 21× on JSON records of 800
  bytes; the decompressor 1.07 to 1.37× on the Silesia corpus and 1.06 to 1.10× on the JSON records.
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

### Streams

`BrotliCompressionStream` is a managed `BrotliStream`: it compresses what is written to it, or
decompresses what it reads, synchronously or asynchronously, with no size limit.

```csharp
using System.IO.Compression;
using ManagedCompressions.Brotli;

using (var output = new BrotliCompressionStream(file, CompressionLevel.Optimal))   // or a quality, a window, a mode
{
    input.CopyTo(output);   // the bytes BrotliStream writes for the same writes and flushes
}

using var reader = new BrotliCompressionStream(compressedFile, CompressionMode.Decompress);
reader.CopyTo(destination);   // InvalidDataException for an invalid or truncated stream
```

Underneath, `BrotliStreamEncoder` and `BrotliStreamDecoder` work piece by piece, as `System.IO.Compression`'s
`BrotliEncoder` and `BrotliDecoder` do:

```csharp
var encoder = new BrotliStreamEncoder(quality: 4);
OperationStatus status = encoder.Compress(piece, destination, out int consumed, out int written, isFinalBlock: false);
status = encoder.Flush(destination, out written);   // everything so far, decodable, ending on a byte boundary

var decoder = new BrotliStreamDecoder();
status = decoder.Decompress(nextBytes, destination, out consumed, out written);   // NeedMoreData: give it more
```

`BrotliStreamEncoder` takes the calls .NET's `BrotliEncoder` takes and answers as it does: the same status,
the same bytes consumed and written, the same stream. `BrotliStreamDecoder` keeps the stream's window, the
last 1 KiB to 16 MiB of content, and a meta-block, and consumes nothing past the stream's end; its buffers
come from `ArrayPool<byte>.Shared` and go back when it is disposed.

## What it supports

| | |
|:--|:--|
| Format | RFC 7932 streams, windows of 1 KiB to 16 MiB, metadata blocks |
| Compression | qualities 0 to 11, windows 10 to 24, the generic, text and font modes: brotli 1.1.1's bytes at each |
| Large window | not written, and refused, as brotli refuses it by default (`BROTLI_DECODER_PARAM_LARGE_WINDOW`) |
| Shared dictionaries | no (RFC 9841) |
| Streaming | `BrotliCompressionStream`, both ways; `BrotliStreamEncoder`, brotli's streamed bytes, flushes included; `BrotliStreamDecoder`, any size, its memory bounded by the window and a meta-block |
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
- **Several codes per write.** At qualities 0 to 3, a literal's code takes 14 or 15 bits at most, so three
  or four literals go out in one write, and a command's or a distance's code goes out with its extra bits.
  Each code is one word, its bits and its length, read with one load.
- **Tables fetched ahead.** From quality 4, the hash tables outgrow the caches (4 MiB at quality 4 on a
  large input, 32 MiB at quality 9) and the search waits on them. The lines the next search reads first
  are fetched while the current one runs: the next position's, which is nearly always the next searched,
  and, after a copy, the position past it, before the copy's positions are stored.
- **Small inputs.** On an 800-byte input, quality 0 spends most of its time building the literal code. The
  bytes are counted in four histograms, so that a repeated byte does not wait for the increment before,
  the code length counts likewise, and the loops over the 256 symbols run on vectors or without branches.
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
| 0 | 2.25 | 550 | 550 | 1.00× | 312 | 341 | **1.09×** |
| 1 | 2.44 | 411 | 460 | **1.12×** | 189 | 204 | **1.06×** |
| 2 | 2.60 | 216 | 220 | 1.02× | 211 | 250 | **1.19×** |
| 3 | 2.62 | 186 | 186 | 1.00× | 91.3 | 112 | **1.23×** |
| 4 | 2.66 | 124 | 134 | **1.08×** | 59.7 | 69.6 | **1.16×** |
| 5 | 2.88 | 50.4 | 65.6 | **1.29×** | 5.30 | 63.7 | **12.4×** |
| 6 | 2.91 | 36.4 | 43.8 | **1.20×** | 4.11 | 62.1 | **15.6×** |
| 7 | 2.93 | 24.8 | 28.8 | **1.16×** | 3.08 | 54.5 | **17.4×** |
| 8 | 2.94 | 19.1 | 22.4 | **1.18×** | 2.72 | 52.4 | **20.1×** |
| 9 | 2.95 | 14.6 | 17.2 | **1.18×** | 2.38 | 47.6 | **21.0×** |
| 10 | 3.21 | 2.18 | 2.56 | **1.18×** | 1.81 | 2.05 | **1.13×** |
| 11 | 3.31 | 1.05 | 1.20 | **1.15×** | 1.07 | 1.09 | 1.03× |

The ratio is the Silesia sample's. Qualities 4 to 9 move by up to 20 % from one process to the next, on
both sides: brotli took 124 MB/s at quality 4 here, and 77.8 MB/s in an earlier process. On small inputs,
brotli spends qualities 5 to 9 allocating and clearing tables a `BrotliCompressor` keeps.
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

### Streams

`BrotliCompressionStream` against .NET's `BrotliStream` on the Silesia sample, 2 MiB of it at quality 11:
written in pieces of 64 KiB, the same bytes out, and read back 80 KiB at a time from a stream brotli
wrote. The two alternate in one process; each speed is the best of several seconds of runs, the same in
two processes.

| Quality | Writing, native MB/s | managed MB/s | × | Reading, native MB/s | managed MB/s | × |
|--:|--:|--:|--:|--:|--:|--:|
| 1 | 453 | 486 | **1.07×** | 561 | 740 | **1.32×** |
| 4 | 114 | 119 | 1.05× | 666 | 887 | **1.33×** |
| 5 | 60.4 | 65.7 | **1.09×** | 703 | 858 | **1.22×** |
| 9 | 17.9 | 18.6 | 1.04× | 720 | 890 | **1.24×** |
| 11 | 1.10 | 1.30 | **1.18×** | 583 | 625 | 1.07× |

A stream written piece by piece does not announce its size, so brotli picks the hashers of an input of
unknown size, the same on both sides: H4 at quality 4 and H5 at 5 to 9, where one call over a large input
takes H54 and H6.

## Tests

The tests check the compressor and the decoder against brotli 1.1.1. The compressor must write brotli's
stream byte for byte at every quality, at windows of 10 to 24 bits and in every mode, on ten kinds of data
of 1 byte to 1 MiB, refuse the destinations brotli refuses, and its streams must decode back. The decoder
must decode every stream brotli writes, brotli's own test streams, written by other encoders too, and give
tens of thousands of corrupted, truncated and random streams brotli's verdict, content and error code. The
fuzzing runs both comparisons with each buffer against an inaccessible memory page.
