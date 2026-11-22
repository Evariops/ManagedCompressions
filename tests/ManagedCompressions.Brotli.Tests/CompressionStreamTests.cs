using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using ManagedCompressions.Brotli.Tests.Support;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// <see cref="BrotliCompressionStream"/> writes the bytes .NET's <see cref="BrotliStream"/> writes for the
/// same writes and flushes, and reads back what it wrote, in pieces of any size.
/// </summary>
public sealed class CompressionStreamTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(9, 6)]
    [InlineData(11, 7)]
    public void WritesWhatBrotliStreamWrites(int quality, int seed)
    {
        byte[] data = DataKinds.Generate("mixed", quality >= 10 ? 80_000 : 600_000);
        var random = new Random(seed);
        var expected = new MemoryStream();
        var actual = new MemoryStream();
        using (var native = new BrotliStream(expected, new BrotliCompressionOptions { Quality = quality }, leaveOpen: true))
        using (var managed = new BrotliCompressionStream(actual, quality, leaveOpen: true))
        {
            int position = 0;
            while (position < data.Length)
            {
                int piece = Math.Min(data.Length - position, random.Next(4) == 0 ? random.Next(0, 200_000) : random.Next(0, 3000));
                native.Write(data, position, piece);
                managed.Write(data, position, piece);
                position += piece;
                if (random.Next(20) == 0)
                {
                    native.Flush();
                    managed.Flush();
                }
            }
        }

        Assert.Equal(expected.ToArray(), actual.ToArray());
        Assert.Equal(data, ReadAll(new BrotliCompressionStream(new MemoryStream(actual.ToArray()), CompressionMode.Decompress), random));
    }

    [Fact]
    public async Task AsyncRoundTrip()
    {
        byte[] data = DataKinds.Generate("text", 300_000);
        var compressed = new MemoryStream();
        await using (var managed = new BrotliCompressionStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int i = 0; i < data.Length; i += 10_000)
            {
                await managed.WriteAsync(data.AsMemory(i, Math.Min(10_000, data.Length - i)), TestContext.Current.CancellationToken);
            }

            await managed.FlushAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(NativeBrotli.Decompress(compressed.ToArray(), data.Length + 1).Output, data);
        await using var reader = new BrotliCompressionStream(new MemoryStream(compressed.ToArray()), CompressionMode.Decompress);
        var content = new MemoryStream();
        byte[] buffer = new byte[777];
        int read;
        while ((read = await reader.ReadAsync(buffer, TestContext.Current.CancellationToken)) > 0)
        {
            content.Write(buffer, 0, read);
        }

        Assert.Equal(data, content.ToArray());
    }

    [Fact]
    public void ReadsWhatBrotliStreamWrites()
    {
        foreach (CompressionLevel level in new[] { CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
        {
            byte[] data = DataKinds.Generate("json", 200_000);
            var compressed = new MemoryStream();
            using (var native = new BrotliStream(compressed, level, leaveOpen: true))
            {
                native.Write(data);
            }

            Assert.Equal(data, ReadAll(new BrotliCompressionStream(new MemoryStream(compressed.ToArray()), CompressionMode.Decompress), new Random(1)));
        }
    }

    [Fact]
    public void RefusesATruncatedStream()
    {
        byte[] stream = NativeBrotli.Compress(DataKinds.Generate("text", 100_000), 5, 22);
        using var reader = new BrotliCompressionStream(new MemoryStream(stream[..^10]), CompressionMode.Decompress);
        Assert.Throws<InvalidDataException>(() => reader.CopyTo(Stream.Null));
    }

    [Fact]
    public void RefusesTheWrongDirection()
    {
        using var writer = new BrotliCompressionStream(new MemoryStream(), CompressionMode.Compress);
        Assert.False(writer.CanRead);
        Assert.Throws<InvalidOperationException>(() => writer.Read(new byte[1], 0, 1));
        using var reader = new BrotliCompressionStream(new MemoryStream([6]), CompressionMode.Decompress);
        Assert.False(reader.CanWrite);
        Assert.Throws<InvalidOperationException>(() => reader.Write([1], 0, 1));
        Assert.Equal(-1, reader.ReadByte());
    }

    private static byte[] ReadAll(Stream stream, Random random)
    {
        using (stream)
        {
            var content = new MemoryStream();
            byte[] buffer = new byte[1 << 16];
            int read;
            while ((read = stream.Read(buffer, 0, random.Next(1, buffer.Length))) > 0)
            {
                content.Write(buffer, 0, read);
            }

            return content.ToArray();
        }
    }
}
