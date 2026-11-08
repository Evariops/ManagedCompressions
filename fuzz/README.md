# Fuzzing

Coverage-guided fuzzing of both libraries, with [libFuzzer](https://llvm.org/docs/LibFuzzer.html) through
[SharpFuzz](https://github.com/Metalnem/sharpfuzz). libFuzzer generates the inputs. SharpFuzz instruments
the published libraries, so that each input reports the branches it took. A target checks what the
libraries do with the input against the native libraries, zstd 1.5.7 and lz4 1.10.0, as NativeCompressions
ships them.

The sources and destinations lie against inaccessible pages on Linux and macOS, so a single byte read or
written past either end faults. A target fails an input by throwing, and libFuzzer keeps the input. A
fault or a hang (`-timeout`) fails it the same way.

| Target | Input | Guarantee |
|:--|:--|:--|
| `zstd-decompress` | `[flags][frame]` | a status, nothing consumed or written unless decoded; libzstd one-shot's verdict and content, with its two documented leniencies; `DecompressPrefix` gives the start of libzstd's content |
| `zstd-compress` | `[level][flags][content]` | libzstd's frame byte for byte, levels -7 to 22, with or without a checksum or a dictionary; it decodes back |
| `lz4-decompress` | `[capacity, 3 bytes][block]` | lz4's content for every block lz4 decodes, but those with a zero offset; `Decompress` agrees with `TryDecompress` |
| `lz4-compress` | `[level][content]` | lz4's block byte for byte, levels -7 to 12, the compressors reused from input to input; it decodes back |

The flags and the exact rules are in [`Targets.cs`](ManagedCompressions.Fuzz/Targets.cs). The LZ4 decoder
departs from lz4 on three kinds of blocks, all by design. It decodes blocks that break the format's
end-of-block rules, which lz4 refuses. It refuses a zero offset, which lz4 accepts. And into an empty
destination, it takes only the block that is a single zero token, as lz4 does.

## In CI

`.github/workflows/fuzz.yml` runs each target for two minutes on every pull request, and for 50 minutes
each night. The corpus is kept from run to run in the Actions cache, so pull requests start from what the
nightly runs found. Before fuzzing, every input of the corpus and of `regressions/<target>/` runs once. A
failing input is uploaded as the `findings-<target>` artifact.

## Locally

On Linux (or WSL), with the .NET SDK and clang:

```sh
curl -fsSLO https://raw.githubusercontent.com/Metalnem/libfuzzer-dotnet/bd39d4e88d715ab460a929943645be2a186cde52/libfuzzer-dotnet.cc
clang -fsanitize=fuzzer libfuzzer-dotnet.cc -o libfuzzer-dotnet
dotnet publish fuzz/ManagedCompressions.Fuzz -c Release -f net11.0 -r linux-x64 --self-contained false -o out
out/ManagedCompressions.Fuzz seed zstd-decompress corpus/zstd-decompress
dotnet tool restore
dotnet sharpfuzz out/ManagedCompressions.Zstd.dll
dotnet sharpfuzz out/ManagedCompressions.Lz4.dll
./libfuzzer-dotnet --target_path=out/ManagedCompressions.Fuzz --target_arg=zstd-decompress -max_len=65536 corpus/zstd-decompress
```

The seeds come first: instrumented, the libraries report each branch to memory that libFuzzer sets up,
and outside libFuzzer they fault on the first one.

Without libFuzzer, on any system, an uninstrumented build runs inputs directly:

```sh
ManagedCompressions.Fuzz list                                 # the targets
ManagedCompressions.Fuzz seed <target> <directory>            # the inputs a target starts from
ManagedCompressions.Fuzz replay <target> <file or directory>  # each input once
ManagedCompressions.Fuzz smoke <target> <directory> <count>   # random mutations of the seeds, without coverage
```

## A failure

Replay the input to see the broken guarantee, fix the library, and add the input under
`regressions/<target>/`, named after what it broke. CI replays it on every pull request.
