using System;
using System.Buffers;
using System.Collections.Generic;
using ManagedCompressions.Brotli;
using ManagedCompressions.Brotli.Tests.Support;
using ManagedCompressions.Zstd.Tests.Support;

namespace ManagedCompressions.Fuzz;

/// <summary>
/// ManagedCompressions.Brotli against brotli 1.1.1, as .NET ships it in System.IO.Compression.Native.
/// The sources and destinations lie against inaccessible pages, so one byte read or written past
/// either end faults.
/// </summary>
internal static unsafe class BrotliTargets
{
    private const int MaxContent = 1 << 22;

    private static readonly GuardedBuffer Source = new(MaxContent);
    private static readonly GuardedBuffer Destination = new(MaxContent);
    private static readonly BrotliDecompressor Decompressor = new();

    /// <summary>
    /// <c>[flags][stream]</c>. Flags: bits 0 and 1, the destination: 4 MiB, the content's size, one byte
    /// less, or <c>(flags &gt;&gt; 2 &amp; 15) * 97</c> bytes; bits 6 and 7, the source and the
    /// destination against the lower guard page rather than the upper.
    /// </summary>
    /// <remarks>
    /// The decoder must return a status, and consume and write nothing unless it decodes. It decodes what
    /// brotli decodes, to the same bytes, and refuses what brotli refuses, with the same error, given the
    /// whole stream and 4 MiB of room. Into less room it may refuse the stream for want of room at the
    /// header of the meta-block that does not fit, where brotli, which decodes into its ring buffer, may
    /// find it invalid or truncated first.
    /// </remarks>
    public static void Decompress(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        int flags = input[0];
        ReadOnlySpan<byte> stream = input[1..];
        (int result, int code, int nativeConsumed, byte[] expected) = NativeBrotli.Decompress(stream, MaxContent);
        int capacity = (flags & 3) switch
        {
            0 => MaxContent,
            1 => expected.Length,
            2 => Math.Max(0, expected.Length - 1),
            _ => ((flags >> 2) & 15) * 97,
        };

        Span<byte> src = (flags & 0x40) != 0 ? Source.AtStart(stream.Length) : Source.AtEnd(stream.Length);
        stream.CopyTo(src);
        Span<byte> dst = (flags & 0x80) != 0 ? Destination.AtStart(capacity) : Destination.AtEnd(capacity);
        OperationStatus status = Decompressor.Decompress(src, dst, out int consumed, out int written);
        BrotliError error = Decompressor.LastError;
        string verdict = $"managed {status} ({error}) into {capacity}, native {result} (error {code}, {expected.Length} bytes)";
        if (status != OperationStatus.Done && (consumed != 0 || written != 0))
        {
            throw new FuzzFailure($"{verdict}, yet {consumed} bytes consumed and {written} written");
        }

        bool fits = result == NativeBrotli.ResultSuccess && expected.Length <= capacity;
        switch (status)
        {
            case OperationStatus.Done:
                if (!fits || consumed != nativeConsumed || written != expected.Length || !dst[..written].SequenceEqual(expected))
                {
                    throw new FuzzFailure($"{verdict}: decoded otherwise");
                }

                return;

            case OperationStatus.DestinationTooSmall:
                if (fits)
                {
                    throw new FuzzFailure($"{verdict}: the content fits");
                }

                return;

            case OperationStatus.NeedMoreData:
                if (result != NativeBrotli.ResultNeedsMoreInput)
                {
                    throw new FuzzFailure($"{verdict}: brotli does not need more input");
                }

                return;

            default:
                if (result != NativeBrotli.ResultError || (NativeBrotli.HasErrorCodes && !Equivalent(error, code)))
                {
                    throw new FuzzFailure($"{verdict}: brotli does not refuse it so");
                }

                return;
        }
    }

    /// <summary>Whether <paramref name="error"/> is the managed name of brotli's <paramref name="code"/> (tests/…/Verdict.cs).</summary>
    private static bool Equivalent(BrotliError error, int code) => code switch
    {
        -1 => error == BrotliError.ExuberantNibble,
        -2 => error == BrotliError.Reserved,
        -3 => error == BrotliError.ExuberantMetaNibble,
        -4 => error == BrotliError.SimpleHuffmanAlphabet,
        -5 => error == BrotliError.SimpleHuffmanSame,
        -6 => error == BrotliError.CodeLengthSpace,
        -7 => error == BrotliError.HuffmanSpace,
        -8 => error == BrotliError.ContextMapRepeat,
        -9 or -10 => error == BrotliError.BlockLength,
        -11 => error == BrotliError.Transform,
        -12 => error == BrotliError.Dictionary,
        -13 => error == BrotliError.WindowBits,
        -14 or -15 => error == BrotliError.Padding,
        -16 => error == BrotliError.Distance,
        -17 => error is BrotliError.BlockSwitch or BrotliError.BlockLength,
        _ => false,
    };

    public static IEnumerable<byte[]> DecompressSeeds()
    {
        int seed = 0;
        foreach (string kind in DataKinds.All)
        {
            for (int quality = 0; quality <= 11; quality += 1 + (seed % 3))
            {
                byte[] content = DataKinds.Generate(kind, 100 + (seed * 1499 % 20_000), seed + 1);
                int window = 10 + (seed % 15);
                byte[] stream = NativeBrotli.Compress(content, quality, window, seed % 3);
                yield return [(byte)(seed & 0xC3), .. stream];
                seed++;
            }
        }
    }
}
