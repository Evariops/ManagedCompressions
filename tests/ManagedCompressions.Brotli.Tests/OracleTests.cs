using System;
using System.IO.Compression;
using ManagedCompressions.Brotli.Tests.Support;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// The oracle itself. It must be brotli 1.1.1, whose streams the compressor writes and whose checks
/// the decoder makes: another version fails here, rather than in every differential test at once. In
/// CI it must be the reference build, which has the decoder's error codes; and the brotli .NET ships
/// must write the same bytes, since that is the one applications use.
/// </summary>
public sealed class OracleTests
{
    [Fact]
    public void TheEncoderIsBrotli1_1_1()
    {
        if (!NativeBrotli.HasVersions)
        {
            Assert.Skip($"{NativeBrotli.Name} exports no version");
        }

        Assert.Equal(0x1001001u, NativeBrotli.EncoderVersion);
    }

    [Fact]
    public void TheDecoderIsBrotli1_1_1()
    {
        if (!NativeBrotli.HasVersions)
        {
            Assert.Skip($"{NativeBrotli.Name} exports no version");
        }

        Assert.Equal(0x1001001u, NativeBrotli.DecoderVersion);
    }

    [Fact]
    public void CIUsesTheReferenceBuild()
    {
        if (Environment.GetEnvironmentVariable("CI") != "true")
        {
            Assert.Skip("outside CI, the tests may fall back to .NET's library");
        }

        Assert.True(NativeBrotli.IsReference, $"the oracle is {NativeBrotli.Name}: run tools/native-ref/build-brotli.sh");
        Assert.True(NativeBrotli.HasErrorCodes);
    }

    [Theory]
    [InlineData("text", 100_000)]
    [InlineData("json", 70_000)]
    [InlineData("mixed", 300_000)]
    [InlineData("walk64", 50_000)]
    public void DotNetWritesTheOraclesBytes(string kind, int size)
    {
        byte[] data = DataKinds.Generate(kind, size);
        byte[] dotnet = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        for (int quality = 0; quality <= 11; quality++)
        {
            foreach (int window in new[] { 10, 16, 22, 24 })
            {
                Assert.True(BrotliEncoder.TryCompress(data, dotnet, out int written, quality, window));
                byte[] oracle = NativeBrotli.Compress(data, quality, window);
                Assert.True(oracle.AsSpan().SequenceEqual(dotnet.AsSpan(0, written)), $"q{quality} w{window}: .NET's brotli writes other bytes");
            }
        }
    }
}
