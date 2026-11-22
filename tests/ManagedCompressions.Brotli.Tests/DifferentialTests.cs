using System;
using System.Collections.Generic;
using ManagedCompressions.Brotli.Tests.Support;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// Corrupted streams: bits flipped, bytes replaced, streams cut short, and noise. Each must get the
/// verdict brotli gives it, with the same content when both accept it and the same error when both
/// refuse it (<see cref="Verdict"/>).
/// </summary>
public sealed class DifferentialTests
{
    private const int Mutations = 400;

    public static IEnumerable<TheoryDataRow<string, int, int>> Bases()
    {
        foreach (string kind in new[] { "text", "json", "mixed", "repeats", "walk64", "incompressible" })
        {
            foreach (int quality in new[] { 0, 1, 2, 4, 5, 7, 9, 10, 11 })
            {
                yield return new(kind, quality, 3000);
            }
        }

        yield return new("text", 11, 40);
        yield return new("text", 5, 200);
        yield return new("zeros", 5, 3000);
    }

    [Theory]
    [MemberData(nameof(Bases))]
    public void FlippedBits(string kind, int quality, int size)
    {
        byte[] stream = NativeBrotli.Compress(DataKinds.Generate(kind, size), quality, 18);
        var random = new Random(HashCode.Combine(kind.Length, quality, size, 1));
        var decompressor = new BrotliDecompressor();
        byte[] mutated = new byte[stream.Length];
        for (int m = 0; m < Mutations; m++)
        {
            stream.CopyTo(mutated, 0);
            int flips = 1 + random.Next(3);
            for (int f = 0; f < flips; f++)
            {
                // Mostly in the first bytes, where the headers are.
                int at = random.Next(2) == 0 ? random.Next(Math.Min(mutated.Length, 64)) : random.Next(mutated.Length);
                mutated[at] ^= (byte)(1 << random.Next(8));
            }

            Verdict.Check(decompressor, mutated, (size * 2) + 1024, $"{kind} q{quality} mutation {m}");
        }
    }

    [Theory]
    [MemberData(nameof(Bases))]
    public void ReplacedBytes(string kind, int quality, int size)
    {
        byte[] stream = NativeBrotli.Compress(DataKinds.Generate(kind, size), quality, 22);
        var random = new Random(HashCode.Combine(kind.Length, quality, size, 2));
        var decompressor = new BrotliDecompressor();
        byte[] mutated = new byte[stream.Length];
        for (int m = 0; m < Mutations; m++)
        {
            stream.CopyTo(mutated, 0);
            int at = random.Next(mutated.Length);
            mutated[at] = (byte)random.Next(256);
            Verdict.Check(decompressor, mutated, (size * 2) + 1024, $"{kind} q{quality} replacement {m}");
        }
    }

    [Theory]
    [MemberData(nameof(Bases))]
    public void Truncated(string kind, int quality, int size)
    {
        byte[] stream = NativeBrotli.Compress(DataKinds.Generate(kind, size), quality, 22);
        var decompressor = new BrotliDecompressor();
        for (int length = 0; length < stream.Length; length += 1 + (length / 64))
        {
            Verdict.Check(decompressor, stream.AsSpan(0, length), size + 64, $"{kind} q{quality} cut at {length}");
        }
    }

    [Fact]
    public void Noise()
    {
        var random = new Random(3);
        var decompressor = new BrotliDecompressor();
        for (int m = 0; m < 20_000; m++)
        {
            byte[] noise = new byte[1 + random.Next(m < 10_000 ? 16 : 300)];
            random.NextBytes(noise);
            Verdict.Check(decompressor, noise, 4096, $"noise {m}");
        }
    }

    [Fact]
    public void TrailingData()
    {
        byte[] data = DataKinds.Generate("text", 5000);
        byte[] stream = NativeBrotli.Compress(data, 5, 22);
        byte[] padded = new byte[stream.Length + 10];
        stream.CopyTo(padded, 0);
        padded.AsSpan(stream.Length).Fill(0xA5);
        Verdict.Check(new BrotliDecompressor(), padded, data.Length, "trailing data");
    }
}
