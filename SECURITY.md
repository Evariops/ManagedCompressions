# Security

## What is guaranteed

A corrupt or hostile input produces an error, never anything worse. The decoders read untrusted data:

- `ZstdDecompressor.Decompress` returns `OperationStatus.InvalidData` for a frame libzstd refuses, and
  for a block over RFC 8878's size limit, which libzstd only partly enforces.
- `ZstdDecoder.Decompress` returns `OperationStatus.InvalidData` for a stream libzstd's streaming
  decoder refuses, a frame whose window exceeds the decoder's `MaxWindowLog` among them.
- `BrotliDecompressor.Decompress` returns `OperationStatus.InvalidData` for a stream brotli 1.1.1
  refuses, and `OperationStatus.DestinationTooSmall` for one whose meta-blocks declare more content
  than the destination holds, before decoding them.
- `Lz4Block.TryDecompress` returns false, and `Lz4Block.Decompress` throws `InvalidDataException`, for a
  literal or a match past either end, an offset of zero or before the start, and a block that does not
  end with literals.

None reads nor writes outside the spans it is given, and none allocates what the input asks for:
a decompressor's memory is bounded by the format's block size and by the dictionary the caller passes,
and its output by the destination the caller passes. A `ZstdDecoder` also holds the window of the frame
it decodes, which `ZstdDecompressionOptions.MaxWindowLog` bounds: 128 MiB and a block by default. A
`BrotliDecompressor` holds its prefix codes, at most 256 of each kind and a few megabytes, whatever the
stream's size: the destination is its window. The tests fuzz the decoders with each buffer
placed against an inaccessible memory page, so that a single byte read or written out of bounds faults.

## What is not

- **A frame's declared content size is a claim.** `ZstdDecompressor.TryGetFrameContentSize` reads it
  from the header, unchecked: allocate from it only up to a limit you choose.
- **Integrity without a checksum.** A Zstandard frame's checksum is verified when the frame has one,
  but a corruption that leaves a frame without one valid decodes to wrong content, and neither the LZ4
  block format nor Brotli has a checksum at all: a Brotli stream's corruption that stays valid decodes
  to wrong content too. No format authenticates anything: protection against tampering
  needs a MAC or a signature around the compressed data.
- **Confidentiality.** Compressing a secret together with data an attacker controls can reveal the
  secret through the compressed size (CRIME, BREACH). Nothing here runs in constant time.
- **Bounded compression costs.** A compressor's memory and time grow with its level and its input;
  a service that lets an untrusted party choose either has to bound them.

## Reporting

Report a vulnerability through the repository's Security tab, with "Report a vulnerability". That
opens a private advisory visible only to you and the maintainers. A public issue is the wrong place,
and cannot be taken back.

Include the frame or block that reproduces it, and the call that reads or writes it. A crash, a hang
or an out-of-bounds access on a corrupt input is a bug within the scope of the guarantee above, and is
treated as one.
