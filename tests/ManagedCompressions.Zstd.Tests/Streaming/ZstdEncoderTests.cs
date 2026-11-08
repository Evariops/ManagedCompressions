using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using ManagedCompressions.Zstd.Tests.Compression;
using ManagedCompressions.Zstd.Tests.Differential;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Streaming;

/// <summary>
/// <see cref="ZstdEncoder"/> against libzstd's <c>ZSTD_compressStream2</c> (<see cref="NativeZstdEncoder"/>),
/// both driven by the same calls: each call must take and write the same bytes, and end the same way.
/// </summary>
public sealed class ZstdEncoderTests
{
    /// <summary>Levels across every strategy, but level 22, whose streams of unknown size need the long-distance matcher.</summary>
    private static readonly int[] Levels = [-5, -1, 1, 2, 3, 4, 5, 6, 7, 9, 12, 13, 15, 16, 17, 19, 21];

    public static TheoryData<string> Kinds() => [.. DataKinds.All];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_final_first_call_writes_the_one_shot_frame(string kind)
    {
        int variant = 0;
        Cases.CheckAll(
            from size in (int[])[0, 1, 1000, (128 << 10) - 1, 128 << 10, (128 << 10) + 1, 300_000]
            from level in (int[])[-5, 1, 3, 9, 19, 22]
            select (size, level),
            c =>
            {
                byte[] data = DataKinds.Generate(kind, c.size, ++variant);
                var options = new ZstdCompressionOptions { Level = c.level, AppendChecksum = variant % 2 == 0 };
                byte[] oneShot = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
                Assert.Equal(OperationStatus.Done, new ZstdCompressor(options).Compress(data, oneShot, out _, out int length));

                byte[] streamed = new byte[ZstdEncoder.GetMaxCompressedLength(data.Length)];
                var encoder = new ZstdEncoder(options);
                Assert.Equal(OperationStatus.Done, encoder.Compress(data, streamed, out int consumed, out int written, isFinalBlock: true));
                Assert.Equal(data.Length, consumed);
                Corpus.AssertSameBytes(oneShot.AsSpan(0, length), streamed.AsSpan(0, written), $"{kind}/{c.size}/L{c.level}");
            },
            c => $"{c.size} bytes, level {c.level}");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_sequence_of_calls_writes_libzstds_bytes(string kind)
    {
        int variant = 0;
        Cases.CheckAll(
            Levels,
            level =>
            {
                for (int round = 0; round < 3; round++)
                {
                    variant++;
                    var random = new Random(variant);
                    byte[] data = DataKinds.Generate(kind, random.Next(0, 600_000), variant);
                    bool checksum = random.Next(2) == 0;
                    var options = new ZstdCompressionOptions { Level = level, AppendChecksum = checksum };
                    using var native = new NativeZstdEncoder(new NativeZstdOptions { Quality = level, AppendChecksum = checksum });
                    byte[] frames = EncoderScript.Run(new ZstdEncoder(options), native, data, EncoderScript.RandomCalls(random, data.Length), $"{kind} L{level} round {round}");
                    EncoderScript.AssertDecodes(frames, data, $"{kind} L{level} round {round}");
                }
            },
            level => $"level {level}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(19)]
    [InlineData(22)]
    public void A_pledged_size_is_declared_and_fits_the_parameters(int level)
    {
        foreach (int size in (int[])[0, 1, 999, 1000, 1001, 128 << 10, 200_000])
        {
            var random = new Random(size);
            byte[] data = DataKinds.Generate("mixed", size, level);
            var encoder = new ZstdEncoder(level);
            using var native = new NativeZstdEncoder(new NativeZstdOptions { Quality = level });
            encoder.SetSourceLength(size);
            native.SetSourceLength(size);

            // Continued, never ended early: the frame is the pledged content.
            IEnumerable<StreamCall> calls = EncoderScript.RandomCalls(random, size).Select(c => c.Op == StreamOp.End ? c with { Op = StreamOp.Continue } : c);
            byte[] frame = EncoderScript.Run(encoder, native, data, calls, $"L{level}, {size} bytes pledged");
            Assert.True(ZstdDecompressor.TryGetFrameContentSize(frame, out ulong declared), "the header declares the size");
            Assert.Equal((ulong)size, declared);
            EncoderScript.AssertDecodes(frame, data, $"L{level}, {size} bytes pledged");
        }
    }

    /// <summary>
    /// A block that is the whole pledged content waits for the final call, a byte further than the
    /// block, so that it is the last block, not a full block followed by an empty one.
    /// </summary>
    [Fact]
    public void A_pledged_content_of_one_block_ends_in_one_block()
    {
        byte[] data = DataKinds.Generate("text", 1000, 1);
        var encoder = new ZstdEncoder(3);
        using var native = new NativeZstdEncoder(new NativeZstdOptions { Quality = 3 });
        encoder.SetSourceLength(1000);
        native.SetSourceLength(1000);
        byte[] frame = EncoderScript.Run(encoder, native, data, [new(StreamOp.Continue, 1000, 4096)], "one block");
        EncoderScript.AssertDecodes(frame, data, "one block");
    }

    [Fact]
    public void A_content_that_differs_from_the_pledge_is_refused_until_Reset()
    {
        byte[] data = DataKinds.Generate("json", 300, 2);
        byte[] output = new byte[4096];
        var encoder = new ZstdEncoder(3);
        using var native = new NativeZstdEncoder(new NativeZstdOptions { Quality = 3 });

        // More than pledged.
        encoder.SetSourceLength(100);
        native.SetSourceLength(100);
        Assert.Equal(OperationStatus.InvalidData, native.Compress(data.AsSpan(0, 150), output, out _, out _, isFinalBlock: false));
        Assert.Equal(OperationStatus.InvalidData, encoder.Compress(data.AsSpan(0, 150), output, out int consumed, out int written, isFinalBlock: false));
        Assert.Equal(0, consumed);
        Assert.Equal(0, written);
        Assert.Equal(OperationStatus.InvalidData, encoder.Flush(output, out _));
        Assert.Equal(OperationStatus.InvalidData, encoder.Compress(data, output, out _, out _, isFinalBlock: true));

        // Less than pledged, ended.
        encoder.Reset();
        native.Reset();
        encoder.SetSourceLength(100);
        native.SetSourceLength(100);
        byte[] partial = EncoderScript.Run(encoder, native, data.AsSpan(0, 50).ToArray(), [new(StreamOp.Continue, 50, 4096), new(StreamOp.Flush, 0, 4096)], "less than pledged, flushed");
        Assert.Equal(OperationStatus.InvalidData, encoder.Compress(default, output, out _, out _, isFinalBlock: true));

        // A first call that is final pledges its own source, whatever was pledged before.
        encoder.Reset();
        native.Reset();
        encoder.SetSourceLength(100);
        native.SetSourceLength(100);
        byte[] frame = EncoderScript.Run(encoder, native, data.AsSpan(0, 50).ToArray(), [], "final first call");
        EncoderScript.AssertDecodes(frame, data.AsSpan(0, 50).ToArray(), "final first call");
        Assert.NotEmpty(partial);
    }

    [Fact]
    public void Reset_abandons_the_frame_and_the_next_one_begins_afresh()
    {
        byte[] data = DataKinds.Generate("text", 400_000, 7);
        byte[] scratch = new byte[1 << 20];
        var encoder = new ZstdEncoder(3);
        using var native = new NativeZstdEncoder(new NativeZstdOptions { Quality = 3 });

        // A frame begun, a block written and half another gathered, then abandoned.
        OperationStatus theirs = native.Compress(data.AsSpan(0, 200_000), scratch, out _, out int expected, isFinalBlock: false);
        Assert.Equal(theirs, encoder.Compress(data.AsSpan(0, 200_000), scratch, out _, out int written, isFinalBlock: false));
        Assert.Equal(expected, written);
        encoder.Reset();
        native.Reset();

        byte[] frames = EncoderScript.Run(encoder, native, data, EncoderScript.RandomCalls(new Random(3), data.Length), "after Reset");
        EncoderScript.AssertDecodes(frames, data, "after Reset");
    }

    [Theory]
    [InlineData(1, 10, true)]
    [InlineData(3, 15, false)]
    [InlineData(6, 20, true)]
    [InlineData(17, 13, false)]
    public void The_options_apply_to_streamed_frames(int level, int windowLog, bool pledge)
    {
        var random = new Random(level + windowLog);
        byte[] data = DataKinds.Generate("mixed", 250_000, level);
        var options = new ZstdCompressionOptions { Level = level, WindowLog = windowLog, AppendChecksum = true, WriteContentSize = false };
        var native = new NativeZstdOptions { Quality = level, WindowLog = windowLog, AppendChecksum = true, ContentSizeFlag = false };
        var encoder = new ZstdEncoder(options);
        using var theirs = new NativeZstdEncoder(native);
        if (pledge)
        {
            encoder.SetSourceLength(data.Length);
            theirs.SetSourceLength(data.Length);
        }

        IEnumerable<StreamCall> calls = EncoderScript.RandomCalls(random, data.Length).Select(c => c.Op == StreamOp.End ? c with { Op = StreamOp.Flush } : c);
        byte[] frame = EncoderScript.Run(encoder, theirs, data, calls, $"L{level}, window 2^{windowLog}, pledged {pledge}");
        Assert.False(ZstdDecompressor.TryGetFrameContentSize(frame, out _), "no size declared");
        EncoderScript.AssertDecodes(frame, data, $"L{level}, window 2^{windowLog}");
    }

    [Fact]
    public void The_source_length_is_set_before_a_frame_begins()
    {
        var encoder = new ZstdEncoder();
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.SetSourceLength(-1));
        Assert.Equal(OperationStatus.Done, encoder.Compress(new byte[10], new byte[100], out _, out _, isFinalBlock: false));
        Assert.Throws<InvalidOperationException>(() => encoder.SetSourceLength(10));

        // libzstd agrees.
        using var native = new NativeZstdEncoder(new NativeZstdOptions { Quality = 3 });
        Assert.Equal(OperationStatus.Done, native.Compress(new byte[10], new byte[100], out _, out _, isFinalBlock: false));
        Assert.Throws<InvalidOperationException>(() => native.SetSourceLength(10));

        // After Reset, or after the frame ends, it may be set again.
        encoder.Reset();
        encoder.SetSourceLength(10);
        Assert.Equal(OperationStatus.Done, encoder.Compress(new byte[10], new byte[100], out _, out _, isFinalBlock: true));
        encoder.SetSourceLength(10);
    }

    /// <summary>
    /// Streams longer than the window and a block, in small windows: the input buffer wraps, and each
    /// time the window's bytes before the wrap become an extDict below the new ones, which overwrite
    /// them as they arrive.
    /// </summary>
    [Theory]
    [InlineData(1, 10)]
    [InlineData(3, 12)]
    [InlineData(5, 14)]
    [InlineData(9, 15)]
    [InlineData(13, 16)]
    [InlineData(16, 17)]
    [InlineData(19, 18)]
    public void The_input_buffer_wraps_and_the_window_follows(int level, int windowLog)
    {
        int variant = 0;
        Cases.CheckAll(
            DataKinds.All,
            kind =>
            {
                var random = new Random(++variant + (level << 8));
                int window = 1 << windowLog;
                byte[] data = DataKinds.Generate(kind, (6 * window) + (3 * Math.Min(window, 128 << 10)) + random.Next(1, 5000), variant);
                var options = new ZstdCompressionOptions { Level = level, WindowLog = windowLog, AppendChecksum = true };
                using var native = new NativeZstdEncoder(new NativeZstdOptions { Quality = level, WindowLog = windowLog, AppendChecksum = true });
                IEnumerable<StreamCall> calls = EncoderScript.RandomCalls(random, data.Length).Select(c => c.Op == StreamOp.End ? c with { Op = StreamOp.Flush } : c);
                byte[] frame = EncoderScript.Run(new ZstdEncoder(options), native, data, calls, $"{kind} L{level} window 2^{windowLog}");
                EncoderScript.AssertDecodes(frame, data, $"{kind} L{level} window 2^{windowLog}");
            });
    }

    /// <summary>
    /// The long-distance matcher in streamed frames, asked for at the optimal parsers' levels, in a
    /// window of 1 MiB the input buffer wraps in, on content that repeats itself far apart: the
    /// matcher's own window wraps with it.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(19)]
    [InlineData(22)]
    public void The_long_distance_matcher_streams_like_libzstd(int level)
    {
        Cases.CheckAll(
            LongDistanceCompressionTests.FarRepeats.Kinds,
            kind =>
            {
                var random = new Random(level + kind.Length);
                byte[] data = LongDistanceCompressionTests.FarRepeats.Generate(kind, 3_000_000);
                var options = new ZstdCompressionOptions { Level = level, WindowLog = 20, LongDistanceMatching = true, AppendChecksum = true };
                using var native = new NativeZstdEncoder(new NativeZstdOptions { Quality = level, WindowLog = 20, EnableLongDistanceMatching = true, AppendChecksum = true });
                IEnumerable<StreamCall> calls = EncoderScript.RandomCalls(random, data.Length).Select(c => c.Op == StreamOp.End ? c with { Op = StreamOp.Flush } : c);
                byte[] frame = EncoderScript.Run(new ZstdEncoder(options), native, data, calls, $"{kind} L{level}");
                EncoderScript.AssertDecodes(frame, data, $"{kind} L{level}");
            });
    }

    /// <summary>
    /// Level 22 without a pledged size: a window of 2^27 bytes, for which libzstd turns the matcher on
    /// by itself. The heaviest of these tests, for the tables of that window, on both sides.
    /// </summary>
    [Fact]
    public void Level_22_of_unknown_size_turns_the_matcher_on()
    {
        var random = new Random(22);
        byte[] data = LongDistanceCompressionTests.FarRepeats.Generate("far-json", 2_500_000);
        using var native = new NativeZstdEncoder(new NativeZstdOptions { Quality = 22 });
        IEnumerable<StreamCall> calls = EncoderScript.RandomCalls(random, data.Length).Select(c => c.Op == StreamOp.End ? c with { Op = StreamOp.Flush } : c);
        byte[] frame = EncoderScript.Run(new ZstdEncoder(22), native, data, calls, "L22");
        EncoderScript.AssertDecodes(frame, data, "L22");
    }

    [Fact]
    public void What_is_not_streamed_yet_is_refused()
    {
        Assert.Throws<NotSupportedException>(() => new ZstdEncoder(new ZstdCompressionOptions { Dictionary = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } }));

        // As in one-shot frames, the matcher asked for below the optimal parsers.
        var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = 3, LongDistanceMatching = true });
        Assert.Throws<NotSupportedException>(() => encoder.Compress(new byte[10], new byte[100], out _, out _, isFinalBlock: false));
    }

