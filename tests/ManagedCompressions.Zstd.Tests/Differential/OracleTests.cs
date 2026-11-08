using System;
using System.Buffers;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Differential;

/// <summary>
/// The oracle itself. It must be libzstd 1.5.7, the version whose frames the compressor writes: another
/// version fails here, rather than in hundreds of differential tests at once. On .NET 11 it must also
/// write the frames of the platform's libzstd, the oracle before it, and decode them alike.
/// </summary>
public sealed class OracleTests
{
    [Fact]
    public void The_oracle_is_libzstd_1_5_7() => Assert.Equal(10507u, NativeZstd.VersionNumber);

#if NET11_0_OR_GREATER
    public static TheoryData<string, int, bool> Frames()
    {
        var data = new TheoryData<string, int, bool>();
        foreach (string kind in (string[])["text", "json", "walk64"])
        {
            foreach (int level in (int[])[-5, 1, 3, 9, 19, 22])
            {
                data.Add(kind, level, level == 3);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void It_writes_the_platforms_frames(string kind, int level, bool checksum)
    {
        // Past 256 KiB, where libzstd's parameters stop depending on the size.
        byte[] data = DataKinds.Generate(kind, 300 * 1024);
        byte[] native = NativeZstd.Compress(data, new NativeZstdOptions { Quality = level, AppendChecksum = checksum });
        Assert.Equal(Platform(data, level, checksum, null), native);
        AssertDecodesAlike(native, data, null);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(19)]
    public void It_writes_the_platforms_frames_with_a_dictionary(int level)
    {
        (byte[] dictionary, NativeZstdDictionary trained) = Dictionaries.Get("json", "trained");
        byte[] data = DataKinds.Generate("json", 48 * 1024, 5);
        byte[] native = NativeZstd.Compress(data, new NativeZstdOptions { Quality = level, Dictionary = trained });
        Assert.Equal(Platform(data, level, false, dictionary), native);
        AssertDecodesAlike(native, data, dictionary);
    }

    /// <summary>The frame of the platform's ZstandardEncoder, the dictionary prepared at the level.</summary>
    private static byte[] Platform(byte[] data, int level, bool checksum, byte[]? dictionary)
    {
        using System.IO.Compression.ZstandardDictionary? prepared =
            dictionary is null ? null : System.IO.Compression.ZstandardDictionary.Create(dictionary, level);
        var options = new System.IO.Compression.ZstandardCompressionOptions { Quality = level, AppendChecksum = checksum, Dictionary = prepared };
        using var encoder = new System.IO.Compression.ZstandardEncoder(options);
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, encoder.Compress(data, output, out int consumed, out int written, isFinalBlock: true));
        Assert.Equal(data.Length, consumed);
        return output.AsSpan(0, written).ToArray();
    }

    private static void AssertDecodesAlike(byte[] frame, byte[] content, byte[]? dictionary)
    {
        using NativeZstdDictionary? native = dictionary is null ? null : NativeZstdDictionary.Create(dictionary);
        byte[] ours = new byte[content.Length + 1];
        Assert.Equal(OperationStatus.Done, NativeZstd.Decompress(frame, ours, native, out int consumed, out int written));
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(content.Length, written);

        using System.IO.Compression.ZstandardDictionary? prepared =
            dictionary is null ? null : System.IO.Compression.ZstandardDictionary.Create(dictionary);
        using var decoder = prepared is null ? new System.IO.Compression.ZstandardDecoder() : new System.IO.Compression.ZstandardDecoder(prepared);
        byte[] theirs = new byte[content.Length + 1];
        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, theirs, out int platformConsumed, out int platformWritten));
        Assert.Equal(consumed, platformConsumed);
        Assert.Equal(written, platformWritten);
        Assert.True(content.AsSpan().SequenceEqual(ours.AsSpan(0, written)) && content.AsSpan().SequenceEqual(theirs.AsSpan(0, platformWritten)));
    }
#endif
}
