using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ManagedCompressions.Zstd.Internal;
using ManagedCompressions.Zstd.Tests.Differential;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Compression;

/// <summary>
/// libzstd turns its long-distance matcher on by itself only at level 22 for sources over 64 MiB; asked
/// for explicitly, it runs at any size. The matcher is compared that way, on sources of a few megabytes,
/// at the optimal parsers' levels.
/// </summary>
public sealed class LongDistanceCompressionTests
{
    private static readonly int[] Levels = [16, 17, 18, 19, 22];

    /// <summary>
    /// The far repeats, whose frames the matcher's own matches fill, at every level; the other kinds,
    /// which give it little to find, at one level each, in turn.
    /// </summary>
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        for (int k = 0; k < DataKinds.All.Length; k++)
        {
            data.Add(CorpusCase.Name(DataKinds.All[k], 1_500_000, Levels[k % Levels.Length]));
        }

        foreach (string kind in FarRepeats.Kinds)
        {
            foreach (int level in Levels)
            {
                data.Add(CorpusCase.Name(kind, 3_000_000, level));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Compresses_like_libzstd(string name)
    {
        CorpusCase @case = CorpusCase.Parse(name);
        byte[] data = FarRepeats.Generate(@case.Kind, @case.Size);
        byte[] expected = Libzstd(@case.Kind, @case.Size, @case.Level, longDistance: true);

        var compressor = new ZstdCompressor(@case.Level) { LongDistanceMatching = true };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);
        CompressionCorpus.AssertDecodes(output.AsSpan(0, written), data, name, libzstds: true);

        // A second frame on the same compressor, whose tables carry over but whose matcher starts afresh:
        // on far repeats, so that a matcher that carried over would show, one kind a level, in turn.
        int far = Array.IndexOf(FarRepeats.Kinds, @case.Kind);
        if (far >= 0 && far == Array.IndexOf(Levels, @case.Level) % FarRepeats.Kinds.Length)
        {
            Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int again));
            Corpus.AssertSameBytes(expected, output.AsSpan(0, again), name);
        }
    }

    /// <summary>
    /// Sizes across the three ways a frame takes a dictionary (attached, its tables copied, loaded):
    /// libzstd's matcher starts afresh at the source in all of them, and loads a raw-content dictionary
    /// when the frame does.
    /// </summary>
    private static readonly int[] DictionarySizes = [6_000, 100_000, 1_500_000];

    private static readonly string[] DictionaryTypes = ["trained", "raw"];

    /// <summary>
    /// Every level with every kind, the size and the type of dictionary in turn: each pair of a level,
    /// a kind, a size and a type meets in some case (a covering array of strength 2), where the whole
    /// product would be six times the cases.
    /// </summary>
    private static IEnumerable<(string Kind, int Size, int Level, string Type)> DictionaryCombinations() =>
        from l in Enumerable.Range(0, Levels.Length)
        from k in Enumerable.Range(0, FarRepeats.Kinds.Length)
        select (FarRepeats.Kinds[k], DictionarySizes[(k + l) % DictionarySizes.Length], Levels[l], DictionaryTypes[(k + l) % DictionaryTypes.Length]);

    public static TheoryData<string> DictionaryCases() =>
        [.. DictionaryCombinations().Select(c => CorpusCase.Name(c.Kind, c.Size, c.Level, "dict=" + c.Type))];

    [Theory]
    [MemberData(nameof(DictionaryCases))]
    public void Compresses_with_a_dictionary_like_libzstd(string name)
    {
        CorpusCase @case = CorpusCase.Parse(name);
        (byte[] dictionary, _) = Dictionaries.Get(FarRepeats.Content(@case.Kind), @case.Dictionary!);
        byte[] data = FarRepeats.Generate(@case.Kind, @case.Size, dictionary);
        using NativeZstdDictionary native = NativeZstdDictionary.Create(dictionary, @case.Level);
        byte[] expected = NativeZstd.Compress(data, new NativeZstdOptions { Dictionary = native, EnableLongDistanceMatching = true });

        var compressor = new ZstdCompressor(@case.Level, dictionary) { LongDistanceMatching = true };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);

        byte[] decoded = new byte[data.Length];
        Assert.Equal(OperationStatus.Done, new ZstdDecompressor(dictionary).Decompress(output.AsSpan(0, written), decoded, out _, out int decodedSize));
        Corpus.AssertSameBytes(data, decoded.AsSpan(0, decodedSize), name);

        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int again));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, again), name);
    }

    /// <summary>
    /// Long-distance matches across a raw dictionary's end and the source's start, which follow each
    /// other in the matcher's indices: later in the source, the dictionary's last kilobyte then the
    /// source's first bytes (a match in the dictionary that runs on into the source), and the
    /// dictionary's last 24 bytes then the source's first bytes (a match at the source's start that
    /// runs back into the dictionary, too short for one of its own there). The matcher finds both,
    /// but so does the parser's own search, which counts across the segments too: the frames guard
    /// the matcher's reads at the boundary more than they depend on its lengths there.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(22)]
    public void Matches_across_a_raw_dictionary_end_like_libzstd(int level)
    {
        (byte[] dictionary, _) = Dictionaries.Get("text", "raw");
        byte[] data = DataKinds.Generate("json", 1_500_000);
        Span<byte> head = data.AsSpan(0, 8192);
        dictionary.AsSpan(dictionary.Length - 1024).CopyTo(data.AsSpan(500_000));
        head.CopyTo(data.AsSpan(500_000 + 1024));
        dictionary.AsSpan(dictionary.Length - 24).CopyTo(data.AsSpan(1_000_000));
        head.CopyTo(data.AsSpan(1_000_000 + 24));

        using NativeZstdDictionary native = NativeZstdDictionary.Create(dictionary, level);
        byte[] expected = NativeZstd.Compress(data, new NativeZstdOptions { Dictionary = native, EnableLongDistanceMatching = true });
        var compressor = new ZstdCompressor(level, dictionary) { LongDistanceMatching = true };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), "L" + level);
    }

    /// <summary>
    /// The dictionary cases must exercise the matcher too: where their frames load a raw-content
    /// dictionary, libzstd's change with it, for most of them.
    /// </summary>
    [Fact]
    public void The_matcher_changes_the_frames_with_a_raw_dictionary()
    {
        int changed = 0;
        int loaded = 0;
        foreach ((string kind, int size, int level, string type) in DictionaryCombinations())
        {
            if (size != DictionarySizes[^1] || type != "raw")
            {
                continue;
            }

            (byte[] dictionary, _) = Dictionaries.Get(FarRepeats.Content(kind), "raw");
            byte[] data = FarRepeats.Generate(kind, size, dictionary);
            using NativeZstdDictionary native = NativeZstdDictionary.Create(dictionary, level);
            byte[] with = NativeZstd.Compress(data, new NativeZstdOptions { Dictionary = native, EnableLongDistanceMatching = true });
            byte[] without = NativeZstd.Compress(data, new NativeZstdOptions { Dictionary = native });
            changed += with.AsSpan().SequenceEqual(without) ? 0 : 1;
            loaded++;
        }

        Assert.True(loaded > 0 && changed * 2 >= loaded, $"only {changed} of {loaded} frames changed");
    }

    /// <summary>
    /// The cases must exercise the matcher: libzstd's frames change with it, for most of them. Its frames
    /// with the matcher are those <see cref="Compresses_like_libzstd"/> compares, made once.
    /// </summary>
    [Fact]
    public void The_matcher_changes_the_frames()
    {
        int changed = 0;
        foreach (string kind in FarRepeats.Kinds)
        {
            foreach (int level in Levels)
            {
                changed += Libzstd(kind, 3_000_000, level, longDistance: true).AsSpan().SequenceEqual(Libzstd(kind, 3_000_000, level, longDistance: false)) ? 0 : 1;
            }
        }

        Assert.True(changed >= FarRepeats.Kinds.Length * Levels.Length / 2, $"only {changed} frames changed");
    }

    /// <summary>Over 64 MiB at level 22, libzstd's window reaches 128 MiB and its matcher turns itself on.</summary>
    private const int TurnedOnSize = (64 << 20) + 300_000;

    /// <summary>The source of the frames the matcher turns itself on for, made once for the three tests that compress it.</summary>
    private static readonly Lazy<byte[]> TurnedOnSource = new(() => FarRepeats.Long(TurnedOnSize));

    /// <summary>libzstd's frame of <see cref="TurnedOnSource"/> at level 22, the matcher left to it: on.</summary>
    private static readonly Lazy<byte[]> TurnedOnFrame = new(() => NativeZstd.Compress(TurnedOnSource.Value, new NativeZstdOptions { Quality = 22 }));

    /// <summary>
    /// Level 22 over 64 MiB: libzstd's window reaches 128 MiB and its matcher turns itself on; the
    /// frames are the same with no switch on either side.
    /// </summary>
    [Fact]
    public void Turns_itself_on_like_libzstd()
    {
        Assert.True(LongDistanceMatcher.EnabledFor(CompressionParameters.ForFrame(22, TurnedOnSize)));
        Assert.False(LongDistanceMatcher.EnabledFor(CompressionParameters.ForFrame(22, 64 << 20)));
        Assert.False(LongDistanceMatcher.EnabledFor(CompressionParameters.ForFrame(21, TurnedOnSize)));

        byte[] data = TurnedOnSource.Value;
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, new ZstdCompressor(22).Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(TurnedOnFrame.Value, output.AsSpan(0, written), "long/" + TurnedOnSize + "/L22");
        CompressionCorpus.AssertDecodes(output.AsSpan(0, written), data, "long/" + TurnedOnSize + "/L22", libzstds: true);
    }

    /// <summary>
    /// The same source with the matcher kept off (<see cref="ZstdCompressionOptions.LongDistanceMatching"/>
    /// false, libzstd's <c>ZSTD_ps_disable</c>): libzstd's frame without it, which is not the frame it
    /// makes when it turns the matcher on by itself.
    /// </summary>
    [Fact]
    public void Kept_off_like_libzstd()
    {
        byte[] data = TurnedOnSource.Value;
        byte[] expected = NativeZstd.Compress(data, new NativeZstdOptions { Quality = 22, DisableLongDistanceMatching = true });
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        var compressor = new ZstdCompressor(new ZstdCompressionOptions { Level = 22, LongDistanceMatching = false });
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), "long/" + TurnedOnSize + "/L22/ldm=off");
        Assert.False(expected.AsSpan().SequenceEqual(TurnedOnFrame.Value), "the matcher should change this frame");
    }

    /// <summary>
    /// The same with a raw-content dictionary, which the frame loads (64 MiB is over six times its
    /// size), and into the matcher too: the dictionary's stretches in the source are found there.
    /// </summary>
    [Fact]
    public void Turns_itself_on_with_a_dictionary_like_libzstd()
    {
        const int Size = TurnedOnSize;
        (byte[] dictionary, _) = Dictionaries.Get("walk64", "raw");
        Assert.True(LongDistanceMatcher.EnabledFor(CompressionParameters.ForFrame(22, Size, dictionary.Length, attached: false)));

        byte[] data = FarRepeats.WithStretches(FarRepeats.Long(Size), dictionary);
        using NativeZstdDictionary native = NativeZstdDictionary.Create(dictionary, 22);
        byte[] expected = NativeZstd.Compress(data, new NativeZstdOptions { Dictionary = native });
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, new ZstdCompressor(22, dictionary).Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), "long/" + Size + "/L22/dict=raw");

        byte[] decoded = new byte[data.Length];
        Assert.Equal(OperationStatus.Done, new ZstdDecompressor(dictionary).Decompress(output.AsSpan(0, written), decoded, out _, out int decodedSize));
        Corpus.AssertSameBytes(data, decoded.AsSpan(0, decodedSize), "long/" + Size + "/L22/dict=raw");
    }

    /// <summary>
    /// Every level with every kind, the job size and the dictionary in turn: each pair of a level, a
    /// kind, a job size and a dictionary meets in some case (a covering array of strength 2), where the
    /// whole product would be six times the cases.
    /// </summary>
    public static TheoryData<string> JobCases()
    {
        var data = new TheoryData<string>();
        int[] jobSizes = [0, 512 << 10];
        string[] dictionaries = ["none", "raw", "trained"];
        for (int l = 0; l < Levels.Length; l++)
        {
            for (int k = 0; k < FarRepeats.Kinds.Length; k++)
            {
                int jobSize = jobSizes[(k + l) % jobSizes.Length];
                string dictionary = dictionaries[(k + l) % dictionaries.Length];
                data.Add(string.Create(CultureInfo.InvariantCulture, $"{FarRepeats.Kinds[k]}/{Levels[l]}/{jobSize}/{dictionary}"));
            }
        }

        return data;
    }

    /// <summary>
    /// libzstd with workers runs one matcher for the frame, the jobs' sections in order by chunks of
    /// 1 MiB, and gives each job its sequences, which its blocks consume in turn: against libzstd built
    /// with threads, one job of the default size, or many small ones; a dictionary, which the first
    /// job takes, never reaches the matcher.
    /// </summary>
    [Theory]
    [MemberData(nameof(JobCases))]
    public async Task Compresses_in_jobs_like_libzstd_with_workers(string name)
    {
        LibzstdMt? libzstd = LibzstdMt.Instance;
        Assert.SkipWhen(libzstd is null, "libzstd with threads is not built: tools/native-ref/build.sh mt");
        string[] parts = name.Split('/');
        int level = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int jobSize = int.Parse(parts[2], CultureInfo.InvariantCulture);
        byte[]? dictionary = parts[3] == "none" ? null : Dictionaries.Get(FarRepeats.Content(parts[0]), parts[3]).Bytes;
        byte[] data = dictionary is null ? FarRepeats.Generate(parts[0], 3_000_000) : FarRepeats.Generate(parts[0], 3_000_000, dictionary);
        byte[] expected = libzstd!.Compress(data, level, workers: 2, longDistance: true, dictionary: dictionary, jobSize: jobSize);

        ZstdCompressor compressor = dictionary is null ? new ZstdCompressor(level) : new ZstdCompressor(level, dictionary);
        compressor.LongDistanceMatching = true;
        compressor.JobSizeOverride = jobSize;
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.CompressInJobs(data, output, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);
        byte[] decoded = new byte[data.Length];
        var decoder = dictionary is null ? new ZstdDecompressor() : new ZstdDecompressor(dictionary);
        Assert.Equal(OperationStatus.Done, decoder.Decompress(output.AsSpan(0, written), decoded, out _, out int decodedSize));
        Corpus.AssertSameBytes(data, decoded.AsSpan(0, decodedSize), name);

        Array.Clear(output);
        int parallel = await compressor.CompressAsync(data, output, 3, TestContext.Current.CancellationToken);
        Corpus.AssertSameBytes(expected, output.AsSpan(0, parallel), name + " in parallel");
    }

    /// <summary>
    /// Level 22 over 64 MiB with workers: the matcher turns itself on, and its jobs are sized by the
    /// match finder's cycle (512 MiB here), so the frame is one job, its sequences made by chunks of
    /// 1 MiB: not the frame without workers.
    /// </summary>
    [Fact]
    public async Task Turns_itself_on_in_jobs_like_libzstd_with_workers()
    {
        LibzstdMt? libzstd = LibzstdMt.Instance;
        Assert.SkipWhen(libzstd is null, "libzstd with threads is not built: tools/native-ref/build.sh mt");
        byte[] data = TurnedOnSource.Value;
        byte[] expected = libzstd!.Compress(data, 22, workers: 2);
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        int written = await new ZstdCompressor(22).CompressAsync(data, output, 4, TestContext.Current.CancellationToken);
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), "long/" + TurnedOnSize + "/L22 in jobs");
    }

    private static readonly ConcurrentDictionary<(string, int, int, bool), Lazy<byte[]>> Frames = new();

    /// <summary>
    /// libzstd's frame of the kind at the size, with its long-distance matcher asked for or left to
    /// libzstd: made once, for the cases and for <see cref="The_matcher_changes_the_frames"/>.
    /// </summary>
    private static byte[] Libzstd(string kind, int size, int level, bool longDistance) =>
        Frames.GetOrAdd(
            (kind, size, level, longDistance),
            key => new Lazy<byte[]>(() => NativeZstd.Compress(
                FarRepeats.Generate(key.Item1, key.Item2),
                new NativeZstdOptions { Quality = key.Item3, EnableLongDistanceMatching = key.Item4 }))).Value;

    /// <summary>
    /// Sources whose matches lie far back and run long: copies of a stretch of content, slightly edited,
    /// between other content. The binary trees skip positions inside long matches, which the
    /// long-distance matcher's table still holds.
    /// </summary>
    internal static class FarRepeats
    {
        public static readonly string[] Kinds = ["far-text", "far-json", "far-random", "far-walk64"];

        /// <summary>The kind of content a far-repeat kind repeats: its dictionaries' kind too.</summary>
        public static string Content(string kind) => kind.Substring(4) == "random" ? "random64" : kind.Substring(4);

        /// <summary>
        /// <see cref="Generate(string, int)"/>, with stretches of <paramref name="dictionary"/>, a few
        /// kilobytes each, written over it every 200 KB or so.
        /// </summary>
        public static byte[] Generate(string kind, int size, byte[] dictionary) => WithStretches(Generate(kind, size), dictionary);

        /// <summary>Stretches of <paramref name="dictionary"/>, a few kilobytes each, written over <paramref name="data"/> every 200 KB or so.</summary>
        public static byte[] WithStretches(byte[] data, byte[] dictionary)
        {
            int size = data.Length;
            int stretches = Math.Max(1, size / 200_000);
            int length = Math.Min(Math.Min(4096, dictionary.Length / 2), size / 3);
            for (int i = 0; i < stretches; i++)
            {
                int position = (int)((long)size * ((2 * i) + 1) / (2 * stretches));
                int start = (i * 7919) % (dictionary.Length - length);
                dictionary.AsSpan(start, Math.Min(length, size - position)).CopyTo(data.AsSpan(position));
            }

            return data;
        }

        public static byte[] Generate(string kind, int size)
        {
            if (!kind.StartsWith("far-", StringComparison.Ordinal))
            {
                return DataKinds.Generate(kind, size);
            }

            string content = Content(kind);
            var random = new Random(size ^ kind.Length);
            byte[] stretch = DataKinds.Generate(content, size / 6, seed: 2);
            byte[] data = new byte[size];
            int position = 0;
            int copy = 0;
            while (position < size)
            {
                // Other content, then a copy of the stretch with an edit every few kilobytes.
                byte[] filler = DataKinds.Generate("text", random.Next(1_000, 100_000), seed: 3 + copy);
                int length = Math.Min(filler.Length, size - position);
                filler.AsSpan(0, length).CopyTo(data.AsSpan(position));
                position += length;

                length = Math.Min(stretch.Length, size - position);
                stretch.AsSpan(0, length).CopyTo(data.AsSpan(position));
                for (int edit = random.Next(500, 5_000); edit < length; edit += random.Next(500, 5_000))
                {
                    data[position + edit] = (byte)random.Next(256);
                }

                position += length;
                copy++;
            }

            return data;
        }

        /// <summary>
        /// Tens of megabytes cheaply: copies of a 2 MiB stretch of walk64 with an edit every 32 to 64 KB,
        /// between other content. The matches run tens of kilobytes, which the binary trees skip
        /// through, where <see cref="Generate(string, int)"/>'s edits every few kilobytes keep them
        /// searching: level 22 takes a quarter of the time over 64 MiB, and the matcher still changes
        /// its frames.
        /// </summary>
        public static byte[] Long(int size)
        {
            var random = new Random(7);
            byte[] stretch = DataKinds.Generate("walk64", 2 << 20, seed: 2);
            byte[] data = new byte[size];
            int position = 0;
            int copy = 0;
            while (position < size)
            {
                byte[] filler = DataKinds.Generate("text", random.Next(1_000, 100_000), seed: 3 + copy);
                int length = Math.Min(filler.Length, size - position);
                filler.AsSpan(0, length).CopyTo(data.AsSpan(position));
                position += length;

                length = Math.Min(stretch.Length, size - position);
                stretch.AsSpan(0, length).CopyTo(data.AsSpan(position));
                for (int edit = random.Next(32 << 10, 64 << 10); edit < length; edit += random.Next(32 << 10, 64 << 10))
                {
                    data[position + edit] = (byte)random.Next(256);
                }

                position += length;
                copy++;
            }

            return data;
        }
    }
}
