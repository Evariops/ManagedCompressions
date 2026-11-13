using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using ManagedCompressions.Zstd.Tests.Differential;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Compression;

/// <summary>
/// <see cref="ZstdDictionary"/>: a dictionary prepared once, which every codec takes as it is. Its frames
/// are those of the same bytes given to each codec, libzstd's; its codecs share it across threads.
/// </summary>
public sealed class PreparedDictionaryTests
{
    public static MatrixTheoryData<string, int> TypesAndLevels() => new(["trained", "raw"], [-1, 1, 3, 5, 9, 13, 16, 19]);

    [Theory]
    [MemberData(nameof(TypesAndLevels))]
    public void Writes_and_reads_the_frames_of_its_bytes(string type, int level)
    {
        (byte[] bytes, _) = Dictionaries.Get("json", type);
        ZstdDictionary prepared = ZstdDictionary.Create(bytes, level);
        Assert.Equal(level, prepared.Level);
        Assert.Equal(bytes, prepared.Data.ToArray());

        var fromBytes = new ZstdCompressor(level, bytes);
        var fromPrepared = new ZstdCompressor(prepared);
        var fromOptions = new ZstdCompressor(new ZstdCompressionOptions { Level = 7, PreparedDictionary = prepared, AppendChecksum = true });
        var checksummed = new ZstdCompressor(new ZstdCompressionOptions { Level = level, Dictionary = bytes, AppendChecksum = true });
        Assert.Equal(level, fromOptions.Level);

        // Attached, copied and loaded: the three ways a frame uses the dictionary, by its size.
        foreach (int size in (int[])[100, 5000, 40_000, 140_000, 900_000])
        {
            byte[] data = DataKinds.Generate("json", size, level + size);
            byte[] expected = Compress(fromBytes, data);
            Corpus.AssertSameBytes(expected, Compress(fromPrepared, data), $"{type} L{level} {size}");
            Corpus.AssertSameBytes(Compress(checksummed, data), Compress(fromOptions, data), $"{type} L{level} {size} options");

            byte[] decoded = new byte[data.Length];
            Assert.Equal(OperationStatus.Done, new ZstdDecompressor(prepared).Decompress(expected, decoded, out _, out int written));
            Corpus.AssertSameBytes(data, decoded.AsSpan(0, written), $"{type} L{level} {size} decoded");

            // Streams: the encoder and the decoder, by their own constructors and by the options.
            byte[] streamed = Stream(data, s => new ZstdStream(s, new ZstdEncoder(prepared), leaveOpen: true));
            Corpus.AssertSameBytes(Stream(data, s => new ZstdStream(s, new ZstdEncoder(new ZstdCompressionOptions { Level = level, Dictionary = bytes }), leaveOpen: true)), streamed, $"{type} L{level} {size} stream");
            Assert.Equal(streamed, Stream(data, s => new ZstdStream(s, new ZstdCompressionOptions { PreparedDictionary = prepared }, leaveOpen: true)));
            Assert.Equal(data, Read(streamed, s => new ZstdStream(s, new ZstdDecoder(prepared))));
            Assert.Equal(data, Read(streamed, s => new ZstdStream(s, new ZstdDecompressionOptions { PreparedDictionary = prepared })));
        }
    }

    /// <summary>
    /// Compressors and decompressors on many threads at once, one dictionary between them, its
    /// compressor's tables prepared by whichever thread comes first: every frame the expected one.
    /// </summary>
    [Fact]
    public void One_dictionary_serves_many_threads_at_once()
    {
        (byte[] bytes, _) = Dictionaries.Get("text", "trained");
        byte[][] data = new byte[64][];
        byte[][] expected = new byte[data.Length][];
        var reference = new ZstdCompressor(5, bytes);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = DataKinds.Generate(i % 2 == 0 ? "text" : "json", 2000 + (i * 997), i);
            expected[i] = Compress(reference, data[i]);
        }

        ZstdDictionary prepared = ZstdDictionary.Create(bytes, 5);
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, thread =>
        {
            var compressor = new ZstdCompressor(prepared);
            var decompressor = new ZstdDecompressor(prepared);
            for (int round = 0; round < 8; round++)
            {
                for (int i = (thread + round) % data.Length; i < data.Length; i += 7)
                {
                    byte[] frame = Compress(compressor, data[i]);
                    Assert.Equal(expected[i], frame);
                    byte[] decoded = new byte[data[i].Length];
                    Assert.Equal(OperationStatus.Done, decompressor.Decompress(frame, decoded, out _, out _));
                    Assert.Equal(data[i], decoded);
                }
            }
        });
    }

    [Fact]
    public void It_is_given_once_and_valid()
    {
        (byte[] bytes, _) = Dictionaries.Get("json", "trained");
        ZstdDictionary prepared = ZstdDictionary.Create(bytes);
        Assert.Equal(ZstdCompressor.DefaultLevel, prepared.Level);
        Assert.NotEqual(0u, prepared.Id);
        Assert.Equal(0u, ZstdDictionary.Create(DataKinds.Generate("text", 1000, 1)).Id);

        Assert.Throws<ArgumentException>(() => ZstdDictionary.Create([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstdDictionary.Create(bytes, ZstdCompressor.MaxLevel + 1));
        byte[] corrupted = (byte[])bytes.Clone();
        corrupted.AsSpan(8, 40).Fill(0xFF);
        Assert.Throws<InvalidDataException>(() => ZstdDictionary.Create(corrupted));

        // Given both as bytes and prepared: refused.
        Assert.Throws<ArgumentException>(() => new ZstdCompressor(new ZstdCompressionOptions { Dictionary = bytes, PreparedDictionary = prepared }));
        Assert.Throws<ArgumentException>(() => new ZstdDecoder(new ZstdDecompressionOptions { Dictionary = bytes, PreparedDictionary = prepared }));
        Assert.Throws<ArgumentException>(() => new ZstdStream(new MemoryStream(), new ZstdDecompressionOptions { Dictionary = bytes, PreparedDictionary = prepared }));
        Assert.Throws<ArgumentException>(() => new ZstdCompressor(new ZstdCompressionOptions { PreparedDictionary = prepared, Experimental = ZstdExperimentalOptions.EntropyAwareMatches }));
        Assert.Throws<ArgumentNullException>(() => new ZstdCompressor((ZstdDictionary)null!));
        Assert.Throws<ArgumentNullException>(() => new ZstdDecompressor((ZstdDictionary)null!));
    }

    private static byte[] Compress(ZstdCompressor compressor, byte[] data)
    {
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        return output.AsSpan(0, written).ToArray();
    }

    private static byte[] Stream(byte[] data, Func<Stream, ZstdStream> open)
    {
        var output = new MemoryStream();
        using (ZstdStream zstd = open(output))
        {
            zstd.Write(data, 0, data.Length / 2);
            zstd.Write(data, data.Length / 2, data.Length - (data.Length / 2));
        }

        return output.ToArray();
    }

    private static byte[] Read(byte[] frames, Func<Stream, ZstdStream> open)
    {
        using ZstdStream zstd = open(new MemoryStream(frames));
        var content = new MemoryStream();
        zstd.CopyTo(content);
        return content.ToArray();
    }
}
