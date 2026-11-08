using System;
using System.Buffers;
using System.Threading.Tasks;
using ManagedCompressions.Zstd.Tests.Differential;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Compression;

/// <summary>
/// <see cref="ZstdCompressionOptions"/>: libzstd's parameters beyond the level, each against
/// <c>ZSTD_compress2</c> with the same parameter set (<c>ZSTD_CCtx_setParameter</c>), byte for byte, and
/// every frame decoded back.
/// </summary>
public sealed class CompressionOptionsTests
{
    public static TheoryData<int> Levels() => [-5, 1, 2, 3, 4, 5, 6, 7, 9, 12, 13, 16, 19, 22];

    /// <summary>
    /// The window asked for, from the smallest to the largest, over sources that libzstd's adjustments
    /// shrink it to and sources that outgrow it: the window, the tables fitted to it, the frame header.
    /// </summary>
    [Theory]
    [MemberData(nameof(Levels))]
    public void The_window_asked_for_gives_libzstds_frames(int level)
    {
        int[] sizes = level >= 16 ? [0, 1000, 70_000, 300_000] : [0, 1000, 70_000, 600_000];
        int variant = 0;
        foreach (int windowLog in (int[])[10, 13, 17, 20, 24, 31])
        {
            foreach (int size in sizes)
            {
                string kind = variant++ % 2 == 0 ? "text" : "walk64";
                byte[] data = DataKinds.Generate(kind, size, windowLog);
                AssertLibzstds(
                    data,
                    new ZstdCompressionOptions { Level = level, WindowLog = windowLog },
                    new NativeZstdOptions { Quality = level, WindowLog = windowLog },
                    null,
                    $"{kind}/{size}/L{level}/wlog={windowLog}");
            }
        }
    }

    public static TheoryData<int> DictionaryLevels() => [1, 3, 5, 9, 13, 19];

    /// <summary>
    /// The window asked for with a prepared dictionary: attached for small sources, its tables copied
    /// or the dictionary loaded for larger ones, the frame's window kept in each case.
    /// </summary>
    [Theory]
    [MemberData(nameof(DictionaryLevels))]
    public void The_window_asked_for_with_a_dictionary(int level)
    {
        (byte[] dictionary, _) = Dictionaries.Get("json", "trained");
        using NativeZstdDictionary prepared = NativeZstdDictionary.Create(dictionary, level);
        foreach (int windowLog in (int[])[10, 14, 17, 21])
        {
            foreach (int size in (int[])[100, 5000, 50_000, 300_000])
            {
                byte[] data = DataKinds.Generate("json", size, windowLog);
                AssertLibzstds(
                    data,
                    new ZstdCompressionOptions { Level = level, WindowLog = windowLog, Dictionary = dictionary },
                    new NativeZstdOptions { WindowLog = windowLog, Dictionary = prepared },
                    dictionary,
                    $"json/{size}/L{level}/wlog={windowLog}/dict");
            }
        }
    }

    /// <summary>
    /// Headers without the content size, which then hold the window, and without the dictionary's
    /// identifier, at the sizes where the content size's field changes length.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(19)]
    public void Header_flags_give_libzstds_headers(int level)
    {
        (byte[] dictionary, _) = Dictionaries.Get("text", "trained");
        using NativeZstdDictionary prepared = NativeZstdDictionary.Create(dictionary, level);
        foreach (bool contentSize in (bool[])[true, false])
        {
            foreach (bool dictionaryId in (bool[])[true, false])
            {
                foreach (bool withDictionary in (bool[])[false, true])
                {
                    foreach (int size in (int[])[0, 1, 255, 256, 65791, 65792, 300_000])
                    {
                        byte[] data = DataKinds.Generate("text", size, size);
                        AssertLibzstds(
                            data,
                            new ZstdCompressionOptions
                            {
                                Level = level, WriteContentSize = contentSize, WriteDictionaryId = dictionaryId, AppendChecksum = size % 2 == 0,
                                Dictionary = withDictionary ? dictionary : default,
                            },
                            new NativeZstdOptions
                            {
                                Quality = level, ContentSizeFlag = contentSize, DictionaryIdFlag = dictionaryId, AppendChecksum = size % 2 == 0,
                                Dictionary = withDictionary ? prepared : null,
                            },
                            withDictionary ? dictionary : null,
                            $"text/{size}/L{level}/fcs={contentSize}/dictid={dictionaryId}/dict={withDictionary}");
                    }
                }
            }
        }
    }

