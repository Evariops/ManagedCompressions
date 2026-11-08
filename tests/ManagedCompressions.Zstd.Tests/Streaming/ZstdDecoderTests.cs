using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using ManagedCompressions.Zstd.Tests.Differential;
using ManagedCompressions.Zstd.Tests.Fuzz;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Streaming;

/// <summary>
/// <see cref="ZstdDecoder"/>: frames handed over in pieces of every size, with destinations of every
/// size, against what they hold; and streams, valid or broken, judged as libzstd's streaming decoder
/// (<c>ZSTD_decompressStream</c>) judges them.
/// </summary>
public sealed class ZstdDecoderTests
{
    public static TheoryData<string> Kinds() => [.. DataKinds.All];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_frame_decodes_in_pieces_of_any_size(string kind)
    {
        (byte[] dictionary, NativeZstdDictionary trained) = Dictionaries.Get(kind, "trained");
        int variant = 0;
        foreach (int size in (int[])[0, 1, 1000, 70_000, 300_000])
        {
            foreach (int level in (int[])[-5, 3, 19])
            {
                if (level == 19 && size > 70_000)
                {
                    continue;
                }

                foreach (bool contentSize in (bool[])[true, false])
                {
                    variant++;
                    bool withDictionary = variant % 3 == 0;
                    byte[] content = DataKinds.Generate(kind, size, variant);
                    var options = new NativeZstdOptions { Quality = level, AppendChecksum = variant % 2 == 0, Dictionary = withDictionary ? trained : null };
                    byte[] frame = NativeZstd.Compress(content, options, noContentSize: !contentSize);
                    string name = $"{kind}/{size}/L{level}/{(contentSize ? "fcs" : "nofcs")}/{(withDictionary ? "dict" : "none")}";
                    Func<ZstdDecoder> decoder = () => withDictionary ? new ZstdDecoder(dictionary) : new ZstdDecoder();

                    AssertDecodes(decoder(), frame, content, Pieces.Whole, Pieces.Whole, name + " whole");
                    var random = new Random(variant);
                    AssertDecodes(decoder(), frame, content, () => Pieces.Random(random), () => Pieces.Random(random), name + " random");
                    if (size <= 70_000)
                    {
                        AssertDecodes(decoder(), frame, content, Pieces.One, Pieces.One, name + " one byte");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(3, 7777)]
    [InlineData(9, 130_000)]
    [InlineData(19, 4096)]
    public void Frames_libzstd_flushed_decode_whole(int level, int flushEvery)
    {
        byte[] content = DataKinds.Generate("mixed", 400_000, level);
        byte[] frame = NativeZstd.CompressStream(content, new NativeZstdOptions { Quality = level, AppendChecksum = true }, flushEvery, flush: true, pledge: false);
        var random = new Random(level);
        AssertDecodes(new ZstdDecoder(), frame, content, () => Pieces.Random(random), () => Pieces.Random(random), $"L{level}, flushed every {flushEvery}");
        AssertDecodes(new ZstdDecoder(), frame, content, Pieces.Whole, Pieces.Whole, $"L{level}, flushed every {flushEvery}, whole");
    }

    /// <summary>
    /// Content four times the window, without a declared size, repeating itself at distances near the
    /// window: the window restarts from its start several times, and matches reach across each
    /// restart, back into what the window held before it.
    /// </summary>
    [Theory]
    [InlineData(17, false)]
    [InlineData(18, true)]
    [InlineData(20, false)]
    public void The_window_restarts_and_the_content_stays_whole(int windowLog, bool withDictionary)
    {
        int window = 1 << windowLog;
        byte[] piece = DataKinds.Generate("incompressible", window - (window / 8), windowLog);
        byte[] content = new byte[4 * window];
        for (int at = 0; at < content.Length; at += piece.Length)
        {
            piece.AsSpan(0, Math.Min(piece.Length, content.Length - at)).CopyTo(content.AsSpan(at));
            content[at] ^= (byte)at;
        }

        (byte[] dictionary, NativeZstdDictionary trained) = Dictionaries.Get("text", "trained");
        var options = new NativeZstdOptions
        {
            Quality = 3, WindowLog = windowLog, EnableLongDistanceMatching = true, AppendChecksum = true,
            Dictionary = withDictionary ? trained : null,
        };
        byte[] frame = NativeZstd.Compress(content, options, noContentSize: true);
        Assert.True(frame.Length < content.Length / 2, $"the repeats should be found: {frame.Length} bytes for {content.Length}");

        Func<ZstdDecoder> decoder = () => withDictionary ? new ZstdDecoder(dictionary) : new ZstdDecoder();
        var random = new Random(windowLog);
        AssertDecodes(decoder(), frame, content, () => Pieces.Random(random), () => Pieces.Random(random), $"window 2^{windowLog}");
        AssertDecodes(decoder(), frame, content, Pieces.Whole, Pieces.Whole, $"window 2^{windowLog}, whole");
    }

    /// <summary>
    /// A frame's window over <see cref="ZstdDecompressionOptions.MaxWindowLog"/>: refused, but where
    /// the whole frame and room for its declared content are there at once, which libzstd decodes in
    /// one pass before it looks at the window. libzstd judges each case the same way.
    /// </summary>
    [Fact]
    public void A_window_over_the_limit_is_refused_as_libzstd_refuses_it()
    {
        byte[] content = DataKinds.Generate("text", 1_500_000, 3);

        // 2 MiB of window, no declared size: libzstd keeps the window it is asked for.
        byte[] unsized = NativeZstd.CompressStream(content, new NativeZstdOptions { Quality = 3, WindowLog = 21 }, 1 << 16, flush: false, pledge: false);
        AssertJudgedAlike(unsized, content.Length, maxWindowLog: 20, "2 MiB window, no size, limit 1 MiB");
        AssertJudgedAlike(unsized, content.Length, maxWindowLog: 21, "2 MiB window, no size, limit 2 MiB");
        Assert.Equal(OperationStatus.InvalidData, Run(new ZstdDecoder(new ZstdDecompressionOptions { MaxWindowLog = 20 }), unsized, content.Length, out _, out ZstdError error));
        Assert.Equal(ZstdError.WindowTooLarge, error);

        // A single segment: its window is its content, 1.5 MB, over a 1 MiB limit.
        byte[] single = NativeZstd.CompressStream(content, new NativeZstdOptions { Quality = 3, WindowLog = 21 }, 1 << 16, flush: false, pledge: true);
        AssertJudgedAlike(single, content.Length, maxWindowLog: 20, "single segment, room for it all");
        AssertJudgedAlike(single, content.Length - 1, maxWindowLog: 20, "single segment, a byte short");

        // In pieces, the frame is never whole in one call: the window is checked, and refused.
        var limited = new ZstdDecoder(new ZstdDecompressionOptions { MaxWindowLog = 20 });
        (OperationStatus status, _, _) = Pieces.Decode(limited, single, () => 4096, () => 1 << 20);
        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(ZstdError.WindowTooLarge, limited.LastError);
    }

    [Fact]
    public void Concatenated_and_skippable_frames_decode_one_after_the_other()
    {
        byte[] a = DataKinds.Generate("json", 50_000, 1);
        byte[] b = DataKinds.Generate("text", 200_000, 2);
        byte[] c = DataKinds.Generate("walk64", 1, 3);
        byte[] stream =
        [
            .. NativeZstd.Compress(a, new NativeZstdOptions { Quality = 3 }),
            .. Skippable(0x184D2A50, 0),
            .. NativeZstd.Compress(b, new NativeZstdOptions { Quality = 1, AppendChecksum = true }, noContentSize: true),
            .. Skippable(0x184D2A5F, 300),
            .. NativeZstd.Compress(c, new NativeZstdOptions { Quality = 19 }),
        ];
        byte[] content = [.. a, .. b, .. c];

        Assert.True(NativeZstd.DecompressStream(stream, content.Length, null, 27, out byte[] native));
        Corpus.AssertSameBytes(content, native, "libzstd");

        var random = new Random(5);
        foreach ((Func<int> source, Func<int> destination, string how) in new (Func<int>, Func<int>, string)[]
        {
            (Pieces.Whole, Pieces.Whole, "whole"),
            (() => Pieces.Random(random), () => Pieces.Random(random), "random"),
            (Pieces.One, Pieces.One, "one byte"),
        })
        {
            (OperationStatus status, byte[] decoded, int frames) = Pieces.Decode(new ZstdDecoder(), stream, source, destination);
            Assert.Equal(OperationStatus.Done, status);
            Assert.Equal(5, frames);
            Corpus.AssertSameBytes(content, decoded, how);
        }
    }

    [Fact]
    public void A_stream_cut_anywhere_asks_for_the_rest()
    {
        byte[] content = DataKinds.Generate("text", 20_000, 9);
        byte[] frame = NativeZstd.Compress(content, new NativeZstdOptions { Quality = 3, AppendChecksum = true }, noContentSize: true);
        byte[] output = new byte[content.Length];
        for (int cut = 0; cut <= frame.Length; cut++)
        {
            var decoder = new ZstdDecoder();
            OperationStatus first = decoder.Decompress(frame.AsSpan(0, cut), output, out int consumed, out int written);
            Assert.Equal(cut, consumed);
            if (cut < frame.Length)
            {
                Assert.Equal(OperationStatus.NeedMoreData, first);
                Assert.True(decoder.InFrame || cut == 0);
                OperationStatus rest = decoder.Decompress(frame.AsSpan(cut), output.AsSpan(written), out int restConsumed, out int restWritten);
                Assert.Equal(OperationStatus.Done, rest);
                Assert.Equal(frame.Length - cut, restConsumed);
                written += restWritten;
            }
            else
            {
                Assert.Equal(OperationStatus.Done, first);
            }

            Assert.Equal(content.Length, written);
            Assert.True(content.AsSpan().SequenceEqual(output), $"cut at {cut}");
            Assert.False(decoder.InFrame);
            Assert.Equal(ZstdError.None, decoder.LastError);
        }
    }

    /// <summary>
    /// Valid frames broken by <see cref="FrameMutator"/>: whatever the decoder makes of a stream, libzstd's
    /// streaming decoder must make the same, given the same destination, but for its two leniencies the
    /// fuzz tests document (a Huffman stream overrun by its last symbol, a block over its maximum size,
    /// in the one-pass shortcut).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Mutated_streams_are_judged_like_libzstd(int seed)
    {
        var random = new Random(seed);
        var frames = new List<(byte[] Frame, int Size)>();
        foreach (string kind in DataKinds.All)
        {
            byte[] content = DataKinds.Generate(kind, random.Next(1, 40_000), seed);
            var options = new NativeZstdOptions { Quality = random.Next(-3, 20), AppendChecksum = random.Next(2) == 0 };
            frames.Add((NativeZstd.Compress(content, options, noContentSize: random.Next(2) == 0), content.Length));
        }

        var mutator = new FrameMutator(random, frames.Select(f => f.Frame).ToList());
        var divergences = new Dictionary<ZstdError, int>();
        for (int i = 0; i < 1500; i++)
        {
            (byte[] original, int size) = frames[random.Next(frames.Count)];
            byte[] stream = mutator.Mutate(original);
            int capacity = random.Next(3) switch { 0 => size, 1 => size + random.Next(1 << 16), _ => random.Next(size + 1) };

            OperationStatus status = Run(new ZstdDecoder(), stream, capacity, out byte[] decoded, out ZstdError error);
            bool ours = status == OperationStatus.Done;
            bool theirs = NativeZstd.DecompressStream(stream, capacity, null, 27, out byte[] native);
            if (ours)
            {
                Assert.True(theirs, $"seed {seed}, mutation {i}: decoded {decoded.Length} bytes of a stream libzstd refuses");
                Corpus.AssertSameBytes(native, decoded, $"seed {seed}, mutation {i}");
            }
            else if (theirs)
            {
                Assert.True(
                    status == OperationStatus.InvalidData && error is ZstdError.HuffmanStream or ZstdError.BlockTooLarge,
                    $"seed {seed}, mutation {i}: {status} ({error}) for a stream libzstd decodes to {native.Length} bytes");
                divergences[error] = divergences.GetValueOrDefault(error) + 1;
            }
        }

        Console.WriteLine($"seed {seed}: refused where libzstd decodes: " + string.Join(", ", divergences.Select(d => $"{d.Key} x{d.Value}")));
    }

    [Fact]
    public void A_refusal_sticks_until_Reset()
    {
        byte[] content = DataKinds.Generate("json", 10_000, 4);
        byte[] frame = NativeZstd.Compress(content, new NativeZstdOptions { Quality = 3 });
        byte[] output = new byte[content.Length];
        var decoder = new ZstdDecoder();

        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress([0x28, 0xB5, 0x2F, 0xFE, 0, 0], output, out _, out _));
        Assert.Equal(ZstdError.UnknownMagic, decoder.LastError);
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(frame, output, out int consumed, out int written));
        Assert.Equal((0, 0), (consumed, written));

        decoder.Reset();
        Assert.Equal(ZstdError.None, decoder.LastError);
        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, output, out consumed, out written));
        Assert.Equal((frame.Length, content.Length), (consumed, written));
    }

