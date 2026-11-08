using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using ManagedCompressions.Lz4;
using ManagedCompressions.Zstd;
using ManagedCompressions.Zstd.Internal;
using ManagedCompressions.Zstd.Tests.Support;
using NativeCompressions.Interop;
using SharpFuzz;

namespace ManagedCompressions.Fuzz;

/// <summary>A fuzz target: what its input encodes, the code it runs, and the inputs it starts from.</summary>
internal sealed record Target(string Name, string Input, ReadOnlySpanAction Run, Func<IEnumerable<byte[]>> Seeds);

/// <summary>
/// The targets. Each one fails an input by throwing: libFuzzer then keeps it as a crash. A target
/// throws <see cref="FuzzFailure"/> for a broken guarantee; any other exception is one too, as is a
/// fault on the guard pages around the sources and destinations, or a hang (libFuzzer's -timeout).
/// </summary>
internal static class Targets
{
    public static readonly Target[] All =
    [
        new("zstd-decompress", "[flags][frame]", ZstdTargets.Decompress, ZstdTargets.DecompressSeeds),
        new("zstd-compress", "[level][flags][content]", ZstdTargets.Compress, ZstdTargets.CompressSeeds),
        new("lz4-decompress", "[capacity, 3 bytes][block]", Lz4Targets.Decompress, Lz4Targets.DecompressSeeds),
        new("lz4-compress", "[level][content]", Lz4Targets.Compress, Lz4Targets.CompressSeeds),
    ];

    public static Target? Find(string name) => Array.Find(All, t => t.Name == name);
}

/// <summary>An input broke a guarantee.</summary>
internal sealed class FuzzFailure(string message) : Exception(message);

/// <summary>
/// ManagedCompressions.Zstd against libzstd 1.5.7. The sources and destinations lie against
/// inaccessible pages, so one byte read or written past either end faults.
/// </summary>
internal static unsafe class ZstdTargets
{
    private const int MaxContent = 1 << 22;

    private static readonly GuardedBuffer Source = new(MaxContent);
    private static readonly GuardedBuffer Destination = new(MaxContent);
    private static readonly byte[] Expected = new byte[MaxContent];

    /// <summary>A dictionary in the zstd format, which libzstd trains on json records.</summary>
    private static readonly byte[] Dictionary = TrainDictionary();

    private static readonly ZstdDecompressor Plain = new();
    private static readonly ZstdDecompressor Prepared = new(Dictionary);
    private static readonly ZSTD_DDict_s* NativeDictionary = Native.ZstdDecompressionDictionary(Dictionary);
    private static readonly Dictionary<(int Level, bool Dictionary), ZstdCompressor> Compressors = [];
    private static readonly Dictionary<int, nint> NativeCompressionDictionaries = [];

