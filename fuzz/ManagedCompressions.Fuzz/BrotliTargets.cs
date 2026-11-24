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
    private static readonly byte[] StreamExpected = new byte[MaxContent];

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

    /// <summary>
    /// <c>[quality][window][seed][content]</c>. Quality: the byte mod 12; window: 10 plus the byte mod 15.
    /// The seed draws a script of calls: pieces of the content, flushes, the final call, into
    /// destinations of 0 bytes to 64 KiB.
    /// </summary>
    /// <remarks>
    /// <see cref="BrotliStreamEncoder"/> and .NET's <c>System.IO.Compression.BrotliEncoder</c> take the
    /// script: each call must get the same status, bytes consumed and written, and stream, which decodes
    /// back. Qualities 10 and 11 take the first 64 KiB of the content, the others 512 KiB.
    /// </remarks>
    public static void StreamCompress(ReadOnlySpan<byte> input)
    {
        if (input.Length < 3)
        {
            return;
        }

        int quality = input[0] % 12;
        int window = 10 + (input[1] % 15);
        var random = new Random(input[2]);
        ReadOnlySpan<byte> content = input[3..];
        int limit = quality >= 10 ? 1 << 16 : 1 << 19;
        if (content.Length > limit)
        {
            content = content[..limit];
        }

        var native = new System.IO.Compression.BrotliEncoder(quality, window);
        var managed = new BrotliStreamEncoder(quality, window);
        byte[] nativeOut = new byte[1 << 16];
        byte[] managedOut = new byte[1 << 16];
        var stream = new List<byte>();
        try
        {
            int position = 0;
            for (int step = 0; ; step++)
            {
                int room = random.Next(5) switch { 0 => 0, 1 => random.Next(1, 32), _ => random.Next(32, nativeOut.Length) };
                bool final = position == content.Length ? random.Next(2) == 0 : random.Next(16) == 0;
                bool flush = !final && random.Next(8) == 0;
                int piece = final ? content.Length - position : Math.Min(content.Length - position, random.Next(0, 8192));
                ReadOnlySpan<byte> source = content.Slice(position, piece);
                OperationStatus nativeStatus;
                OperationStatus managedStatus;
                int nativeConsumed = 0;
                int managedConsumed = 0;
                int nativeWritten;
                int managedWritten;
                if (flush)
                {
                    nativeStatus = native.Flush(nativeOut.AsSpan(0, room), out nativeWritten);
                    managedStatus = managed.Flush(managedOut.AsSpan(0, room), out managedWritten);
                }
                else
                {
                    nativeStatus = native.Compress(source, nativeOut.AsSpan(0, room), out nativeConsumed, out nativeWritten, final);
                    managedStatus = managed.Compress(source, managedOut.AsSpan(0, room), out managedConsumed, out managedWritten, final);
                }

                if (nativeStatus != managedStatus || nativeConsumed != managedConsumed || nativeWritten != managedWritten || !nativeOut.AsSpan(0, nativeWritten).SequenceEqual(managedOut.AsSpan(0, managedWritten)))
                {
                    throw new FuzzFailure($"q{quality} w{window}, step {step}, {(flush ? "flush" : final ? "final" : "compress")} of {piece} into {room}: managed {managedStatus} {managedConsumed}/{managedWritten}, .NET {nativeStatus} {nativeConsumed}/{nativeWritten}");
                }

                stream.AddRange(managedOut.AsSpan(0, managedWritten));
                position += managedConsumed;
                if (final && managedStatus == OperationStatus.Done && position == content.Length)
                {
                    break;
                }

                if (step > 1_000_000)
                {
                    throw new FuzzFailure("the script does not end");
                }
            }
        }
        finally
        {
            native.Dispose();
        }

        byte[] decoded = new byte[content.Length];
        if (Decompressor.Decompress(stream.ToArray(), decoded, out _, out int decodedLength) != OperationStatus.Done || decodedLength != content.Length || !content.SequenceEqual(decoded))
        {
            throw new FuzzFailure($"q{quality} w{window}: the stream does not decode back");
        }
    }

    public static IEnumerable<byte[]> StreamCompressSeeds()
    {
        int seed = 0;
        foreach (string kind in DataKinds.All)
        {
            for (int quality = 0; quality <= 11; quality += 1 + (seed % 2))
            {
                byte[] content = DataKinds.Generate(kind, 100 + (seed * 2711 % 40_000), seed + 1);
                yield return [(byte)quality, (byte)((seed * 7) % 15), (byte)seed, .. content];
                seed++;
            }
        }
    }

    /// <summary>
    /// <c>[seed][stream]</c>. The seed draws the pieces the stream comes in, of up to 64 KiB, and
    /// the destinations, from 0 bytes to 64 KiB.
    /// </summary>
    /// <remarks>
    /// <see cref="BrotliStreamDecoder"/> must decode what <see cref="BrotliDecompressor"/> decodes into
    /// 4 MiB, to the same content, consuming the same bytes; refuse what it refuses, with the same error;
    /// and ask for more input for a stream it finds truncated.
    /// </remarks>
    public static void StreamDecompress(ReadOnlySpan<byte> input)
    {
        if (input.Length < 1)
        {
            return;
        }

        var random = new Random(input[0]);
        ReadOnlySpan<byte> stream = input[1..];
        byte[] expected = StreamExpected;
        OperationStatus expectedStatus = Decompressor.Decompress(stream, expected, out int expectedConsumed, out int expectedWritten);
        // Disposed, so that the next input's decoder rents the buffers this one returns.
        using var decoder = new BrotliStreamDecoder();
        var content = new List<byte>();
        byte[] buffer = new byte[1 << 16];
        int position = 0;
        OperationStatus status;
        int steps = 0;
        do
        {
            int piece = Math.Min(stream.Length - position, random.Next(3) == 0 ? random.Next(0, 8) : random.Next(0, 1 << 16));
            int room = random.Next(4) == 0 ? random.Next(0, 8) : random.Next(0, buffer.Length);
            status = decoder.Decompress(stream.Slice(position, piece), buffer.AsSpan(0, room), out int consumed, out int written);
            content.AddRange(buffer.AsSpan(0, written));
            position += consumed;
            if (content.Count > MaxContent || ++steps > 10_000_000)
            {
                return;
            }
        }
        while (status == OperationStatus.DestinationTooSmall || (status == OperationStatus.NeedMoreData && position < stream.Length));

        string verdict = $"managed {status} ({decoder.LastError}), {position} consumed, {content.Count} written; one-shot {expectedStatus} ({Decompressor.LastError}), {expectedConsumed} consumed, {expectedWritten} written";
        switch (expectedStatus)
        {
            case OperationStatus.Done:
                if (status != OperationStatus.Done || position != expectedConsumed || content.Count != expectedWritten || !expected.AsSpan(0, expectedWritten).SequenceEqual(content.ToArray()))
                {
                    throw new FuzzFailure($"{verdict}: decoded otherwise");
                }

                return;
            case OperationStatus.InvalidData:
                if (status != OperationStatus.InvalidData || decoder.LastError != Decompressor.LastError)
                {
                    throw new FuzzFailure($"{verdict}: not refused so");
                }

                return;
            case OperationStatus.NeedMoreData:
                if (status != OperationStatus.NeedMoreData)
                {
                    throw new FuzzFailure($"{verdict}: not truncated");
                }

                return;
            default:
                return;
        }
    }

    public static IEnumerable<byte[]> StreamDecompressSeeds()
    {
        foreach (byte[] seed in DecompressSeeds())
        {
            yield return seed;
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
