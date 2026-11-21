using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using ManagedCompressions.Zstd.Internal;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Compression;

/// <summary>
/// <see cref="ZstdCompressor.CompressAsync"/> with <see cref="ZstdJobFrames.Independent"/>: a frame per
/// job, each the one libzstd writes in one shot of the job's section (the oracle, frame by frame), the
/// frames one after the other; read back whole by <see cref="ZstdDecompressor.DecompressAsync"/> and by
/// the streaming decoder.
/// </summary>
public sealed class IndependentJobFramesTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string kind in new[] { "text", "json", "mixed", "incompressible", "zeros" })
        {
            foreach (int level in new[] { -1, 1, 3, 7, 12, 19 })
            {
                data.Add(Name(kind, 3_000_000, level, 512 << 10, level == 3));
            }
        }

        data.Add(Name("text", 524_288, 3, 0, false));
        data.Add(Name("text", 524_289, 3, 512 << 10, true));
        data.Add(Name("json", 5_000_000, 1, 0, false));
        data.Add(Name("mixed", 9_000_000, 3, 0, true));
        return data;
    }

    private static string Name(string kind, int size, int level, int jobSize, bool checksum) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind}/{size}/L{level}/job={jobSize}{(checksum ? "/xxh" : string.Empty)}");

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_job_is_libzstds_frame_of_its_section(string name)
    {
        string[] parts = name.Split('/');
        byte[] content = DataKinds.Generate(parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture), seed: 3);
        int level = int.Parse(parts[2][1..], CultureInfo.InvariantCulture);
        bool checksum = parts.Length > 4;
        var compressor = new ZstdCompressor(level)
        {
            AppendChecksum = checksum,
            JobFrames = ZstdJobFrames.Independent,
            JobSizeOverride = int.Parse(parts[3]["job=".Length..], CultureInfo.InvariantCulture),
        };

        foreach (int degree in new[] { 1, 3, -1 })
        {
            byte[] destination = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length) + 4096];
            int size = await compressor.CompressAsync(content, destination, degree, TestContext.Current.CancellationToken);
            byte[] frames = destination.AsSpan(0, size).ToArray();

            // Frame by frame: the section it declares, compressed by libzstd in one shot.
            List<FrameSpan> found = FrameScan.Scan(frames, out bool complete);
            Assert.True(complete);
            // A job is never smaller than the overlap libzstd would load before it (the whole 8 MiB window
            // at level 19), independent or not: the fast levels' small jobs make several frames.
            if (content.Length <= ZstdCompressor.MinJobsSourceSize)
            {
                Assert.Single(found);
            }
            else if (level <= 3)
            {
                Assert.True(found.Count >= 2, $"{found.Count} frames");
            }

            int offset = 0;
            foreach (FrameSpan frame in found)
            {
                Assert.False(frame.Skippable);
                Assert.True(frame.ContentSize >= 0);
                byte[] expected = NativeZstd.Compress(content.AsSpan(offset, (int)frame.ContentSize), new NativeZstdOptions { Quality = level, AppendChecksum = checksum });
                Assert.True(frames.AsSpan(frame.Offset, frame.Length).SequenceEqual(expected), $"degree {degree}, frame at {frame.Offset}");
                offset += (int)frame.ContentSize;
            }

            Assert.Equal(content.Length, offset);

            // Read back whole, in parallel and as a stream.
            byte[] decoded = new byte[content.Length];
            Assert.Equal(content.Length, await new ZstdDecompressor().DecompressAsync(frames, decoded, 4, TestContext.Current.CancellationToken));
            Assert.True(decoded.AsSpan().SequenceEqual(content));
            using var stream = new ZstdStream(new MemoryStream(frames), System.IO.Compression.CompressionMode.Decompress);
            var streamed = new MemoryStream();
            await stream.CopyToAsync(streamed, TestContext.Current.CancellationToken);
            Assert.True(streamed.ToArray().AsSpan().SequenceEqual(content));
        }
    }

    [Fact]
    public async Task The_default_stays_one_frame()
    {
        byte[] content = DataKinds.Generate("text", 3_000_000, seed: 4);
        var compressor = new ZstdCompressor(3) { JobSizeOverride = 512 << 10 };
        Assert.Equal(ZstdJobFrames.Single, compressor.JobFrames);
        byte[] destination = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
        int size = await compressor.CompressAsync(content, destination, 4, TestContext.Current.CancellationToken);
        Assert.Single(FrameScan.Scan(destination.AsSpan(0, size), out bool complete));
        Assert.True(complete);
    }
}
