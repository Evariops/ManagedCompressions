using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using ManagedCompressions.Brotli.Tests.Support;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// brotli's own test streams (tests/testdata at v1.1.1): each <c>.compressed</c> file, and the
/// <c>.compressed.NN</c> ones written by other encoders and settings, decodes to the file it is named
/// after, and gets brotli's verdict when cut short or corrupted.
/// </summary>
public sealed class GoldenTests
{
    public static IEnumerable<TheoryDataRow<string>> Streams() =>
        TestData.Files("*.compressed*").Select(name => new TheoryDataRow<string>(name));

    [Theory]
    [MemberData(nameof(Streams))]
    public void DecodesToItsOriginal(string name)
    {
        TestData.Require(name);
        byte[] stream = TestData.Read(name);
        byte[] original = TestData.Read(name.Substring(0, name.IndexOf(".compressed", StringComparison.Ordinal)));
        var decompressor = new BrotliDecompressor();
        byte[] output = new byte[original.Length + 100];
        OperationStatus status = decompressor.Decompress(stream, output, out int consumed, out int written);
        Assert.Equal(BrotliError.None, decompressor.LastError);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(stream.Length, consumed);
        Assert.Equal(original.Length, written);
        Assert.True(original.AsSpan().SequenceEqual(output.AsSpan(0, written)), "the content differs");

        byte[] exact = new byte[original.Length];
        Assert.Equal(OperationStatus.Done, decompressor.Decompress(stream, exact, out _, out _));
        Assert.True(original.AsSpan().SequenceEqual(exact), "the content differs in an exact destination");
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void CutAndCorruptedAsBrotli(string name)
    {
        TestData.Require(name);
        byte[] stream = TestData.Read(name);
        int capacity = TestData.Read(name.Substring(0, name.IndexOf(".compressed", StringComparison.Ordinal))).Length + 4096;
        var decompressor = new BrotliDecompressor();
        for (int length = 0; length < stream.Length; length += 1 + (length / 32))
        {
            Verdict.Check(decompressor, stream.AsSpan(0, length), capacity, $"{name} cut at {length}");
        }

        var random = new Random(stream.Length);
        byte[] mutated = new byte[stream.Length];
        for (int m = 0; m < 200; m++)
        {
            stream.CopyTo(mutated, 0);
            int at = random.Next(2) == 0 ? random.Next(Math.Min(mutated.Length, 48)) : random.Next(mutated.Length);
            mutated[at] ^= (byte)(1 << random.Next(8));
            Verdict.Check(decompressor, mutated, capacity, $"{name} mutation {m}");
        }
    }
}