    /// <summary>The options at their defaults are the plain constructors: the same frames.</summary>
    [Fact]
    public void Defaults_are_the_plain_constructors()
    {
        (byte[] dictionary, _) = Dictionaries.Get("json", "trained");
        byte[] data = DataKinds.Generate("json", 200_000, 3);
        foreach (int level in (int[])[-3, 3, 9, 19])
        {
            Assert.Equal(
                Compress(new ZstdCompressor(level) { AppendChecksum = true }, data),
                Compress(new ZstdCompressor(new ZstdCompressionOptions { Level = level, AppendChecksum = true }), data));
            Assert.Equal(
                Compress(new ZstdCompressor(level, dictionary), data),
                Compress(new ZstdCompressor(new ZstdCompressionOptions { Level = level, Dictionary = dictionary }), data));
        }
    }

    [Fact]
    public void Options_out_of_range_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdCompressionOptions { Level = ZstdCompressor.MaxLevel + 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdCompressionOptions { Level = ZstdCompressor.MinLevel - 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdCompressionOptions { WindowLog = ZstdCompressionOptions.MinWindowLog - 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdCompressionOptions { WindowLog = ZstdCompressionOptions.MaxWindowLog + 1 });
        Assert.Equal(0, new ZstdCompressionOptions { WindowLog = 0 }.WindowLog);
        Assert.Throws<ArgumentNullException>(() => new ZstdCompressor((ZstdCompressionOptions)null!));

        (byte[] dictionary, _) = Dictionaries.Get("json", "trained");
        Assert.Throws<ArgumentException>(() => new ZstdCompressor(
            new ZstdCompressionOptions { Dictionary = dictionary, Experimental = ZstdExperimentalOptions.EntropyAwareMatches }));

        // The long-distance matcher is implemented for the optimal parsers only.
        var low = new ZstdCompressor(new ZstdCompressionOptions { Level = 3, LongDistanceMatching = true });
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(1000)];
        Assert.Throws<NotSupportedException>(() => low.Compress(new byte[1000], output, out _, out _));
    }

    public static TheoryData<int, int> JobWindows() => new()
    {
        { 1, 18 },
        { 1, 22 },
        { 3, 19 },
        { 3, 23 },
        { 19, 20 },
    };

    /// <summary>
    /// The window asked for in jobs on the thread pool: zstdmt's job size follows the window, and the
    /// frame is the one libzstd writes with workers and the same window.
    /// </summary>
    [Theory]
    [MemberData(nameof(JobWindows))]
    public async Task The_window_asked_for_in_jobs_gives_zstdmts_frames(int level, int windowLog)
    {
        LibzstdMt? libzstd = LibzstdMt.Instance;
        Assert.SkipWhen(libzstd is null, "libzstd with threads is not built: tools/native-ref/build.sh mt");
        byte[] data = DataKinds.Generate("mixed", level >= 19 ? 3 << 20 : 9 << 20, windowLog);
        byte[] expected = libzstd!.Compress(data, level, workers: 2, windowLog: windowLog);
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        var compressor = new ZstdCompressor(new ZstdCompressionOptions { Level = level, WindowLog = windowLog });
        int written = await compressor.CompressAsync(data, output, 4, TestContext.Current.CancellationToken);
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), $"mixed/{data.Length}/L{level}/wlog={windowLog} in jobs");
    }

    private static void AssertLibzstds(byte[] data, ZstdCompressionOptions options, NativeZstdOptions native, byte[]? dictionary, string name)
    {
        byte[] expected = NativeZstd.Compress(data, native);
        byte[] frame = Compress(new ZstdCompressor(options), data);
        Corpus.AssertSameBytes(expected, frame, name);

        // The streaming decoder takes frames with and without a content size alike.
        var decoder = dictionary is null ? new ZstdDecoder() : new ZstdDecoder(dictionary);
        byte[] decoded = new byte[data.Length + 1];
        OperationStatus status = decoder.Decompress(frame, decoded, out int consumed, out int written);
        Assert.True(status == OperationStatus.Done && consumed == frame.Length, $"{name}: {status} ({decoder.LastError})");
        Corpus.AssertSameBytes(data, decoded.AsSpan(0, written), name + " decoded");
    }

    private static byte[] Compress(ZstdCompressor compressor, byte[] data)
    {
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out int consumed, out int written));
        Assert.Equal(data.Length, consumed);
        return output.AsSpan(0, written).ToArray();
    }
}