    /// <summary>
    /// <c>[flags][frame]</c>. Flags: bit 0, the dictionary; bits 1 and 2, the destination: the declared
    /// content size (64 KiB when there is none), half of it, 4 KiB more, or <c>flags &gt;&gt; 3</c>
    /// bytes; bit 5, <see cref="ZstdDecompressor.DecompressPrefix"/> as well; bits 6 and 7, the source
    /// and the destination against the upper guard page rather than the lower.
    /// </summary>
    /// <remarks>
    /// The decoder must return a status, consume and write nothing unless it decodes, and decode only
    /// what libzstd one-shot decodes, to the same bytes. A frame it refuses, libzstd must refuse too,
    /// but for libzstd's two leniencies the tests document, a Huffman stream whose last symbol overruns
    /// it by a few bits and a block larger than its maximum size, and for the frames of zstd's formats
    /// before v0.8: NativeCompressions builds libzstd with their decoders, which RFC 8878 does not
    /// define and this decoder does not have. libFuzzer found one at once (regressions/zstd-decompress).
    /// </remarks>
    public static void Decompress(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        int flags = input[0];
        ReadOnlySpan<byte> frame = input[1..];
        bool dictionary = (flags & 1) != 0;
        int declared = ZstdDecompressor.TryGetFrameContentSize(frame, out ulong size) && size <= MaxContent ? (int)size : 1 << 16;
        int capacity = ((flags >> 1) & 3) switch
        {
            0 => declared,
            1 => declared / 2,
            2 => Math.Min(declared + 4096, MaxContent),
            _ => flags >> 3,
        };

        Span<byte> src = (flags & 0x40) != 0 ? Source.AtEnd(frame.Length) : Source.AtStart(frame.Length);
        frame.CopyTo(src);
        Span<byte> dst = (flags & 0x80) != 0 ? Destination.AtEnd(capacity) : Destination.AtStart(capacity);
        ZstdDecompressor decoder = dictionary ? Prepared : Plain;
        ZSTD_DDict_s* native = dictionary ? NativeDictionary : null;

        OperationStatus status = decoder.Decompress(src, dst, out int consumed, out int written);
        if (status != OperationStatus.Done)
        {
            if (consumed != 0 || written != 0)
            {
                throw new FuzzFailure($"{status} ({decoder.LastError}), yet {consumed} bytes consumed and {written} written");
            }

            int expected = Native.ZstdDecompress(frame, Expected, native, out _);
            if (expected >= 0 && expected <= capacity && status != OperationStatus.NeedMoreData
                && decoder.LastError is not (ZstdError.HuffmanStream or ZstdError.BlockTooLarge)
                && !IsLegacy(frame))
            {
                throw new FuzzFailure($"refused ({decoder.LastError}) a frame libzstd decodes to {expected} bytes");
            }
        }
        else
        {
            if (consumed > frame.Length || written > capacity)
            {
                throw new FuzzFailure($"consumed {consumed} of {frame.Length} bytes, wrote {written} into {capacity}");
            }

            int expected = Native.ZstdDecompress(frame[..consumed], Expected, native, out int frameSize);
            if (expected != written || frameSize != consumed || !Expected.AsSpan(0, written).SequenceEqual(dst[..written]))
            {
                throw new FuzzFailure($"decoded {written} bytes from {consumed}, where libzstd gives {expected} from {frameSize}");
            }
        }

        if ((flags & 0x20) != 0)
        {
            Prefix(src, dst, dictionary, native);
        }
    }

    /// <summary>
    /// Whether <paramref name="frame"/> starts with the magic number of a zstd format before v0.8,
    /// v0.1 to v0.7, which libzstd's legacy decoders read.
    /// </summary>
    private static bool IsLegacy(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4)
        {
            return false;
        }

