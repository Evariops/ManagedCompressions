using System;
using System.Buffers;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCompressions.Zstd.Tests.Differential;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Compression;

/// <summary>
/// The frames libzstd writes with workers (zstdmt), against libzstd built with threads
/// (<see cref="LibzstdMt"/>), byte for byte: jobs of the default sizes over sources of several jobs
/// at the fast levels, and jobs and overlaps made small (<c>ZSTD_c_jobSize</c>, <c>ZSTD_c_overlapLog</c>)
/// to cut sources of a few megabytes into many jobs at every level.
/// </summary>
public sealed class JobCompressionTests
{
    private static readonly string[] Kinds = ["text", "json", "walk64", "mixed", "incompressible", "zeros", "urls", "repeats"];

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();

        // The defaults: jobs of four windows, the overlap by the strategy. Each kind takes two of the
        // four, and each of the four four kinds.
        (int Size, int Level, bool Checksum)[] defaults = [(600_000, 3, false), (5_000_000, 1, false), (5_000_000, -3, true), (20_000_000, 3, false)];
        for (int k = 0; k < Kinds.Length; k++)
        {
            foreach ((int size, int level, bool checksum) in (ReadOnlySpan<(int, int, bool)>)[defaults[k % 4], defaults[(k + 1) % 4]])
            {
                data.Add(Name(Kinds[k], size, level, 0, 0, checksum));
            }
        }

        data.Add(Name("text", 20_000_000, 5, 0, 0, true));
        data.Add(Name("json", 40_000_000, 9, 0, 0, false));

        // The edges: no workers up to 512 KiB, one job past it, a last job of a whole job, of a byte,
        // of fewer bytes than a hash reads. Level 19's jobs are its window, whatever the job size
        // asked for: past one job, its frames of these sizes are all a single job.
        foreach (int level in new[] { 1, 3, 9 })
        {
            foreach (int size in new[] { 524_288, 524_289, 1_048_576, 1_048_577, 1_048_583, 1_572_864 + 100 })
            {
                data.Add(Name("mixed", size, level, 512 << 10, 0, false));
            }
        }

        data.Add(Name("mixed", 524_288, 19, 512 << 10, 0, false));
        data.Add(Name("mixed", 524_289, 19, 512 << 10, 0, false));
        data.Add(Name("text", (2 << 20) + 1, 1, 0, 0, true));

        // Small jobs at every level: the mixed content, which holds most kinds, and two kinds more in
        // turn, so that each kind meets the strategies from the fastest to the strongest. The jobs and
        // overlaps follow the window, which follows the source: 1.5 MB cut as 3 MB would be, three jobs
        // up to the lazy strategies, two for the first optimal parsers, one for the strongest.
        int[] levels = [-1, 1, 2, 3, 4, 5, 6, 8, 10, 12, 13, 15, 16, 17, 18, 19, 22];
        string[] others = [.. Kinds.Where(kind => kind != "mixed")];
        for (int l = 0; l < levels.Length; l++)
        {
            foreach (string kind in (string[])["mixed", others[(2 * l) % others.Length], others[((2 * l) + 1) % others.Length]])
            {
                data.Add(Name(kind, 1_500_000, levels[l], 512 << 10, 0, levels[l] % 2 == 0));
            }
        }

        // Overlaps from none to the window.
        foreach (int overlapLog in new[] { 1, 3, 6, 9 })
        {
            foreach (int level in new[] { 1, 3, 7, 13, 19 })
            {
                data.Add(Name("mixed", 2_500_000, level, 700_000, overlapLog, false));
            }
        }