#if NET11_0_OR_GREATER
    /// <summary>
    /// System.IO.Compression's <c>ZstandardEncoder</c>, the shape <see cref="ZstdEncoder"/> takes: over
    /// the same calls, the same statuses, and the bytes of the platform's libzstd.
    /// </summary>
    [Theory]
    [InlineData(-5)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(19)]
    public void Every_sequence_of_calls_writes_the_platforms_bytes(int level)
    {
        foreach (string kind in (string[])["text", "json", "walk64"])
        {
            var random = new Random((level * 31) + kind.Length);
            byte[] data = DataKinds.Generate(kind, random.Next(0, 600_000), level + 10);
            using var platform = new PlatformEncoder(level);
            byte[] frames = EncoderScript.Run(new ZstdEncoder(level), platform, data, EncoderScript.RandomCalls(random, data.Length), $"{kind} L{level}");
            EncoderScript.AssertDecodes(frames, data, $"{kind} L{level}");
        }
    }

    private sealed class PlatformEncoder(int level) : IStreamEncoder, IDisposable
    {
        private readonly System.IO.Compression.ZstandardEncoder _encoder = new(level);

        public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock) =>
            _encoder.Compress(source, destination, out bytesConsumed, out bytesWritten, isFinalBlock);

        public OperationStatus Flush(Span<byte> destination, out int bytesWritten) => _encoder.Flush(destination, out bytesWritten);

        public void Dispose() => _encoder.Dispose();
    }