    [Fact]
    public void Reset_drops_the_frame_in_progress()
    {
        byte[] first = NativeZstd.Compress(DataKinds.Generate("text", 50_000, 1), new NativeZstdOptions { Quality = 3 }, noContentSize: true);
        byte[] content = DataKinds.Generate("json", 30_000, 2);
        byte[] second = NativeZstd.Compress(content, new NativeZstdOptions { Quality = 5, AppendChecksum = true }, noContentSize: true);
        byte[] output = new byte[100_000];
        var decoder = new ZstdDecoder();
        Assert.Equal(OperationStatus.NeedMoreData, decoder.Decompress(first.AsSpan(0, first.Length / 2), output, out _, out _));
        Assert.True(decoder.InFrame);

        decoder.Reset();
        Assert.False(decoder.InFrame);
        Assert.Equal(OperationStatus.Done, decoder.Decompress(second, output, out int consumed, out int written));
        Assert.Equal((second.Length, content.Length), (consumed, written));
        Assert.True(content.AsSpan().SequenceEqual(output.AsSpan(0, written)));
    }

    [Fact]
    public void An_empty_destination_takes_no_content_and_loses_none()
    {
        byte[] content = DataKinds.Generate("mixed", 300_000, 6);
        byte[] frame = NativeZstd.Compress(content, new NativeZstdOptions { Quality = 3 });
        var decoder = new ZstdDecoder();
        Assert.Equal(OperationStatus.DestinationTooSmall, decoder.Decompress(frame, [], out int consumed, out int written));
        Assert.Equal(0, written);
        Assert.InRange(consumed, 1, frame.Length);

        byte[] output = new byte[content.Length];
        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame.AsSpan(consumed), output, out int restConsumed, out written));
        Assert.Equal(frame.Length, consumed + restConsumed);
        Assert.Equal(content.Length, written);
        Assert.True(content.AsSpan().SequenceEqual(output));
    }

    [Fact]
    public void A_frame_that_names_another_dictionary_is_refused()
    {
        (byte[] json, NativeZstdDictionary trained) = Dictionaries.Get("json", "trained");
        byte[] frame = NativeZstd.Compress(DataKinds.Generate("json", 5000, 1), new NativeZstdOptions { Quality = 3, Dictionary = trained }, noContentSize: true);
        var decoder = new ZstdDecoder();
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(frame, new byte[10_000], out _, out _));
        Assert.Equal(ZstdError.DictionaryMismatch, decoder.LastError);
        Assert.False(NativeZstd.DecompressStream(frame, 10_000, null, 27, out _));
        Assert.Equal(OperationStatus.Done, new ZstdDecoder(json).Decompress(frame, new byte[10_000], out _, out _));
    }

    /// <summary>
    /// Frame after frame, decoded in place and in pieces, once the decoder is warm: nothing allocated,
    /// measured as <see cref="AllocationTests"/> measures, the least of several rounds.
    /// </summary>
    [Fact]
    public void A_warm_decoder_allocates_nothing()
    {
#if DEBUG
        Assert.Skip("allocations are a property of the optimized build");
#endif
        byte[] content = DataKinds.Generate("text", 200_000, 8);
        byte[] sized = NativeZstd.Compress(content, new NativeZstdOptions { Quality = 3, AppendChecksum = true });
        byte[] unsized = NativeZstd.Compress(content, new NativeZstdOptions { Quality = 3, AppendChecksum = true }, noContentSize: true);
        byte[] output = new byte[content.Length];
        var decoder = new ZstdDecoder();

        // No assertion inside: the test framework's own allocate.
        bool DecodeBoth()
        {
            bool done = decoder.Decompress(sized, output, out _, out _) == OperationStatus.Done;
            int at = 0;
            int written = 0;
            OperationStatus status;
            do
            {
                status = decoder.Decompress(unsized.AsSpan(at, Math.Min(5000, unsized.Length - at)), output.AsSpan(written, Math.Min(7000, output.Length - written)), out int consumed, out int wrote);
                at += consumed;
                written += wrote;
            }
            while (status is OperationStatus.NeedMoreData or OperationStatus.DestinationTooSmall);

            return done && status == OperationStatus.Done && written == content.Length;
        }

        for (int i = 0; i < 3; i++)
        {
            Assert.True(DecodeBoth());
        }

        long least = long.MaxValue;
        bool decoded = true;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++)
            {
                decoded &= DecodeBoth();
            }

            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.True(decoded);
        Assert.Equal(0, least);
    }

    private static void AssertDecodes(ZstdDecoder decoder, byte[] frame, byte[] content, Func<int> source, Func<int> destination, string name)
    {
        (OperationStatus status, byte[] decoded, int frames) = Pieces.Decode(decoder, frame, source, destination);
        Assert.True(status == OperationStatus.Done, $"{name}: {status} ({decoder.LastError})");
        Assert.Equal(1, frames);
        Corpus.AssertSameBytes(content, decoded, name);
        Assert.Equal(ZstdError.None, decoder.LastError);
    }

    /// <summary>The stream at once and the content into <paramref name="capacity"/> bytes, the decoder judged against libzstd's.</summary>
    private static void AssertJudgedAlike(byte[] stream, int capacity, int maxWindowLog, string name)
    {
        OperationStatus status = Run(new ZstdDecoder(new ZstdDecompressionOptions { MaxWindowLog = maxWindowLog }), stream, capacity, out byte[] decoded, out ZstdError error);
        bool theirs = NativeZstd.DecompressStream(stream, capacity, null, maxWindowLog, out byte[] native);
        Assert.True(theirs == (status == OperationStatus.Done), $"{name}: {status} ({error}), libzstd {(theirs ? "decodes" : "refuses")}");
        if (theirs)
        {
            Corpus.AssertSameBytes(native, decoded, name);
        }
    }

    /// <summary>
    /// The stream through <paramref name="decoder"/> as <see cref="NativeZstd.DecompressStream"/> runs
    /// libzstd's: each call handed all the input left and all the room left in a destination of
    /// <paramref name="capacity"/> bytes.
    /// </summary>
    /// <returns><see cref="OperationStatus.Done"/> when every frame decodes and nothing is left; otherwise the status that stopped it.</returns>
    private static OperationStatus Run(ZstdDecoder decoder, byte[] stream, int capacity, out byte[] content, out ZstdError error)
    {
        byte[] output = new byte[capacity];
        int at = 0;
        int written = 0;
        OperationStatus status;
        while (true)
        {
            status = decoder.Decompress(stream.AsSpan(at), output.AsSpan(written), out int consumed, out int wrote);
            at += consumed;
            written += wrote;
            if (status != OperationStatus.Done || at == stream.Length || (consumed == 0 && wrote == 0))
            {
                break;
            }
        }

        content = output.AsSpan(0, written).ToArray();
        error = decoder.LastError;
        return status == OperationStatus.Done && at == stream.Length ? OperationStatus.Done : status == OperationStatus.Done ? OperationStatus.NeedMoreData : status;
    }

    /// <summary>A skippable frame of <paramref name="size"/> bytes of content.</summary>
    private static byte[] Skippable(uint magic, int size)
    {
        byte[] frame = new byte[8 + size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame, magic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)size);
        new Random(size).NextBytes(frame.AsSpan(8));
        return frame;
    }
}