        uint magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(frame);
        return magic == 0xFD2FB51E || magic is >= 0xFD2FB522 and <= 0xFD2FB527;
    }

    /// <summary>
    /// The start of the frame, with the dictionary given with the call: whatever it returns must be
    /// the start of what libzstd decodes, when libzstd decodes the frame.
    /// </summary>
    private static void Prefix(ReadOnlySpan<byte> frame, Span<byte> destination, bool dictionary, ZSTD_DDict_s* native)
    {
        OperationStatus status = Plain.DecompressPrefix(frame, destination, dictionary ? Dictionary : default, out int written);
        if (status != OperationStatus.Done)
        {
            if (written != 0)
            {
                throw new FuzzFailure($"prefix: {status} ({Plain.LastError}), yet {written} bytes written");
            }

            return;
        }

        if (written > destination.Length)
        {
            throw new FuzzFailure($"prefix: wrote {written} into {destination.Length}");
        }

        int expected = Native.ZstdDecompress(frame, Expected, native, out _);
        if (expected >= 0 && (expected < written || !Expected.AsSpan(0, written).SequenceEqual(destination[..written])))
        {
            throw new FuzzFailure($"prefix: {written} bytes that are not the start of libzstd's {expected}");
        }
    }

    /// <summary>
    /// <c>[level][flags][content]</c>. The level is <c>level % 30 - 7</c>, from -7 to 22. Flags: bit 0,
    /// a checksum; bit 1, the dictionary. The frame must be libzstd's, byte for byte, and decode back.
    /// The compressors are reused from input to input, as an application reuses them.
    /// </summary>
    public static void Compress(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        int level = (input[0] % 30) - 7;
        bool checksum = (input[1] & 1) != 0;
        bool dictionary = (input[1] & 2) != 0;
        ReadOnlySpan<byte> content = input[2..];

        if (!Compressors.TryGetValue((level, dictionary), out ZstdCompressor? compressor))
        {
            compressor = dictionary ? new ZstdCompressor(level, Dictionary) : new ZstdCompressor(level);
            Compressors.Add((level, dictionary), compressor);
        }

        compressor.AppendChecksum = checksum;
        Span<byte> src = Source.AtEnd(content.Length);
        content.CopyTo(src);
        Span<byte> dst = Destination.AtEnd(ZstdCompressor.GetMaxCompressedLength(content.Length));
        if (compressor.Compress(src, dst, out int consumed, out int written) != OperationStatus.Done || consumed != content.Length)
        {
            throw new FuzzFailure($"level {level}: the compressor did not take the whole content");
        }

        byte[] frame = dst[..written].ToArray();
        ZSTD_CDict_s* native = null;
        if (dictionary)
        {
            if (!NativeCompressionDictionaries.TryGetValue(level, out nint prepared))
            {
                prepared = (nint)Native.ZstdCompressionDictionary(Dictionary, level);
                NativeCompressionDictionaries.Add(level, prepared);
            }

            native = (ZSTD_CDict_s*)prepared;
        }

        byte[] expected = Native.ZstdCompress(content, level, checksum, native);
        if (!expected.AsSpan().SequenceEqual(frame))
        {
            throw new FuzzFailure($"level {level}, checksum {checksum}, dictionary {dictionary}: {frame.Length} bytes where libzstd writes {expected.Length}");
        }

        Span<byte> back = Destination.AtEnd(content.Length);
        ZstdDecompressor decoder = dictionary ? Prepared : Plain;
        if (decoder.Decompress(frame, back, out consumed, out written) != OperationStatus.Done
            || consumed != frame.Length || written != content.Length || !back.SequenceEqual(content))
        {
            throw new FuzzFailure($"level {level}: the frame does not decode back ({decoder.LastError})");
        }
    }

    /// <summary>Frames of every kind of data, with and without the dictionary, a flag byte before each.</summary>
    public static IEnumerable<byte[]> DecompressSeeds()
    {
        int[] levels = [-5, 1, 3, 9, 19];
        int seed = 0;
        foreach (string kind in DataKinds.All)
        {
            foreach (bool dictionary in (bool[])[false, true])
            {
                int level = levels[seed % levels.Length];
                byte[] content = DataKinds.Generate(kind, 200 + (seed * 997 % 12_000), seed + 1);
                var compressor = dictionary ? new ZstdCompressor(level, Dictionary) : new ZstdCompressor(level);
                compressor.AppendChecksum = seed % 3 == 0;
                byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
                compressor.Compress(content, frame, out _, out int written);
                yield return [(byte)(dictionary ? 1 : 0), .. frame.AsSpan(0, written)];
                seed++;
            }
        }
    }

    /// <summary>Contents of every kind at levels across the range, with and without a checksum or the dictionary.</summary>
    public static IEnumerable<byte[]> CompressSeeds()
    {
        int[] levels = [-7, -1, 1, 2, 3, 5, 7, 9, 13, 16, 19, 22];
        int seed = 0;
        foreach (string kind in DataKinds.All)
        {
            foreach (int level in (int[])[levels[seed % levels.Length], levels[(seed + 5) % levels.Length]])
            {
                byte[] content = DataKinds.Generate(kind, 100 + (seed * 1499 % 16_000), seed + 1);
                yield return [(byte)(level + 7), (byte)(seed & 3), .. content];
                seed++;
            }
        }
    }

    private static byte[] TrainDictionary()
    {
        const int SampleSize = 1024;
        byte[] samples = new byte[200 * SampleSize];
        for (int i = 0; i < 200; i++)
        {
            DataKinds.Generate("json", SampleSize, 1000 + i).CopyTo(samples, i * SampleSize);
        }

        return Native.ZstdTrain(samples, SampleSize, 16 << 10) ?? throw new InvalidDataException("libzstd trained no dictionary.");
    }
}

