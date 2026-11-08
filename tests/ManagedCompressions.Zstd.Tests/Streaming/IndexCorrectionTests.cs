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
/// libzstd's overflow correction: before the indices pass 3500 MiB, every index moves down, in the
/// window and in the tables, by a correction that keeps the window whole, which changes no byte of the
/// frames. Corrected as soon as they may be (<c>ZSTD_WINDOW_OVERFLOW_CORRECT_FREQUENTLY</c>), many times
/// over a few windows, the frames must stay those of an encoder that never corrects them; and a stream
/// past 3500 MiB must stay libzstd's.
/// </summary>
public sealed class IndexCorrectionTests
{
    /// <summary>Every strategy's levels, and windows small enough that the corrections come every few of them.</summary>
    public static TheoryData<int, int> Streams() => new()
    {
        { -5, 10 }, { 1, 12 }, { 2, 13 }, { 3, 14 }, { 5, 15 }, { 6, 16 }, { 7, 12 }, { 9, 17 },
        { 12, 14 }, { 13, 15 }, { 15, 16 }, { 16, 12 }, { 17, 14 }, { 19, 16 }, { 22, 13 },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void Corrected_streams_are_the_same_bytes(int level, int windowLog)
    {
        Cases.CheckAll(
            DataKinds.All,
            kind =>
            {
                var random = new Random(level + windowLog + kind.Length);
                byte[] data = DataKinds.Generate(kind, (24 << windowLog) + random.Next(1, 5000), level + 100);
                var options = new ZstdCompressionOptions { Level = level, WindowLog = windowLog, AppendChecksum = true };
                var plain = new ZstdEncoder(options);
                var corrected = new ZstdEncoder(options) { CorrectOverflowFrequently = true };

                // Pledged: libzstd fits the chains to the window only for a known size, and the
                // corrections come every cycle of them.
                plain.SetSourceLength(data.Length);
                corrected.SetSourceLength(data.Length);
                IEnumerable<StreamCall> calls = EncoderScript.RandomCalls(random, data.Length).Select(c => c.Op == StreamOp.End ? c with { Op = StreamOp.Flush } : c);
                byte[] frame = EncoderScript.Run(plain, new Managed(corrected), data, calls, $"{kind} L{level} window 2^{windowLog}");
                Assert.True(corrected.OverflowCorrections >= 2, $"{kind} L{level}: {corrected.OverflowCorrections} corrections");
                EncoderScript.AssertDecodes(frame, data, $"{kind} L{level}");
            });
    }

    /// <summary>The long-distance matcher corrects its own window, a byte its cycle, chunk by chunk.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(19)]
    [InlineData(22)]
    public void Corrected_long_distance_matching_is_the_same_bytes(int level)
    {
        var random = new Random(level);
        byte[] data = LongDistanceCompressionTests.FarRepeats.Generate("far-text", 6_000_000);
        var options = new ZstdCompressionOptions { Level = level, WindowLog = 17, LongDistanceMatching = true };
        var plain = new ZstdEncoder(options);
        var corrected = new ZstdEncoder(options) { CorrectOverflowFrequently = true };
        plain.SetSourceLength(data.Length);
        corrected.SetSourceLength(data.Length);
        IEnumerable<StreamCall> calls = EncoderScript.RandomCalls(random, data.Length).Select(c => c.Op == StreamOp.End ? c with { Op = StreamOp.Flush } : c);
        byte[] frame = EncoderScript.Run(plain, new Managed(corrected), data, calls, $"L{level}");
        Assert.True(corrected.OverflowCorrections >= 2, $"L{level}: {corrected.OverflowCorrections} corrections");
        EncoderScript.AssertDecodes(frame, data, $"L{level}");
    }

    /// <summary>
    /// With a dictionary, corrected only once the window has passed it, whose loss would change the
    /// frame: frames of every size, one after the other, attached, copied and loaded.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(19)]
    public void Corrected_dictionary_frames_are_the_same_bytes(int level)
    {
        (byte[] dictionary, _) = Dictionaries.Get("json", "trained");
        var options = new ZstdCompressionOptions { Level = level, Dictionary = dictionary, WindowLog = 14 };
        var plain = new ZstdEncoder(options);
        var corrected = new ZstdEncoder(options) { CorrectOverflowFrequently = true };
        foreach (int size in (int[])[1000, 30_000, 200_000, 600_000, 2_000_000])
        {
            var random = new Random(size + level);
            byte[] data = DataKinds.Generate("json", size, level);
            byte[] frame = EncoderScript.Run(plain, new Managed(corrected), data, EncoderScript.RandomCalls(random, data.Length), $"L{level}, {size} bytes");
            EncoderScript.AssertDecodes(frame, data, $"L{level}, {size} bytes", dictionary);
        }

        Assert.True(corrected.OverflowCorrections > 0, $"L{level}: no correction");
    }

    /// <summary>One-shot frames correct too, block by block, frame after frame, with and without a dictionary.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(19)]
    public void Corrected_one_shot_frames_are_the_same_bytes(int level)
    {
        (byte[] dictionary, _) = Dictionaries.Get("json", "trained");
        foreach (bool withDictionary in (bool[])[false, true])
        {
            var options = new ZstdCompressionOptions { Level = level, WindowLog = 15, Dictionary = withDictionary ? dictionary : default };
            var plain = new ZstdCompressor(options);
            var corrected = new ZstdCompressor(options) { CorrectOverflowFrequently = true };
            foreach (int size in (int[])[50_000, 1_000_000, 300_000, 2_500_000])
            {
                byte[] data = DataKinds.Generate("mixed", size, size + level);
                Corpus.AssertSameBytes(Compress(plain, data), Compress(corrected, data), $"L{level}, {size} bytes, dictionary {withDictionary}");
            }

            Assert.True(corrected.OverflowCorrections > 0, $"L{level}, dictionary {withDictionary}: no correction");
        }
    }

    /// <summary>
    /// A stream of 3.7 GB, a megabyte at a time, through the encoder and libzstd: past 3500 MiB, both
    /// correct their indices, and the frame stays libzstd's.
    /// </summary>
    [Theory]
    [InlineData(1)]
    public void A_stream_past_3500_MiB_stays_libzstds(int level)
    {
        const long Total = 3700L << 20;
        byte[] chunk = DataKinds.Generate("mixed", 1 << 20, level);
        byte[] ours = new byte[2 << 20];
        byte[] theirs = new byte[2 << 20];
        var encoder = new ZstdEncoder(level);
        using var native = new NativeZstdEncoder(new NativeZstdOptions { Quality = level });
        for (long at = 0; at < Total; at += chunk.Length)
        {
            // A byte changed each megabyte, so that no two are alike.
            chunk[(int)((at >> 20) * 7919 % chunk.Length)] ^= 0x5A;
            bool final = at + chunk.Length >= Total;
            OperationStatus ourStatus = encoder.Compress(chunk, ours, out int ourConsumed, out int ourWritten, final);
            OperationStatus theirStatus = native.Compress(chunk, theirs, out int theirConsumed, out int theirWritten, final);
            Assert.True(
                ourStatus == theirStatus && ourConsumed == theirConsumed && ourWritten == theirWritten,
                $"at {at >> 20} MiB: {ourStatus}, {ourConsumed}, {ourWritten}; libzstd: {theirStatus}, {theirConsumed}, {theirWritten}");
            Assert.True(ours.AsSpan(0, ourWritten).SequenceEqual(theirs.AsSpan(0, theirWritten)), $"at {at >> 20} MiB: not libzstd's bytes");
        }

        Assert.Equal(1u, encoder.OverflowCorrections);
    }

    private static byte[] Compress(ZstdCompressor compressor, byte[] data)
    {
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        return output.AsSpan(0, written).ToArray();
    }

    /// <summary>A <see cref="ZstdEncoder"/> as the other side of <see cref="EncoderScript.Run"/>.</summary>
    private sealed class Managed(ZstdEncoder encoder) : IStreamEncoder
    {
        public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock) =>
            encoder.Compress(source, destination, out bytesConsumed, out bytesWritten, isFinalBlock);

        public OperationStatus Flush(Span<byte> destination, out int bytesWritten) => encoder.Flush(destination, out bytesWritten);
    }
}
