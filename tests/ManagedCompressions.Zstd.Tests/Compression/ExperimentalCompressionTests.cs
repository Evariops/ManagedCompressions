using System;
using System.Buffers;
using System.IO.Compression;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Compression;

/// <summary>
/// The experimental features (<see cref="ZstdExperimentalOptions"/>): their frames are not libzstd's,
/// so the guarantee is the format's. Every frame must decode back to its content with libzstd (the
/// platform's decoder) and with this library's, frame after frame on one compressor, at every level
/// and size the feature changes, and on sizes around its edges.
/// </summary>
public sealed class ExperimentalCompressionTests
{
    public static TheoryData<string, int> KindsAndSizes()
    {
        var data = new TheoryData<string, int>();
        foreach (string kind in (string[])[.. DataKinds.All, "hex"])
        {
            foreach (int size in new[] { 0, 1, 7, 12, 13, 20, 33, 100, 1000, 4096, 65_536, 131_073, 300_000, 1 << 20 })
            {
                data.Add(kind, size);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(KindsAndSizes))]
    public void Entropy_aware_frames_decode_with_libzstd_and_here(string kind, int size)
    {
        foreach (int level in new[] { -5, 1, 2, 3, 4, 5 })
        {
            var compressor = new ZstdCompressor(level, ZstdExperimentalOptions.EntropyAwareMatches);
            for (int seed = 1; seed <= 3; seed++)
            {
                // Frame after frame on one compressor: its tables carry indices from one to the next.
                byte[] content = kind == "hex" ? Hexadecimal(size, seed) : DataKinds.Generate(kind, size, seed);
                byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
                Assert.Equal(OperationStatus.Done, compressor.Compress(content, frame, out _, out int length));
                AssertDecodes(content, frame.AsSpan(0, length), $"{kind}, {size} bytes, seed {seed}, level {level}");
            }
        }
    }

    /// <summary>
    /// Parallel compression takes the features into each job: its frame, of jobs that each weighed their
    /// matches, decodes as any other.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async System.Threading.Tasks.Task Parallel_frames_with_the_features_decode(int level)
    {
        // Hexadecimal and text in turn, over several jobs.
        byte[] content = new byte[6 << 20];
        for (int at = 0, part = 0; at < content.Length; at += 1 << 20, part++)
        {
            byte[] piece = part % 2 == 0 ? Hexadecimal(1 << 20, part) : DataKinds.Generate("text", 1 << 20, part);
            piece.CopyTo(content, at);
        }

        var compressor = new ZstdCompressor(level, ZstdExperimentalOptions.EntropyAwareMatches);
        byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
        int length = await compressor.CompressAsync(content, frame, maxDegreeOfParallelism: 4, TestContext.Current.CancellationToken);
        AssertDecodes(content, frame.AsSpan(0, length), $"parallel, level {level}");
    }

    [Fact]
    public void Experimental_features_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdCompressor(3, (ZstdExperimentalOptions)(1 << 30)));
        Assert.Equal(ZstdExperimentalOptions.None, new ZstdCompressor(3).Experimental);
        Assert.Equal(ZstdExperimentalOptions.EntropyAwareMatches, new ZstdCompressor(3, ZstdExperimentalOptions.EntropyAwareMatches).Experimental);
    }

    /// <summary>
    /// Random hexadecimal digits, whose matches the entropy-aware search refuses: four bits a byte, and
    /// matches of five or six bytes at random distances.
    /// </summary>
    private static byte[] Hexadecimal(int size, int seed)
    {
        var random = new Random(seed);
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++)
        {
            data[i] = (byte)"0123456789abcdef"[random.Next(16)];
        }

        return data;
    }

    /// <summary>
    /// A frame both decoders must take whole and give back as <paramref name="content"/>: libzstd
    /// through the platform's decoder, and <see cref="ZstdDecompressor"/>.
    /// </summary>
    internal static void AssertDecodes(byte[] content, ReadOnlySpan<byte> frame, string what)
    {
        // A byte of room more: the platform's decoder reports an empty destination too small, even for an empty frame.
        byte[] decoded = new byte[content.Length + 1];
        using (var platform = new ZstandardDecoder())
        {
            OperationStatus status = platform.Decompress(frame, decoded, out int consumed, out int written);
            Assert.True(
                status == OperationStatus.Done && consumed == frame.Length && written == content.Length && decoded.AsSpan(0, written).SequenceEqual(content),
                $"{what}: libzstd gives {status}, {consumed} of {frame.Length} bytes read, {written} of {content.Length} written");
        }

        decoded = new byte[content.Length];
        OperationStatus own = new ZstdDecompressor().Decompress(frame, decoded, out int ownConsumed, out int ownWritten);
        Assert.True(
            own == OperationStatus.Done && ownConsumed == frame.Length && ownWritten == content.Length && decoded.AsSpan().SequenceEqual(content),
            $"{what}: this library gives {own}, {ownConsumed} of {frame.Length} bytes read, {ownWritten} of {content.Length} written");
    }
}
