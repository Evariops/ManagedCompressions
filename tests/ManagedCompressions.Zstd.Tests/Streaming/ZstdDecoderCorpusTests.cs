using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ManagedCompressions.Zstd.Tests.Differential;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Streaming;

/// <summary>
/// <see cref="ZstdDecoder"/> over zstd's golden files and decodecorpus frames (tools/native-ref/testdata.sh),
/// handed over whole, in random pieces and one byte at a time, the pieces and the destinations against
/// guard pages: what the one-shot decoder decodes, it decodes to the same bytes; what the one-shot
/// decoder refuses, it refuses for the same reason.
/// </summary>
public sealed class ZstdDecoderCorpusTests
{
    public static TheoryData<string> Valid() => GoldenTests.Valid();

    public static TheoryData<string> Invalid() => GoldenTests.Invalid();

    public static TheoryData<string> Sets() => DecodeCorpusTests.Sets();

    [Theory]
    [MemberData(nameof(Valid))]
    public void Golden_frames_decode_in_pieces(string file)
    {
        TestData.Require("golden-decompression");
        byte[] frame = TestData.Read(Path.Combine("golden-decompression", file));
        byte[] output = new byte[1 << 20];
        Assert.Equal(OperationStatus.Done, new ZstdDecompressor().Decompress(frame, output, out _, out int size));
        byte[] content = output.AsSpan(0, size).ToArray();

        foreach ((Func<int> source, Func<int> destination, Random? guards, string how) in Ways(seed: content.Length))
        {
            var decoder = new ZstdDecoder();
            (OperationStatus status, byte[] decoded, _) = Pieces.Decode(decoder, frame, source, destination, guards);
            Assert.True(status == OperationStatus.Done, $"{file} {how}: {status} ({decoder.LastError})");
            Corpus.AssertSameBytes(content, decoded, $"{file} {how}");
        }
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Golden_errors_are_refused_in_pieces_for_the_same_reason(string file)
    {
        TestData.Require("golden-decompression-errors");
        byte[] frame = TestData.Read(Path.Combine("golden-decompression-errors", file));
        Assert.False(NativeZstd.DecompressStream(frame, 1 << 20, null, 27, out _), "libzstd's streaming decoder refuses it");
        var oneShot = new ZstdDecompressor();
        Assert.Equal(OperationStatus.InvalidData, oneShot.Decompress(frame, new byte[1 << 20], out _, out _));

        foreach ((Func<int> source, Func<int> destination, Random? guards, string how) in Ways(seed: frame.Length))
        {
            var decoder = new ZstdDecoder();
            (OperationStatus status, _, _) = Pieces.Decode(decoder, frame, source, destination, guards);
            Assert.True(status == OperationStatus.InvalidData, $"{file} {how}: {status}");
            Assert.Equal(oneShot.LastError, decoder.LastError);
        }
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void Decodecorpus_frames_decode_in_pieces(string set)
    {
        TestData.Require("decodecorpus");
        string directory = Path.Combine("decodecorpus", set);
        byte[]? dictionary = set == "dict" ? TestData.Read(Path.Combine(directory, "dictionary")) : null;
        string[] manifest = File.ReadAllLines(TestData.PathOf(Path.Combine(directory, "manifest.txt")));
        Cases.CheckAll(
            Enumerable.Range(0, manifest.Length),
            entry =>
            {
                // "name size sha256"
                string[] parts = manifest[entry].Split(' ');
                byte[] frame = TestData.Read(Path.Combine(directory, parts[0]));
                foreach ((Func<int> source, Func<int> destination, Random? guards, string how) in Ways(seed: entry))
                {
                    ZstdDecoder decoder = dictionary is null ? new ZstdDecoder() : new ZstdDecoder(dictionary);
                    (OperationStatus status, byte[] decoded, int frames) = Pieces.Decode(decoder, frame, source, destination, guards);
                    Assert.True(status == OperationStatus.Done, $"{how}: {status} ({decoder.LastError})");
                    Assert.Equal(1, frames);
                    Assert.Equal(int.Parse(parts[1], CultureInfo.InvariantCulture), decoded.Length);
                    Assert.Equal(parts[2], Convert.ToHexStringLower(SHA256.HashData(decoded)));
                }
            },
            entry => $"{set}/{manifest[entry].Split(' ')[0]}");
    }

    /// <summary>
    /// The ways a stream is handed over: whole, into one destination; then in random pieces, and one
    /// byte at a time, each piece and each destination against a guard page.
    /// </summary>
    private static (Func<int> Source, Func<int> Destination, Random? Guards, string How)[] Ways(int seed)
    {
        var random = new Random(seed);
        return
        [
            (Pieces.Whole, Pieces.Whole, null, "whole"),
            (() => Pieces.Random(random), () => Pieces.Random(random), random, "in random pieces"),
            (Pieces.One, Pieces.One, random, "one byte at a time"),
        ];
    }
}
