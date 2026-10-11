# ManagedCompressions

Zstandard and LZ4 in fully managed C#, measured against the native libraries that
[NativeCompressions](https://github.com/Cysharp/NativeCompressions) ships (zstd 1.5.7 and lz4 1.10.0).

- `src/ManagedCompressions.Zstd` is a Zstandard decompressor and a compressor that writes exactly
  the frames libzstd 1.5.7 writes, at every level.
- `src/ManagedCompressions.Lz4` is an LZ4 block decoder and a port of lz4 1.10.0's compressors, every
  level from acceleration 65536 to 12, writing exactly the blocks lz4 writes.

## Layout

| path | contents |
|---|---|
| `src/ManagedCompressions.Zstd` | Zstandard (RFC 8878) |
| `src/ManagedCompressions.Lz4` | the LZ4 block format: `Lz4Block` (decoder, default compressor) and `Lz4Compressor` (every level) |
| `tests/ManagedCompressions.Zstd.Tests` | differential tests against libzstd, golden files, decodecorpus, fuzzing |
| `tests/ManagedCompressions.Lz4.Tests` | lz4 itself as the oracle at every level, byte for byte; fuzzing against guard pages |
| `bench/ManagedCompressions.Bench` | the managed codecs against NativeCompressions, level by level, one thread |
| `bench/ManagedCompressions.Zstd.Perf` | Native AOT micro-benchmarks and PC sampler for zstd |
| `bench/jit-profile.sh` | samples a benchmark run at the instruction, the JIT's code resolved through its perf map |
| `tools/native-ref` | zstd 1.5.7 built from source: the tests' data and the multithreaded oracle |

## Building and testing

```sh
tools/native-ref/testdata.sh      # zstd's golden files and decodecorpus frames
tools/native-ref/build.sh mt      # libzstd with threads, for the parallel compression tests
bench/zstd-corpus.sh              # silesia.zip, github.tar.zst, github.dict.zst
dotnet test -c Release --solution ManagedCompressions.slnx
```

## Benchmarks

```sh
dotnet run -c Release --project bench/ManagedCompressions.Bench -- \
  [--codecs zstd,lz4] [--sets silesia,silesia-64k,github,github-dict] [--levels 1,3] \
  [--seconds 3] [--directions compression,decompression] [--only managed] \
  [--results file.tsv] [--markdown file.md]
```

Each level is measured in rounds that time the native and the managed side in turn, after a warm-up
that gives the JIT its final code; the figure is the median round. The first round checks that every
compressed record decodes back with the native decoder, and that the managed compressor writes the
native side's bytes exactly. Decompression decodes the native side's output.

Data sets: `silesia` is silesia.tar (211,948,032 bytes) as one frame or block; `silesia-64k` the same
cut in 3,235 pages of 64 KiB, each its own frame or block; `github` the 500 JSON records of zstd's
regression tests, a frame or block each; `github-dict` the same with their dictionary (zstd only).

Results are in `bench/results/`: `baseline-jit.md` is the code before the LZ4 work, `current-jit.md` the
code of this repository.
