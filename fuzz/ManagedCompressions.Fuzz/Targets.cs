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
        new("zstd-stream-decompress", "[flags][seed][stream]", ZstdTargets.DecompressStream, ZstdTargets.DecompressStreamSeeds),
        new("zstd-stream-compress", "[level][flags][seed][content]", ZstdTargets.CompressStream, ZstdTargets.CompressStreamSeeds),
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

    /// <summary>The window limit of the streaming decoders that bit 5 of the stream target's flags picks.</summary>
    private const int LowWindowLog = 20;

    /// <summary>
    /// The streaming decoders, without and with the dictionary, at the default window limit and at
    /// <see cref="LowWindowLog"/>: reused from input to input, after <see cref="ZstdDecoder.Reset"/>, as
    /// an application reuses them.
    /// </summary>
    private static readonly ZstdDecoder[] StreamDecoders =
    [
        new(),
        new(Dictionary),
        new(new ZstdDecompressionOptions { MaxWindowLog = LowWindowLog }),
        new(new ZstdDecompressionOptions { MaxWindowLog = LowWindowLog, Dictionary = Dictionary }),
    ];

    /// <summary>The content the streaming decoder writes, gathered from its destinations.</summary>
    private static readonly byte[] Content = new byte[MaxContent];

    /// <summary>What libzstd one-shot decodes of a frame the streaming decoder refuses.</summary>
    private static readonly byte[] OneShot = new byte[MaxContent];

    /// <summary>A skippable frame of three bytes, between the frames of some stream seeds.</summary>
    private static readonly byte[] SkippableFrame = [0x50, 0x2A, 0x4D, 0x18, 3, 0, 0, 0, 1, 2, 3];

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
    /// <c>[flags][seed][stream]</c>: the stream through <see cref="ZstdDecoder"/>, in pieces, into
    /// destinations, of the sizes <c>seed</c> draws, each against the upper or the lower guard page as
    /// it draws too. Flags: bit 0, the dictionary; bits 1 and 2, the pieces: the rest of the stream,
    /// one byte, up to 31 bytes, or mostly a few KiB; bits 3 and 4, the destinations alike; bit 5, a
    /// window limit of 2^<see cref="LowWindowLog"/> bytes rather than the default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each call must keep to its spans, take its whole source when it asks for more, fill its
    /// destination when it is too small, and make progress when it can. The verdict is then
    /// libzstd's streaming decoder's, handed the whole stream (<see cref="Native.ZstdDecompressStream"/>):
    /// a stream the decoder decodes, libzstd decodes to the same bytes; a stream it finds cut short,
    /// libzstd finds cut short; a stream it refuses, libzstd refuses, but for the one-shot target's
    /// exceptions, and for a window over the limit: libzstd decodes such a frame in one pass where a
    /// call holds the whole frame and room for its declared content, as the whole stream does and
    /// the pieces seldom do. Where the decoder stops short of libzstd, what it wrote is the start of
    /// libzstd's content.
    /// </para>
    /// <para>
    /// The content is compared up to <c>MaxContent</c> bytes: a stream that holds more is left there.
    /// </para>
    /// </remarks>
    public static void DecompressStream(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        int flags = input[0];
        var random = new Random(input[1]);
        ReadOnlySpan<byte> stream = input[2..];
        bool dictionary = (flags & 1) != 0;
        bool limited = (flags & 0x20) != 0;
        ZstdDecoder decoder = StreamDecoders[(dictionary ? 1 : 0) + (limited ? 2 : 0)];
        decoder.Reset();

        int at = 0;
        int total = 0;
        int frameStart = 0;
        int stalls = 0;
        OperationStatus status;
        while (true)
        {
            int take = Math.Min(Draw((flags >> 1) & 3, random), stream.Length - at);
            int room = Math.Min(Draw((flags >> 3) & 3, random), MaxContent - total);
            Span<byte> src = random.Next(2) == 0 ? Source.AtEnd(take) : Source.AtStart(take);
            stream.Slice(at, take).CopyTo(src);
            Span<byte> dst = random.Next(2) == 0 ? Destination.AtEnd(room) : Destination.AtStart(room);
            status = decoder.Decompress(src, dst, out int consumed, out int written);
            if (consumed < 0 || consumed > take || written < 0 || written > room)
            {
                throw new FuzzFailure($"{status}: {consumed} bytes consumed of {take}, {written} written into {room}");
            }

            dst[..written].CopyTo(Content.AsSpan(total));
            total += written;
            at += consumed;
            if (status == OperationStatus.InvalidData)
            {
                break;
            }

            if (status == OperationStatus.Done)
            {
                frameStart = at;
                if (at == stream.Length)
                {
                    break;
                }
            }
            else if (status == OperationStatus.NeedMoreData)
            {
                if (consumed != take)
                {
                    throw new FuzzFailure($"asks for more data with {take - consumed} bytes left in its source");
                }

                if (at == stream.Length)
                {
                    break;
                }
            }
            else if (written != room)
            {
                throw new FuzzFailure($"destination too small with {room - written} bytes left in it");
            }
            else if (total == MaxContent)
            {
                return;
            }

            // A call with bytes to take and room to fill must do something with them.
            stalls = consumed == 0 && written == 0 && take > 0 && room > 0 ? stalls + 1 : 0;
            if (stalls > 1)
            {
                throw new FuzzFailure($"{status}: no progress at byte {at} of {stream.Length}");
            }
        }

        int windowLogMax = limited ? LowWindowLog : ZstdDecompressionOptions.DefaultMaxWindowLog;
        Native.StreamEnd theirs = Native.ZstdDecompressStream(stream, Expected, dictionary ? NativeDictionary : null, windowLogMax, out int expected);
        bool same = total <= expected && Expected.AsSpan(0, total).SequenceEqual(Content.AsSpan(0, total));
        string verdict = $"{status} ({decoder.LastError}) at byte {at} of {stream.Length}, {total} bytes written; libzstd: {theirs}, {expected} bytes";
        switch (status)
        {
            case OperationStatus.Done:
                if (theirs != Native.StreamEnd.Decoded || expected != total || !same)
                {
                    throw new FuzzFailure(verdict);
                }

                break;

            case OperationStatus.NeedMoreData:
                // libzstd hands a raw block out as it arrives, the decoder once it is whole.
                if (theirs != Native.StreamEnd.Incomplete || !same)
                {
                    throw new FuzzFailure(verdict);
                }

                break;

            default:
                // Where libzstd does not refuse the stream, but decodes it or finds it cut short, the
                // frame the decoder refuses must be one of the exceptions, which libzstd goes past.
                if (theirs != Native.StreamEnd.Refused && (!same || !Excused(decoder.LastError, stream[frameStart..], dictionary, windowLogMax)))
                {
                    throw new FuzzFailure(verdict);
                }

                break;
        }
    }

    /// <summary>
    /// Whether the streaming decoder may refuse <paramref name="frame"/>, for <paramref name="error"/>,
    /// where libzstd's streaming decoder goes past it: a format before v0.8; one of libzstd one-shot's
    /// two leniencies; a window over the limit, which libzstd decodes in one pass, but refuses when the
    /// frame arrives in pieces; and the two leniencies of libzstd's streaming path that its one-shot
    /// decoder does not share, provided that it refuses the frame: a compressed block of size zero,
    /// skipped as an empty block, and a content size checked only after a last block that is not empty.
    /// </summary>
    private static bool Excused(ZstdError error, ReadOnlySpan<byte> frame, bool dictionary, int windowLogMax)
    {
        ZSTD_DDict_s* native = dictionary ? NativeDictionary : null;
        return IsLegacy(frame)
            || error is ZstdError.HuffmanStream or ZstdError.BlockTooLarge
            || (error == ZstdError.WindowTooLarge && Native.ZstdRefusesWindow(frame, native, windowLogMax))
            || (error is ZstdError.LiteralsHeader or ZstdError.ContentSizeMismatch && Native.ZstdDecompress(frame, OneShot, native, out _) < 0);
    }

    /// <summary>
    /// A piece's size, or a destination's: <paramref name="mode"/> 0, all there is; 1, a byte; 2, up to
    /// 31 bytes; 3, mostly a few KiB, sometimes nothing or a few bytes, sometimes more than a block.
    /// </summary>
    private static int Draw(int mode, Random random) => mode switch
    {
        0 => int.MaxValue,
        1 => 1,
        2 => random.Next(32),
        _ => random.Next(10) switch
        {
            0 => random.Next(4),
            1 => random.Next(4, 300),
            2 => random.Next(128 << 10, 400 << 10),
            _ => random.Next(300, 70_000),
        },
    };

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
        byte[] expected = Native.ZstdCompress(content, level, checksum, dictionary ? NativeCompressionDictionary(level) : null);
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

    /// <summary>
    /// Streams of every kind of data, in four shapes (see <see cref="SeedStream"/>), with flags and
    /// seeds that vary the pieces, the destinations and the window limit.
    /// </summary>
    public static IEnumerable<byte[]> DecompressStreamSeeds()
    {
        int[] levels = [-5, 1, 3, 9, 19];
        int seed = 0;
        foreach (string kind in DataKinds.All)
        {
            for (int shape = 0; shape < 4; shape++)
            {
                byte[] content = DataKinds.Generate(kind, 200 + (seed * 997 % 20_000), seed + 1);
                byte[] stream = SeedStream(content, levels[seed % levels.Length], shape, seed % 2 == 0);
                int flags = (shape == 3 ? 1 : 0) | ((seed % 4) << 1) | ((seed / 4 % 4) << 3) | (seed % 5 == 0 ? 0x20 : 0);
                yield return [(byte)flags, (byte)seed, .. stream];
                seed++;
            }
        }

        // A single segment of 1.2 MB, over the low window limit: refused in pieces of a byte, which
        // libzstd decodes in one pass; decoded in place when whole, as libzstd decodes it.
        byte[] single = LargeSingleSegment();
        yield return [0x20 | (1 << 1), 0, .. single];
        yield return [0x20, 0, .. single];
    }

    /// <summary>A frame of 1.2 MB of a repeated pattern, a single segment, whose window is its content size.</summary>
    private static byte[] LargeSingleSegment()
    {
        byte[] content = new byte[1_200_000];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)('a' + (i % 7));
        }

        return Native.ZstdCompress(content, 3, checksum: true, null);
    }

    /// <summary>
    /// <paramref name="content"/> as libzstd compresses it, in one of four shapes: 0, one frame, which
    /// declares its size; 1, one frame without its size, with a checksum and a window of 1 KiB, which
    /// restarts again and again; 2, two frames, the second without its size, around a skippable one;
    /// 3, one frame with the dictionary. <paramref name="variant"/> adds a checksum to the first, and
    /// the size to the last.
    /// </summary>
    private static byte[] SeedStream(byte[] content, int level, int shape, bool variant)
    {
        int half = content.Length / 2;
        return shape switch
        {
            0 => Native.ZstdCompress(content, level, checksum: variant, null),
            1 => Native.ZstdCompress(content, level, checksum: true, null, contentSize: false, windowLog: 10),
            2 =>
            [
                .. Native.ZstdCompress(content.AsSpan(0, half), level, checksum: true, null),
                .. SkippableFrame,
                .. Native.ZstdCompress(content.AsSpan(half), level, checksum: false, null, contentSize: false),
            ],
            _ => Native.ZstdCompress(content, level, checksum: false, NativeCompressionDictionary(level), contentSize: variant),
        };
    }

    /// <summary><see cref="Dictionary"/> as libzstd prepares it to compress at <paramref name="level"/>, once per level.</summary>
    private static ZSTD_CDict_s* NativeCompressionDictionary(int level)
    {
        if (!NativeCompressionDictionaries.TryGetValue(level, out nint prepared))
        {
            prepared = (nint)Native.ZstdCompressionDictionary(Dictionary, level);
            NativeCompressionDictionaries.Add(level, prepared);
        }

        return (ZSTD_CDict_s*)prepared;
    }

    /// <summary>The levels of the stream compression target: every strategy's, from -7 to 19.</summary>
    private static readonly int[] StreamLevels = [-7, -1, 1, 2, 3, 4, 5, 7, 9, 13, 16, 19];

    /// <summary>The windows of the stream compression target, small, so that the input buffer wraps within an input.</summary>
    private static readonly int[] StreamWindowLogs = [10, 12, 14, 17];

    /// <summary>The stream compression target's encoders, both sides, by level, window, dictionary and checksum.</summary>
    private static readonly Dictionary<int, (ZstdEncoder Ours, NativeStreamEncoder Theirs)> StreamEncoders = [];

    /// <summary>
    /// <c>[level][flags][seed][content]</c>: the content through <see cref="ZstdEncoder"/> and libzstd's
    /// <c>ZSTD_compressStream2</c> alike, in the calls <c>seed</c> draws: pieces of every size, flushes,
    /// frames ended mid-way, destinations from nothing to a megabyte, each against a guard page. Level:
    /// <c>level % 12</c> picks one of every strategy's levels from -7 to 19. Flags: bit 0, a checksum;
    /// bit 1, the dictionary; bits 2 and 3, a window of 2^10, 2^12, 2^14 or 2^17 bytes, which the input
    /// buffer wraps in; bits 4 and 5, the pieces, and bit 6, the destinations, as <see cref="Draw"/> draws
    /// them; bit 7, the size pledged, the frame then never ended before the content is.
    /// </summary>
    /// <remarks>
    /// Each call must take and write libzstd's bytes, and end as libzstd's does; the frames must decode
    /// back. The encoders are reused from input to input, after <c>Reset</c>, as an application reuses them.
    /// </remarks>
    public static void CompressStream(ReadOnlySpan<byte> input)
    {
        if (input.Length < 3)
        {
            return;
        }

        int levelIndex = input[0] % StreamLevels.Length;
        int level = StreamLevels[levelIndex];
        int flags = input[1];
        var random = new Random(input[2]);
        bool checksum = (flags & 1) != 0;
        bool dictionary = (flags & 2) != 0;
        int windowIndex = (flags >> 2) & 3;
        bool pledge = (flags & 0x80) != 0;
        int key = (levelIndex << 4) | (windowIndex << 2) | (dictionary ? 2 : 0) | (checksum ? 1 : 0);
        if (!StreamEncoders.TryGetValue(key, out (ZstdEncoder Ours, NativeStreamEncoder Theirs) encoders))
        {
            int windowLog = StreamWindowLogs[windowIndex];
            var options = new ZstdCompressionOptions { Level = level, WindowLog = windowLog, AppendChecksum = checksum, Dictionary = dictionary ? Dictionary : default };
            encoders = (new ZstdEncoder(options), new NativeStreamEncoder(level, checksum, windowLog, dictionary ? NativeCompressionDictionary(level) : null));
            StreamEncoders.Add(key, encoders);
        }

        (ZstdEncoder ours, NativeStreamEncoder theirs) = encoders;
        ours.Reset();
        theirs.Reset();
        Span<byte> content = Source.AtEnd(input.Length - 3);
        input[3..].CopyTo(content);
        if (pledge)
        {
            ours.SetSourceLength(content.Length);
            theirs.SetSourceLength(content.Length);
        }

        int at = 0;
        int total = 0;
        int step = 0;
        while (true)
        {
            // The frame ends once the content is offered whole; before, mostly continued, at times
            // flushed, or ended mid-way when no size is pledged.
            int choice = random.Next(16);
            bool end = at == content.Length || (choice == 0 && !pledge);
            bool flush = !end && choice < 3;
            int take = flush ? 0 : Math.Min(Draw((flags >> 4) & 3, random), content.Length - at);
            while (true)
            {
                int room = Math.Min(Draw((flags & 0x40) != 0 ? 3 : 2, random), MaxContent - total);
                Span<byte> dst = random.Next(2) == 0 ? Destination.AtEnd(room) : Destination.AtStart(room);
                Span<byte> expected = Expected.AsSpan(0, room);
                ReadOnlySpan<byte> source = content.Slice(at, take);
                int ourConsumed = 0;
                int theirConsumed = 0;
                int ourWritten;
                int theirWritten;
                OperationStatus ourStatus;
                OperationStatus theirStatus;
                if (flush)
                {
                    ourStatus = ours.Flush(dst, out ourWritten);
                    theirStatus = theirs.Flush(expected, out theirWritten);
                }
                else
                {
                    ourStatus = ours.Compress(source, dst, out ourConsumed, out ourWritten, end);
                    theirStatus = theirs.Compress(source, expected, out theirConsumed, out theirWritten, end);
                }

                if (ourStatus != theirStatus || ourConsumed != theirConsumed || ourWritten != theirWritten
                    || !dst[..ourWritten].SequenceEqual(expected[..theirWritten]))
                {
                    throw new FuzzFailure(
                        $"level {level}, call {step} ({(flush ? "flush" : end ? "end" : "continue")}, {take} bytes at {at}, room {room}): "
                        + $"{ourStatus}, {ourConsumed} taken, {ourWritten} written; libzstd: {theirStatus}, {theirConsumed} taken, {theirWritten} written");
                }

                if (ourStatus == OperationStatus.InvalidData)
                {
                    throw new FuzzFailure($"level {level}, call {step}: refused a content of the size pledged");
                }

                dst[..ourWritten].CopyTo(Content.AsSpan(total));
                total += ourWritten;
                at += ourConsumed;
                take -= ourConsumed;
                step++;
                if (total == MaxContent)
                {
                    return;
                }

                // A final call repeats until the frame is written to its end.
                if (!end || ourStatus == OperationStatus.Done)
                {
                    break;
                }
            }

            if (end && at == content.Length)
            {
                break;
            }
        }

        // The frames, one after the other, decode to the content.
        ZstdDecompressor decoder = dictionary ? Prepared : Plain;
        int read = 0;
        int decoded = 0;
        while (read < total)
        {
            if (decoder.Decompress(Content.AsSpan(read, total - read), Expected.AsSpan(decoded), out int consumed, out int written) != OperationStatus.Done)
            {
                throw new FuzzFailure($"level {level}: the frame at byte {read} does not decode ({decoder.LastError})");
            }

            read += consumed;
            decoded += written;
        }

        if (!Expected.AsSpan(0, decoded).SequenceEqual(content))
        {
            throw new FuzzFailure($"level {level}: the frames decode to {decoded} bytes that are not the content's {content.Length}");
        }
    }

    /// <summary>Contents of every kind at every level of the stream target, with flags that vary all they pick.</summary>
    public static IEnumerable<byte[]> CompressStreamSeeds()
    {
        int seed = 0;
        foreach (string kind in DataKinds.All)
        {
            for (int i = 0; i < 4; i++)
            {
                byte[] content = DataKinds.Generate(kind, 100 + (seed * 2477 % 30_000), seed + 1);
                int flags = (seed & 3) | ((seed / 4 % 4) << 2) | ((seed % 3) << 4) | ((seed % 2) << 6) | (seed % 5 == 0 ? 0x80 : 0);
                yield return [(byte)(seed % StreamLevels.Length), (byte)flags, (byte)seed, .. content];
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