        return data;
    }

    private static string Name(string kind, int size, int level, int jobSize, int overlapLog, bool checksum) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind}/{size}/L{level}/job={jobSize}/ovlog={overlapLog}{(checksum ? "/xxh" : string.Empty)}");

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Compresses_like_libzstd_with_workers(string name)
    {
        LibzstdMt? libzstd = LibzstdMt.Instance;
        Assert.SkipWhen(libzstd is null, "libzstd with threads is not built: tools/native-ref/build.sh mt");
        string[] parts = name.Split('/');
        string kind = parts[0];
        int size = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int level = int.Parse(parts[2][1..], CultureInfo.InvariantCulture);
        int jobSize = int.Parse(parts[3]["job=".Length..], CultureInfo.InvariantCulture);
        int overlapLog = int.Parse(parts[4]["ovlog=".Length..], CultureInfo.InvariantCulture);
        bool checksum = parts.Length > 5;

        byte[] data = DataKinds.Generate(kind, size);
        byte[] expected = libzstd!.Compress(data, level, workers: 2, checksum, jobSize: jobSize, overlapLog: overlapLog);

        // The jobs one after the other on the compressor itself: the frame, and where it parts.
        var compressor = new ZstdCompressor(level) { AppendChecksum = checksum, JobSizeOverride = jobSize, OverlapLogOverride = overlapLog };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(size)];
        Assert.Equal(OperationStatus.Done, compressor.CompressInJobs(data, output, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);
        CompressionCorpus.AssertDecodes(output.AsSpan(0, written), data, name, libzstds: true);

        // In parallel, twice: on new workers, then on those the first call kept (the levels whose
        // compressors hold little keep them), whose tables carry over from job to job and frame to
        // frame. The degrees go round the cases, from one job at a time to more workers than jobs.
        int turn = Support.Cases.Turn(name);
        foreach (int degree in (int[])[Degrees[turn % Degrees.Length], Degrees[(turn + 1) % Degrees.Length]])
        {
            Array.Clear(output);
            int parallel = await compressor.CompressAsync(data, output, degree, TestContext.Current.CancellationToken);
            Corpus.AssertSameBytes(expected, output.AsSpan(0, parallel), name + " in parallel at " + degree);
        }
    }

    private static readonly int[] Degrees = [1, 3, 8];

    /// <summary>
    /// Frames of libzstd with threads, recorded by their size and SHA-256: the jobs stay checked where
    /// that library is not built.
    /// </summary>
    [Theory]
    [InlineData("text", 3000000, 3, 524288, 0, false, null, 852189, "9263AAA0334578838E3B489FDAC3571B6037E6DC27F77737D3B3A8D4181B989C")]
    [InlineData("mixed", 3000000, 1, 524288, 0, true, null, 1386798, "8A778D6DCA540E3C8D9F074F4211EF3F58341DA621B4B2BE5B27F71E3A6B8194")]
    [InlineData("json", 2500000, 9, 700000, 6, false, null, 292727, "C152A032BFD9F24C9B318BB0009AD5FA19BCE8F1C608A7788B11BC275C1CCF21")]
    [InlineData("walk64", 3000000, 19, 524288, 0, false, null, 924363, "67D6FD9502B4E078B7661DD1B248D7A68A680D77160817D571B36C62C8E180FA")]
    [InlineData("mixed", 5000000, 1, 0, 0, false, null, 2264375, "EC69B26033ACF020B584D1173081A9514AC69E7AD0B45A2FC34D401286E7C332")]
    [InlineData("urls", 3000000, 13, 524288, 9, true, null, 559962, "7FCF771AB7A9211AD4ED57CA2DE39389E02E7F4058C3CCB8DB93B73E565F721A")]
    [InlineData("text", 1100000, 5, 524288, 0, false, "trained", 311040, "0318F3866C06FEA4B0ED2DACD8331252225311A130CF327D20442B9D05D17206")]
    [InlineData("json", 1100000, 16, 524288, 0, false, "raw", 115303, "9A0CA46373DC76E9B95032290BFD585F2C92EBFAC7D273B524EB87717030F545")]
    public async Task Writes_the_recorded_frames_of_libzstd_with_workers(
        string kind, int size, int level, int jobSize, int overlapLog, bool checksum, string? dictionary, int length, string sha256)
    {
        byte[] data = DataKinds.Generate(kind, size);
        ZstdCompressor compressor = dictionary is null ? new ZstdCompressor(level) : new ZstdCompressor(level, Dictionaries.Get(kind, dictionary).Bytes);
        compressor.AppendChecksum = checksum;
        compressor.JobSizeOverride = jobSize;
        compressor.OverlapLogOverride = overlapLog;
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(size)];
        int written = await compressor.CompressAsync(data, output, 4, TestContext.Current.CancellationToken);
        Assert.Equal(length, written);
        Assert.Equal(sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(output.AsSpan(0, written))));
    }

    /// <summary>
    /// The jobs write into the destination itself, each at the sum of the bounds of the jobs before it,
    /// then move down: the frame is the sequential one whatever room the destination has, from the
    /// bound down to the frame's own size, where the last jobs' slots fall past its end; a byte less is
    /// refused.
    /// </summary>
    [Theory]
    [InlineData("mixed", 1, false)]
    [InlineData("text", 3, true)]
    [InlineData("incompressible", 3, true)]
    [InlineData("json", 9, false)]
    public async Task Writes_the_frame_into_any_destination_that_holds_it(string kind, int level, bool checksum)
    {
        byte[] data = DataKinds.Generate(kind, 3_000_000, level);
        var compressor = new ZstdCompressor(level) { AppendChecksum = checksum, JobSizeOverride = 512 << 10 };
        int bound = ZstdCompressor.GetMaxCompressedLength(data.Length);
        byte[] expected = new byte[bound];
        Assert.Equal(OperationStatus.Done, compressor.CompressInJobs(data, expected, out int length));
        foreach (int room in (int[])[bound, length + 5000, length + 1, length])
        {
            byte[] output = new byte[room];
            output.AsSpan().Fill(0xA5);
            int written = await compressor.CompressAsync(data, output, 3, TestContext.Current.CancellationToken);
            Corpus.AssertSameBytes(expected.AsSpan(0, length), output.AsSpan(0, written), $"{kind} L{level}, room {room}");
        }

        await Assert.ThrowsAsync<ArgumentException>(
            () => compressor.CompressAsync(data, new byte[length - 1], 3, TestContext.Current.CancellationToken).AsTask());
    }

    /// <summary>
    /// A call cancelled while its first job runs: the job runs to its end, the jobs waiting for a thread
    /// never start, the call throws, and the compressor compresses the next call's frame as before.
    /// </summary>
    [Fact]
    public async Task A_cancelled_call_stops_and_leaves_the_compressor_whole()
    {
        byte[] data = DataKinds.Generate("text", 4_000_000, 19);
        // No overlap, which at level 19 would take a job to the whole window: eight jobs.
        var compressor = new ZstdCompressor(19) { JobSizeOverride = 512 << 10, OverlapLogOverride = 1 };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(50);
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compressor.CompressAsync(data, output, 1, cancellation.Token).AsTask());

        byte[] small = DataKinds.Generate("json", 1_200_000, 5);
        byte[] expected = new byte[ZstdCompressor.GetMaxCompressedLength(small.Length)];
        Assert.Equal(OperationStatus.Done, compressor.CompressInJobs(small, expected, out int length));
        int written = await compressor.CompressAsync(small, output, 2, TestContext.Current.CancellationToken);
        Assert.True(expected.AsSpan(0, length).SequenceEqual(output.AsSpan(0, written)));
    }

    /// <summary>
    /// Every level with every kind, and the type of dictionary in turn: each pair of a level, a kind and
    /// a type meets in some case (a covering array of strength 2), where the whole product would be
    /// three times the cases.
    /// </summary>
    public static TheoryData<string> DictionaryCases()
    {
        var data = new TheoryData<string>();
        string[] kinds = ["text", "json", "mixed", "walk64"];
        string[] types = ["trained", "raw", "large"];
        int[] levels = [-1, 1, 3, 5, 9, 13, 16, 19];
        for (int l = 0; l < levels.Length; l++)
        {
            for (int k = 0; k < kinds.Length; k++)
            {
                data.Add($"{kinds[k]}/{types[(k + l) % types.Length]}/L{levels[l]}");
            }
        }

        return data;
    }

    /// <summary>
    /// With a dictionary, which only the first job takes: loaded by the frame (sources over six times
    /// the dictionary), or its tables copied (a large raw dictionary, over a sixth of the source).
    /// </summary>
    [Theory]
    [MemberData(nameof(DictionaryCases))]
    public async Task Compresses_like_libzstd_with_workers_and_a_dictionary(string name)
    {
        LibzstdMt? libzstd = LibzstdMt.Instance;
        Assert.SkipWhen(libzstd is null, "libzstd with threads is not built: tools/native-ref/build.sh mt");
        string[] parts = name.Split('/');
        int level = int.Parse(parts[2][1..], CultureInfo.InvariantCulture);
        byte[] dictionary = parts[1] == "large"
            ? DataKinds.Generate(parts[0], 200_000, 9)
            : Dictionaries.Get(parts[0], parts[1]).Bytes;
        byte[] data = DataKinds.Generate(parts[0], 1_100_000);
        byte[] expected = libzstd!.Compress(data, level, workers: 2, dictionary: dictionary, jobSize: 512 << 10);

        var compressor = new ZstdCompressor(level, dictionary) { JobSizeOverride = 512 << 10 };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.CompressInJobs(data, output, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);

        byte[] decoded = new byte[data.Length];
        Assert.Equal(OperationStatus.Done, new ZstdDecompressor(dictionary).Decompress(output.AsSpan(0, written), decoded, out _, out int decodedSize));
        Corpus.AssertSameBytes(data, decoded.AsSpan(0, decodedSize), name);

        Array.Clear(output);
        int parallel = await compressor.CompressAsync(data, output, 4, TestContext.Current.CancellationToken);
        Corpus.AssertSameBytes(expected, output.AsSpan(0, parallel), name + " in parallel");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(19)]
    public async Task Keeps_only_the_job_compressors_that_hold_little(int level)
    {
        byte[] data = DataKinds.Generate("text", 3_000_000, seed: 5);
        var compressor = new ZstdCompressor(level) { JobSizeOverride = 512 << 10 };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        await compressor.CompressAsync(data, output, 4, TestContext.Current.CancellationToken);
        long[] kept = compressor.IdleWorkerBytes();
        Assert.All(kept, bytes => Assert.True(bytes <= 16L << 20, $"{bytes} bytes kept"));
        if (level == 1)
        {
            // The fast levels' compressors hold a few MiB: they are kept for the next call.
            Assert.NotEmpty(kept);
        }

        // The next call writes the same frame, with the compressors kept or made again.
        byte[] again = new byte[output.Length];
        int first = await compressor.CompressAsync(data, again, 4, TestContext.Current.CancellationToken);
        Assert.True(again.AsSpan(0, first).SequenceEqual(output.AsSpan(0, first)));
    }
}