/// <summary>Streams handed to a <see cref="ZstdDecoder"/> in pieces.</summary>
internal static class Pieces
{
    public static int Whole() => 1 << 24;

    public static int One() => 1;

    /// <summary>Mostly a few KiB, sometimes nothing or a few bytes, sometimes more than a block.</summary>
    public static int Random(Random random) => random.Next(10) switch
    {
        0 => random.Next(0, 4),
        1 => random.Next(4, 300),
        2 => random.Next(128 * 1024, 400 * 1024),
        _ => random.Next(300, 70_000),
    };

    /// <summary>
    /// The stream through the decoder, its bytes handed over in pieces whose sizes
    /// <paramref name="source"/> draws, its content taken into destinations whose sizes
    /// <paramref name="destination"/> draws, until the stream ends.
    /// </summary>
    /// <returns>The last status, the content, and the frames that ended.</returns>
    public static (OperationStatus Status, byte[] Content, int Frames) Decode(ZstdDecoder decoder, ReadOnlySpan<byte> stream, Func<int> source, Func<int> destination)
    {
        var content = new ArrayBufferWriter<byte>();
        int at = 0;
        int frames = 0;
        int stalls = 0;
        while (true)
        {
            int take = Math.Min(source(), stream.Length - at);
            int room = destination();
            Span<byte> target = room == 0 ? [] : content.GetSpan(room)[..room];
            OperationStatus status = decoder.Decompress(stream.Slice(at, take), target, out int consumed, out int written);
            Assert.InRange(consumed, 0, take);
            Assert.InRange(written, 0, room);
            content.Advance(written);
            at += consumed;
            switch (status)
            {
                case OperationStatus.InvalidData:
                    return (status, content.WrittenSpan.ToArray(), frames);
                case OperationStatus.Done:
                    frames++;
                    Assert.False(decoder.InFrame);
                    if (at == stream.Length)
                    {
                        return (status, content.WrittenSpan.ToArray(), frames);
                    }

                    break;
                case OperationStatus.NeedMoreData:
                    Assert.Equal(take, consumed);
                    if (at == stream.Length && take > 0)
                    {
                        return (status, content.WrittenSpan.ToArray(), frames);
                    }

                    break;
                case OperationStatus.DestinationTooSmall:
                    Assert.Equal(room, written);
                    break;
            }

            // A call with bytes to take and room to fill must do something with them.
            stalls = consumed == 0 && written == 0 && take > 0 && room > 0 ? stalls + 1 : 0;
            Assert.True(stalls < 2, $"no progress at byte {at}, {status}");
        }
    }
}
