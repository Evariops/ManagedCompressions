# ManagedCompressions.Brotli

Brotli ([RFC 7932](https://www.rfc-editor.org/rfc/rfc7932)) decompression for .NET, in fully managed C#.

- It contains no native code: no bundled library and no P/Invoke, so it runs wherever .NET runs on a
  little-endian processor, and it is compatible with trimming and Native AOT.
- It accepts and refuses the streams brotli 1.1.1 does, the version .NET 10 and .NET 11 ship, and
  reports why it refuses one with brotli's own error codes.
- It is faster than the brotli .NET ships (`BrotliDecoder`, `BrotliStream`): 1.07 to 1.37× on the Silesia
  corpus, and 1.06 to 1.10× on JSON records of 800 bytes (see [Performance](#performance)).
- A warm decompressor allocates nothing.

ManagedCompressions.Brotli is an independent implementation of the format. It is not affiliated with the
brotli project or with Google.

## Requirements

ManagedCompressions.Brotli targets .NET 10 (`net10.0`) and .NET 11 (`net11.0`), and has no dependency.

```sh
dotnet add package ManagedCompressions.Brotli
```

From source, reference `src/ManagedCompressions.Brotli/ManagedCompressions.Brotli.csproj`.

## Usage

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
| Large window | refused, as brotli refuses it by default (`BROTLI_DECODER_PARAM_LARGE_WINDOW`) |
| Shared dictionaries | no (RFC 9841) |
| Validation | accepts and refuses what brotli 1.1.1 does, with its error codes |
| Processors | little-endian: x64 and arm64; big-endian refused |

Two pairs of brotli's error codes, which depend on the state of brotli's ring buffer when it finds the
error, are one value each here: `BrotliError.BlockLength` for `FORMAT_BLOCK_LENGTH_1` and `_2`, and
`BrotliError.Padding` for `FORMAT_PADDING_1` and `_2`.

## How it is fast

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

AMD Ryzen 9 7950X (x64), Windows 11, .NET 11.0.0, one thread. Native is the brotli 1.1.1 .NET ships,
through `BrotliDecoder.TryDecompress`; managed is `BrotliDecompressor.Decompress`. Each cell is the
managed throughput divided by the native one, paired round by round: above 1.00 is faster.

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

The tests check the decoder against brotli 1.1.1: every stream brotli writes at every quality, window
and mode; brotli's own test streams, written by other encoders too; and tens of thousands of corrupted,
truncated and random streams, which must get brotli's verdict, content and error code. The fuzzing runs
the same comparison with each buffer against an inaccessible memory page.
