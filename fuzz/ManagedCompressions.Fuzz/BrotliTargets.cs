using System;
using System.Buffers;
using System.Collections.Generic;
using ManagedCompressions.Brotli;
using ManagedCompressions.Brotli.Tests.Support;
using ManagedCompressions.Zstd.Tests.Support;

namespace ManagedCompressions.Fuzz;

/// <summary>
/// ManagedCompressions.Brotli against brotli 1.1.1: libbrotli_ref when it is built, else .NET's.
/// The sources and destinations lie against inaccessible pages, so one byte read or written past
/// either end faults.
/// </summary>
internal static unsafe class BrotliTargets
{
    private const int MaxContent = 1 << 22;

    private static readonly GuardedBuffer Source = new(MaxContent);
    private static readonly GuardedBuffer Destination = new(MaxContent);
    private static readonly BrotliDecompressor Decompressor = new();
    private static readonly Dictionary<(int, int, int), BrotliCompressor> Compressors = [];

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

    /// <summary>
    /// <c>[quality][window and mode][flags][content]</c>. Quality: the byte mod 12; window: 10 plus the byte
    /// mod 15, mode: the byte / 15 mod 3. Flags: bits 0 and 1, the destination: the bound, brotli's
    /// stream's size, one byte less, or <c>(flags &gt;&gt; 2 &amp; 15) * 97</c> bytes; bits 6 and 7, the
    /// source and the destination against the lower guard page rather than the upper. Qualities 10 and
    /// 11 take the first 128 KiB of the content, the others 1 MiB.
    /// </summary>
    /// <remarks>
    /// The compressor must write brotli's bytes into any destination brotli writes them into, refuse any
    /// other without consuming or writing anything, and its stream must decode back to the content.
    /// Compressors are kept by parameters, so that a call also runs on the state another left.
    /// </remarks>
    public static void Compress(ReadOnlySpan<byte> input)
    {
        if (input.Length < 3)
        {
            return;
        }

        int quality = input[0] % 12;
        int window = 10 + (input[1] % 15);
        int mode = input[1] / 15 % 3;
        int flags = input[2];
        ReadOnlySpan<byte> content = input[3..];
        int limit = quality >= 10 ? 1 << 17 : 1 << 20;
        if (content.Length > limit)
        {
            content = content[..limit];
        }

        byte[] stream = NativeBrotli.Compress(content, quality, window, mode);
        int bound = BrotliCompressor.GetMaxCompressedLength(content.Length);
        int capacity = (flags & 3) switch
        {
            0 => bound,
            1 => stream.Length,
            2 => stream.Length - 1,
            _ => ((flags >> 2) & 15) * 97,
        };

        byte[]? expected = NativeBrotli.TryCompress(content, quality, window, mode, capacity);
        Span<byte> src = (flags & 0x40) != 0 ? Source.AtStart(content.Length) : Source.AtEnd(content.Length);
        content.CopyTo(src);
        Span<byte> dst = (flags & 0x80) != 0 ? Destination.AtStart(capacity) : Destination.AtEnd(capacity);
        (int, int, int) key = (quality, window, mode);
        if (!Compressors.TryGetValue(key, out BrotliCompressor? compressor))
        {
            Compressors[key] = compressor = new BrotliCompressor(quality, window, (BrotliMode)mode);
        }

        OperationStatus status = compressor.Compress(src, dst, out int consumed, out int written);
        string verdict = $"q{quality} w{window} mode {mode}, {content.Length} bytes into {capacity}: managed {status}, native {(expected is null ? "refuses" : $"{expected.Length} bytes")}";
        if (expected is null)
        {
            if (status != OperationStatus.DestinationTooSmall || consumed != 0 || written != 0)
            {
                throw new FuzzFailure($"{verdict}: brotli refuses it");
            }

            return;
        }

        if (status != OperationStatus.Done || consumed != content.Length || !dst[..written].SequenceEqual(expected))
        {
            throw new FuzzFailure($"{verdict}: other bytes, the first difference at {FirstDifference(dst[..written], expected)}");
        }

        byte[] decoded = new byte[content.Length];
        if (Decompressor.Decompress(expected, decoded, out _, out int decodedLength) != OperationStatus.Done || decodedLength != content.Length || !content.SequenceEqual(decoded))
        {
            throw new FuzzFailure($"{verdict}: the stream does not decode back");
        }
    }

    private static int FirstDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int length = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < length && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    public static IEnumerable<byte[]> CompressSeeds()
    {
        int seed = 0;
        foreach (string kind in DataKinds.All)
        {
            for (int quality = 0; quality <= 11; quality++)
            {
                byte[] content = DataKinds.Generate(kind, 100 + (seed * 1499 % 20_000), seed + 1);
                yield return [(byte)quality, (byte)((seed * 7) % 45), (byte)(seed & 0xC3), .. content];
                seed++;
            }
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
