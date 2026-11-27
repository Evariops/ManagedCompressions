using System;
using System.Buffers;
using System.Linq;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Streaming;

/// <summary>
/// What a decoder decoded before has no bearing on what it decodes next. A decoder keeps its window and
/// its literals buffer from one frame to the next, and its copies run past the bytes they copy, reading
/// and writing past their end: none of that may reach the content.
/// </summary>
public sealed class EarlierFrameTests
{
    /// <summary>The random frames: fewer in Debug, whose assertions make each slower.</summary>
    private static readonly int Frames = TestBuild.Debug ? 100_000 : 400_000;

    /// <summary>
    /// Random frames of a 1 KiB window, valid or not, whose window restarts, then compressed blocks of
    /// every offset, decoded after earlier frames that leave different bytes, in the literals buffer or
    /// in the window: each decodes alike after each, to the same content or the same refusal, with the
    /// streaming decoder and the one-shot decoder. Before the streaming decoder bounded its history to
    /// the window after a restart (zstd's commit 0c9d2975), 24 of these frames held bytes of the
    /// earlier frame, most of them decoding to the end.
    /// </summary>
    [Fact]
    public void A_frame_decodes_alike_whatever_the_decoder_decoded_before()
    {
        var baseline = new Decoders(Earlier(0x11, 0x11));
        var otherLiterals = new Decoders(Earlier(0xEE, 0x11));
        var otherWindow = new Decoders(Earlier(0x11, 0xEE));
        var random = new Random(20261127);
        int literalsDiffer = 0;
        int windowDiffer = 0;
        int oneShotDiffer = 0;
        int first = -1;
        for (int i = 0; i < Frames; i++)
        {
            byte[] frame = Frame(random);
            baseline.Stream(frame);
            otherLiterals.Stream(frame);
            otherWindow.Stream(frame);
            bool literals = !baseline.SameAs(otherLiterals);
            bool window = !baseline.SameAs(otherWindow);
            baseline.OneShot(frame);
            otherLiterals.OneShot(frame);
            bool oneShot = !baseline.SameAs(otherLiterals);
            literalsDiffer += literals ? 1 : 0;
            windowDiffer += window ? 1 : 0;
            oneShotDiffer += oneShot ? 1 : 0;
            if (first < 0 && (literals || window || oneShot))
            {
                first = i;
            }
        }

        Assert.True(
            literalsDiffer + windowDiffer + oneShotDiffer == 0,
            $"of {Frames} frames, the first at {first}: ZstdDecoder, {literalsDiffer} decode otherwise after other literals, "
            + $"{windowDiffer} after another window; ZstdDecompressor, {oneShotDiffer} after other literals");
    }

    /// <summary>
    /// An earlier frame of a 1 KiB window: 1 KiB of <paramref name="literalFill"/> in the literals
    /// buffer, then <paramref name="windowFill"/> over the whole window, before and after its restart.
    /// </summary>
    private static byte[] Earlier(byte literalFill, byte windowFill)
    {
        byte[] raw = Enumerable.Repeat(windowFill, 1024).ToArray();
        return new FrameBuilder { WindowLog = 10 }
            .Compressed([.. FrameBuilder.RleLiterals(literalFill, 1024), .. FrameBuilder.NoSequences()])
            .Raw(raw).Raw(raw.AsSpan(0, 64)).Raw(raw)
            .Raw(raw).Raw(raw).Raw(raw, last: true)
            .Build();
    }

    /// <summary>
    /// A frame of a 1 KiB window and no declared size: raw blocks of random sizes, enough for the
    /// window to restart most of the time, then compressed blocks of random sequences, then an empty
    /// last block.
    /// </summary>
    private static byte[] Frame(Random random)
    {
        var builder = new FrameBuilder { WindowLog = 10 };
        int raws = random.Next(2, 8);
        for (int i = 0; i < raws; i++)
        {
            byte[] raw = new byte[random.Next(1, 1025)];
            random.NextBytes(raw);
            builder.Raw(raw);
        }

        int blocks = random.Next(1, 5);
        for (int i = 0; i < blocks; i++)
        {
            builder.Compressed(CompressedBlock(random));
        }

        return builder.Raw([], last: true).Build();
    }

    /// <summary>
    /// Up to 12 sequences that share their codes, as <see cref="FrameBuilder.RleSequences"/> writes
    /// them, of offsets from 1 to 8188, after raw or RLE literals, and a few literals after them.
    /// </summary>
    private static byte[] CompressedBlock(Random random)
    {
        int count = random.Next(1, 13);
        int llCode = random.Next(0, 20);
        int mlCode = random.Next(0, 36);
        int ofCode = random.Next(2, 13);
        var sequences = new Seq[count];
        int literalCount = 0;
        for (int i = 0; i < count; i++)
        {
            int ll = FrameBuilder.LiteralLengthBase[llCode] + random.Next(1 << FrameBuilder.LiteralLengthBits[llCode]);
            int ml = FrameBuilder.MatchLengthBase[mlCode] + random.Next(1 << FrameBuilder.MatchLengthBits[mlCode]);
            sequences[i] = new Seq(ll, ml, (uint)((1 << ofCode) + random.Next(1 << ofCode)));
            literalCount += ll;
        }

        byte[] literals = new byte[literalCount + random.Next(0, 16)];
        random.NextBytes(literals);
        byte[] section = random.Next(4) == 0 && literals.Length > 0
            ? FrameBuilder.RleLiterals(literals[0], literals.Length)
            : FrameBuilder.RawLiterals(literals);
        return [.. section, .. FrameBuilder.RleSequences(sequences)];
    }

    /// <summary>A streaming decoder and a one-shot decoder, kept from frame to frame, and what the last decoded.</summary>
    private sealed class Decoders(byte[] earlier)
    {
        private readonly ZstdDecoder _stream = new();
        private readonly ZstdDecompressor _oneShot = new();
        private readonly byte[] _output = new byte[1 << 16];
        private OperationStatus _status;
        private ZstdError _error;
        private int _written;

        /// <summary><paramref name="frame"/> through the streaming decoder, right after the earlier frame.</summary>
        public void Stream(byte[] frame)
        {
            _stream.Reset();
            if (_stream.Decompress(earlier, _output, out int consumed, out _) != OperationStatus.Done || consumed != earlier.Length)
            {
                throw new InvalidOperationException($"the earlier frame does not decode: {_stream.LastError}");
            }

            _status = _stream.Decompress(frame, _output, out _, out _written);
            _error = _stream.LastError;
        }

        /// <summary><paramref name="frame"/> through the one-shot decoder, right after the earlier frame.</summary>
        public void OneShot(byte[] frame)
        {
            if (_oneShot.Decompress(earlier, _output, out _, out _) != OperationStatus.Done)
            {
                throw new InvalidOperationException($"the earlier frame does not decode in one shot: {_oneShot.LastError}");
            }

            _status = _oneShot.Decompress(frame, _output, out _, out _written);
            _error = _oneShot.LastError;
        }

        public bool SameAs(Decoders other) =>
            _status == other._status && _error == other._error
            && _output.AsSpan(0, _written).SequenceEqual(other._output.AsSpan(0, other._written));
    }
}