/// <summary>
/// ManagedCompressions.Lz4 against lz4 1.10.0, the sources and destinations against inaccessible
/// pages.
/// </summary>
internal static class Lz4Targets
{
    private const int MaxContent = 1 << 22;

    private static readonly GuardedBuffer Source = new(MaxContent);
    private static readonly GuardedBuffer Destination = new(MaxContent);
    private static readonly byte[] Expected = new byte[MaxContent];
    private static readonly Dictionary<int, Lz4Compressor> Compressors = [];

    /// <summary>
    /// <c>[capacity, 3 bytes, little-endian][block]</c>. A block lz4 decodes into the capacity, the
    /// decoder must decode to the same bytes; a block lz4 refuses, it may decode, within its own
    /// guarantees (see the remarks). <see cref="Lz4Block.Decompress"/> must agree, refusing with
    /// <see cref="InvalidDataException"/> alone.
    /// </summary>
    /// <remarks>
    /// The decoder and lz4 differ on three kinds of blocks, all found at once, all by design:
    /// <list type="bullet">
    /// <item>lz4 refuses the blocks that break the format's end-of-block rules: the last five bytes
    /// literals, the last match twelve bytes before the end. They are rules for encoders, which this
    /// decoder does not enforce: it decodes such a block when every literal and match lies within its
    /// spans and the block ends with literals.</item>
    /// <item>lz4 decodes a match at offset zero, from bytes the block has not written; this decoder
    /// refuses it (SECURITY.md).</item>
    /// <item>Into an empty destination, both take only the block that is a single zero token, so a
    /// block that decodes to nothing with room to spare can be refused without it.</item>
    /// </list>
    /// </remarks>
    public static void Decompress(ReadOnlySpan<byte> input)
    {
        if (input.Length < 3)
        {
            return;
        }

        int capacity = (input[0] | (input[1] << 8) | (input[2] << 16)) & (MaxContent - 1);
        ReadOnlySpan<byte> block = input[3..];
        Span<byte> src = Source.AtEnd(block.Length);
        block.CopyTo(src);
        Span<byte> dst = Destination.AtEnd(capacity);

        bool decoded = Lz4Block.TryDecompress(src, dst, out int written);
        int expected = Native.Lz4Decompress(block, Expected.AsSpan(0, capacity));
        if (!decoded)
        {
            if (written != 0)
            {
                throw new FuzzFailure($"refused, yet {written} bytes written");
            }

            if (expected >= 0 && !HasZeroOffset(block))
            {
                throw new FuzzFailure($"refused a block lz4 decodes to {expected} bytes");
            }

            try
            {
                Lz4Block.Decompress(src, dst);
                throw new FuzzFailure("Decompress decoded a block TryDecompress refuses");
            }
            catch (InvalidDataException)
            {
            }

            return;
        }

        if (written > capacity)
        {
            throw new FuzzFailure($"wrote {written} into {capacity}");
        }

        if (expected >= 0 && (expected != written || !Expected.AsSpan(0, written).SequenceEqual(dst[..written])))
        {
            throw new FuzzFailure($"decoded {written} bytes, where lz4 gives {expected}");
        }

        // TryDecompress's output, which Decompress must reproduce into exactly its length.
        if (written > 0)
        {
            dst[..written].CopyTo(Expected);
            Span<byte> exact = Destination.AtEnd(written);
            Lz4Block.Decompress(src, exact);
            if (!exact.SequenceEqual(Expected.AsSpan(0, written)))
            {
                throw new FuzzFailure("Decompress decodes otherwise than TryDecompress");
            }
        }
    }