#endif

    [Fact]
    public void The_bound_is_libzstds()
    {
        Assert.Equal(64, ZstdEncoder.GetMaxCompressedLength(0));
        Assert.Equal(1_052_672, ZstdEncoder.GetMaxCompressedLength(1 << 20));
        Assert.Equal(ZstdCompressor.GetMaxCompressedLength(300_000), ZstdEncoder.GetMaxCompressedLength(300_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstdEncoder.GetMaxCompressedLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstdEncoder.GetMaxCompressedLength(long.MaxValue));
    }
}

internal enum StreamOp
{
    Continue,
    Flush,
    End,
}

/// <summary>A call: what it asks for, how many of the data's next bytes it offers, and the room it gives.</summary>
internal readonly record struct StreamCall(StreamOp Op, int Take, int Room);

/// <summary>An encoder <see cref="EncoderScript"/> compares with <see cref="ZstdEncoder"/>: libzstd's, or the platform's.</summary>
internal interface IStreamEncoder
{
    OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock);

    OperationStatus Flush(Span<byte> destination, out int bytesWritten);
}

/// <summary>Calls run alike through <see cref="ZstdEncoder"/> and libzstd's streaming compressor.</summary>
internal static class EncoderScript
{
    /// <summary>
    /// The calls through both encoders, each offered the data's bytes from where the last one stopped:
    /// a final call the destination cut short is repeated with what it did not take, until it ends the
    /// frame; after the calls, a final one ends the last frame. Each call must take and write the same
    /// bytes as libzstd's, and end the same way.
    /// </summary>
    /// <returns>The frames written.</returns>
    public static byte[] Run(ZstdEncoder ours, IStreamEncoder theirs, byte[] data, IEnumerable<StreamCall> calls, string name)
    {
        var output = new ArrayBufferWriter<byte>();
        var expected = new ArrayBufferWriter<byte>();
        int at = 0;
        int step = 0;
        foreach (StreamCall call in calls.Append(new StreamCall(StreamOp.End, int.MaxValue, 1 << 20)))
        {
            int take = call.Op == StreamOp.Flush ? 0 : Math.Min(call.Take, data.Length - at);
            int room = call.Room;
            while (true)
            {
                ReadOnlySpan<byte> source = data.AsSpan(at, take);
                Span<byte> mine = output.GetSpan(room)[..room];
                Span<byte> native = expected.GetSpan(room)[..room];
                int ourConsumed = 0;
                int theirConsumed = 0;
                int ourWritten;
                int theirWritten;
                OperationStatus ourStatus;
                OperationStatus theirStatus;
                if (call.Op == StreamOp.Flush)
                {
                    ourStatus = ours.Flush(mine, out ourWritten);
                    theirStatus = theirs.Flush(native, out theirWritten);
                }
                else
                {
                    ourStatus = ours.Compress(source, mine, out ourConsumed, out ourWritten, call.Op == StreamOp.End);
                    theirStatus = theirs.Compress(source, native, out theirConsumed, out theirWritten, call.Op == StreamOp.End);
                }

                string what = $"{name}, call {step} ({call.Op}, {take} bytes at {at}, room {room})";
                Assert.True(
                    ourStatus == theirStatus && ourConsumed == theirConsumed && ourWritten == theirWritten,
                    $"{what}: {ourStatus}, {ourConsumed} taken, {ourWritten} written; libzstd: {theirStatus}, {theirConsumed} taken, {theirWritten} written");
                Corpus.AssertSameBytes(native[..theirWritten], mine[..ourWritten], what);
                output.Advance(ourWritten);
                expected.Advance(theirWritten);
                at += ourConsumed;
                take -= ourConsumed;
                step++;
                if (call.Op != StreamOp.End || ourStatus != OperationStatus.DestinationTooSmall)
                {
                    break;
                }

                room = Math.Max(room, 64);
            }
        }

        Assert.Equal(data.Length, at);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Calls of every kind until the data is offered whole: mostly continued, some flushed, a few
    /// final, which end a frame mid-way; pieces of every size around a block's; destinations from
    /// nothing to a megabyte.
    /// </summary>
    public static IEnumerable<StreamCall> RandomCalls(Random random, int length)
    {
        long offered = 0;
        while (offered < length)
        {
            int take = random.Next(10) switch
            {
                0 => 0,
                1 => random.Next(1, 16),
                2 => 128 << 10,
                3 => (128 << 10) + random.Next(-2, 3),
                4 => random.Next(200_000, 400_000),
                _ => random.Next(16, 70_000),
            };
            int room = random.Next(10) switch
            {
                0 => 0,
                1 => random.Next(1, 64),
                2 => random.Next(64, 4096),
                3 => 1 << 20,
                _ => random.Next(4096, 200_000),
            };
            StreamOp op = random.Next(16) switch
            {
                0 or 1 => StreamOp.Flush,
                2 => StreamOp.End,
                _ => StreamOp.Continue,
            };
            yield return new StreamCall(op, take, room);
            offered += take;
        }
    }

    /// <summary>The frames decode, one after the other, to <paramref name="data"/>.</summary>
    public static void AssertDecodes(byte[] frames, byte[] data, string name)
    {
        (OperationStatus status, byte[] content, _) = Pieces.Decode(new ZstdDecoder(), frames, Pieces.Whole, Pieces.Whole);
        Assert.True(status == OperationStatus.Done, $"{name}: {status}");
        Corpus.AssertSameBytes(data, content, name);
    }
}