    /// <summary>Whether the block's sequences, read as the format lays them out, hold a match at offset zero.</summary>
    private static bool HasZeroOffset(ReadOnlySpan<byte> block)
    {
        int ip = 0;
        while (ip < block.Length)
        {
            int token = block[ip++];
            long literals = token >> 4;
            if (literals == 15)
            {
                byte more;
                do
                {
                    if (ip >= block.Length)
                    {
                        return false;
                    }

                    more = block[ip++];
                    literals += more;
                }
                while (more == 255);
            }

            // The last literals, or literals past the end: no match follows.
            if (literals >= block.Length - ip)
            {
                return false;
            }

            ip += (int)literals;
            if (block.Length - ip < 2)
            {
                return false;
            }

            if ((block[ip] | (block[ip + 1] << 8)) == 0)
            {
                return true;
            }

            ip += 2;
            if ((token & 15) == 15)
            {
                byte more;
                do
                {
                    if (ip >= block.Length)
                    {
                        return false;
                    }

                    more = block[ip++];
                }
                while (more == 255);
            }
        }

        return false;
    }

    /// <summary>
    /// <c>[level][content]</c>. The level is <c>level % 20 - 7</c>, from -7 to 12. The block must be
    /// lz4's, byte for byte, and decode back. The compressors are reused from input to input: what
    /// earlier blocks leave in their tables must not show in the next.
    /// </summary>
    public static void Compress(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        int level = (input[0] % 20) - 7;
        ReadOnlySpan<byte> content = input[1..];
        if (!Compressors.TryGetValue(level, out Lz4Compressor? compressor))
        {
            compressor = new Lz4Compressor(level);
            Compressors.Add(level, compressor);
        }

        Span<byte> src = Source.AtEnd(content.Length);
        content.CopyTo(src);
        Span<byte> dst = Destination.AtEnd(Lz4Compressor.GetMaxCompressedLength(content.Length));
        int size = compressor.Compress(src, dst);
        byte[] expected = Native.Lz4Compress(content, level);
        if (!expected.AsSpan().SequenceEqual(dst[..size]))
        {
            throw new FuzzFailure($"level {level}: {size} bytes where lz4 writes {expected.Length}");
        }

        Lz4Block.Decompress(dst[..size], Expected.AsSpan(0, content.Length));
        if (!Expected.AsSpan(0, content.Length).SequenceEqual(content))
        {
            throw new FuzzFailure($"level {level}: the block does not decode back");
        }
    }

    /// <summary>lz4's blocks of every kind of data, at levels across the range, each with its exact capacity.</summary>
    public static IEnumerable<byte[]> DecompressSeeds()
    {
        int[] levels = [-3, 1, 2, 9, 12];
        int seed = 0;
        foreach (string kind in DataKinds.All)
        {
            byte[] content = DataKinds.Generate(kind, 200 + (seed * 1499 % 16_000), seed + 1);
            byte[] block = Native.Lz4Compress(content, levels[seed % levels.Length]);
            yield return [(byte)content.Length, (byte)(content.Length >> 8), (byte)(content.Length >> 16), .. block];
            seed++;
        }
    }

    /// <summary>Contents of every kind, at two levels each.</summary>
    public static IEnumerable<byte[]> CompressSeeds()
    {
        int[] levels = [-7, -1, 1, 2, 3, 6, 9, 12];
        int seed = 0;
        foreach (string kind in DataKinds.All)
        {
            foreach (int level in (int[])[levels[seed % levels.Length], levels[(seed + 3) % levels.Length]])
            {
                yield return [(byte)(level + 7), .. DataKinds.Generate(kind, 100 + (seed * 997 % 16_000), seed + 1)];
                seed++;
            }
        }
    }
}
