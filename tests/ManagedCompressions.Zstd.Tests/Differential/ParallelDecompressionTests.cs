using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ManagedCompressions.Zstd.Tests.Fuzz;
using ManagedCompressions.Zstd.Tests.Support;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Differential;

/// <summary>
/// <see cref="ZstdDecompressor.DecompressAsync"/>: sources of several frames, libzstd's, decoded on several
/// threads into the contents one after the other; a large frame's checksum verified on a second thread;
/// and on invalid data the error of the frames decoded one after the other with
/// <see cref="ZstdDecompressor.Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/>, the
/// first in the source's order.
/// </summary>
public sealed class ParallelDecompressionTests
{
    private static readonly int[] Degrees = [1, 2, 3, 8, -1];

    public static TheoryData<string> Sources() =>
    [
        "frames", "frames-checksum", "frames-unsized", "frames-skippable", "one-frame", "one-large-frame-checksum",
        "one-large-frame-unsized-checksum", "frames-mixed", "empty-frames", "many-small-frames",
    ];

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task Decodes_every_frame_in_order(string name)
    {
        (byte[] source, byte[] content) = Build(name, seed: 1);
        foreach (int degree in Degrees)
        {
            byte[] destination = new byte[content.Length + 17];
            var decompressor = new ZstdDecompressor();
            int written = await decompressor.DecompressAsync(source, destination, degree, TestContext.Current.CancellationToken);
            Assert.Equal(content.Length, written);
            Assert.True(destination.AsSpan(0, written).SequenceEqual(content), $"degree {degree}");
            Assert.Equal(ZstdError.None, decompressor.LastError);

            // A second call on the same decompressor, its workers and checksum helper reused.
            Array.Clear(destination);
            written = await decompressor.DecompressAsync(source, destination, degree, TestContext.Current.CancellationToken);
            Assert.True(destination.AsSpan(0, written).SequenceEqual(content), $"degree {degree}, again");
        }
    }

    [Fact]
    public async Task An_exact_destination_is_enough()
    {
        (byte[] source, byte[] content) = Build("frames", seed: 2);
        byte[] destination = new byte[content.Length];
        int written = await new ZstdDecompressor().DecompressAsync(source, destination, 4, TestContext.Current.CancellationToken);
        Assert.Equal(content.Length, written);
        Assert.True(destination.AsSpan().SequenceEqual(content));
    }

    [Theory]
    [InlineData("frames", 1)]
    [InlineData("frames", 4)]
    [InlineData("one-large-frame-checksum", 4)]
    public async Task A_destination_too_small_is_an_argument_error(string name, int degree)
    {
        (byte[] source, byte[] content) = Build(name, seed: 3);
        var decompressor = new ZstdDecompressor();
        await Assert.ThrowsAsync<ArgumentException>(() => decompressor.DecompressAsync(source, new byte[content.Length - 1], degree, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(ZstdError.DestinationTooSmall, decompressor.LastError);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task A_wrong_checksum_is_found_by_the_helper(int degree)
    {
        (byte[] source, byte[] content) = Build("one-large-frame-checksum", seed: 4);
        source[^1] ^= 0x40;
        var decompressor = new ZstdDecompressor();
        await Assert.ThrowsAsync<InvalidDataException>(() => decompressor.DecompressAsync(source, new byte[content.Length], degree, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(ZstdError.ChecksumMismatch, decompressor.LastError);
    }

    [Fact]
    public async Task A_block_that_fails_midway_stops_the_helper()
    {
        (byte[] source, byte[] content) = Build("one-large-frame-checksum", seed: 5);

        // A byte flipped in every 64 KiB of the frame's second half: some block fails there, while the
        // helper hashes the first half.
        for (int at = source.Length / 2; at < source.Length - 4; at += 64 << 10)
        {
            source[at] ^= 0x5A;
        }

        byte[] destination = new byte[content.Length];
        var decompressor = new ZstdDecompressor();
        ZstdError expected = Sequential(source, new byte[content.Length]).Error;
        await Assert.ThrowsAnyAsync<Exception>(() => decompressor.DecompressAsync(source, destination, 4, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(expected, decompressor.LastError);

        // The decompressor and its helper serve the next call.
        (byte[] good, byte[] goodContent) = Build("one-large-frame-checksum", seed: 6);
        byte[] output = new byte[goodContent.Length];
        Assert.Equal(goodContent.Length, await decompressor.DecompressAsync(good, output, 4, TestContext.Current.CancellationToken));
        Assert.True(output.AsSpan().SequenceEqual(goodContent));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task Mutated_sources_fail_as_the_frames_decoded_in_order(int degree)
    {
        var random = new Random(7 + degree);
        for (int i = 0; i < 120; i++)
        {
            (byte[] source, byte[] content) = Build(i % 2 == 0 ? "frames" : "frames-skippable", seed: 100 + i);
            var mutator = new FrameMutator(random, [source]);

            // A frame of the source mutated in place, or the source cut short.
            byte[] mutated = i % 5 == 0 ? source.AsSpan(0, random.Next(source.Length)).ToArray() : MutateOneFrame(source, mutator, random);
            byte[] sequentialOutput = new byte[content.Length + 64];
            (ZstdError error, int expectedWritten) = Sequential(mutated, sequentialOutput);
            byte[] destination = new byte[content.Length + 64];
            var decompressor = new ZstdDecompressor();
            if (error == ZstdError.None)
            {
                int written = await decompressor.DecompressAsync(mutated, destination, degree, TestContext.Current.CancellationToken);
                Assert.Equal(expectedWritten, written);
                Assert.True(destination.AsSpan(0, written).SequenceEqual(sequentialOutput.AsSpan(0, expectedWritten)), $"case {i}");
            }
            else
            {
                Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => decompressor.DecompressAsync(mutated, destination, degree, TestContext.Current.CancellationToken).AsTask());
                Assert.True(thrown is InvalidDataException or ArgumentException, thrown.GetType().Name);
                Assert.Equal(error, decompressor.LastError);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Frames_with_a_dictionary_decode_with_it(int degree)
    {
        byte[] dictionary = DataKinds.Generate("json", 32 << 10, seed: 9);
        var content = new MemoryStream();
        var source = new MemoryStream();
        var compressor = new ZstdCompressor(3, dictionary);
        for (int i = 0; i < 12; i++)
        {
            byte[] part = DataKinds.Generate("json", 200_000 + (i * 1000), seed: 20 + i);
            content.Write(part);
            byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(part.Length)];
            Assert.Equal(OperationStatus.Done, compressor.Compress(part, frame, out _, out int size));
            source.Write(frame, 0, size);
        }

        foreach (ZstdDecompressor decompressor in new[] { new ZstdDecompressor(dictionary), new ZstdDecompressor(ZstdDictionary.Create(dictionary)) })
        {
            byte[] destination = new byte[content.Length];
            int written = await decompressor.DecompressAsync(source.ToArray(), destination, degree, TestContext.Current.CancellationToken);
            Assert.Equal(content.Length, written);
            Assert.True(destination.AsSpan().SequenceEqual(content.ToArray()));
        }
    }

    [Fact]
    public async Task A_cancelled_call_throws()
    {
        (byte[] source, byte[] content) = Build("frames", seed: 10);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ZstdDecompressor().DecompressAsync(source, new byte[content.Length], 4, cancelled.Token).AsTask());
    }

    /// <summary>A source of frames written by libzstd, and the contents they hold, one after the other.</summary>
    private static (byte[] Source, byte[] Content) Build(string name, int seed)
    {
        var source = new MemoryStream();
        var content = new MemoryStream();
        var random = new Random(seed);
        string[] kinds = ["text", "json", "urls", "repeats", "incompressible", "zeros", "walk64"];
        void Frame(int size, bool checksum, bool sized, int level = 3)
        {
            byte[] part = DataKinds.Generate(kinds[random.Next(kinds.Length)], size, seed: random.Next());
            content.Write(part);
            source.Write(NativeZstd.Compress(part, new NativeZstdOptions { Quality = level, AppendChecksum = checksum }, noContentSize: !sized));
        }

        void Skippable(int size)
        {
            byte[] frame = new byte[8 + size];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, 0x184D2A5Bu);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)size);
            random.NextBytes(frame.AsSpan(8));
            source.Write(frame);
        }

        switch (name)
        {
            case "frames":
                for (int i = 0; i < 9; i++)
                {
                    Frame(100_000 + random.Next(400_000), checksum: false, sized: true);
                }

                break;
            case "frames-checksum":
                for (int i = 0; i < 9; i++)
                {
                    Frame(100_000 + random.Next(400_000), checksum: true, sized: true, level: 1 + random.Next(9));
                }

                break;
            case "frames-unsized":
                for (int i = 0; i < 6; i++)
                {
                    Frame(100_000 + random.Next(400_000), checksum: i % 2 == 0, sized: i != 3);
                }

                break;
            case "frames-skippable":
                for (int i = 0; i < 7; i++)
                {
                    Skippable(random.Next(64));
                    Frame(50_000 + random.Next(300_000), checksum: i % 3 == 0, sized: true);
                }

                Skippable(0);
                break;
            case "one-frame":
                Frame(700_000, checksum: true, sized: true);
                break;
            case "one-large-frame-checksum":
                Frame(ZstdDecompressor.ParallelChecksumMin + 3_000_000, checksum: true, sized: true, level: 1);
                break;
            case "one-large-frame-unsized-checksum":
                Frame(ZstdDecompressor.ParallelChecksumMin + 1_000_000, checksum: true, sized: false, level: 1);
                break;
            case "frames-mixed":
                Frame(ZstdDecompressor.ParallelChecksumMin + 500_000, checksum: true, sized: true, level: 1);
                Frame(30_000, checksum: false, sized: true);
                Frame(ZstdDecompressor.ParallelChecksumMin, checksum: true, sized: false, level: 1);
                break;
            case "empty-frames":
                Frame(0, checksum: true, sized: true);
                Frame(0, checksum: false, sized: true);
                Frame(10, checksum: false, sized: true);
                break;
            case "many-small-frames":
                for (int i = 0; i < 200; i++)
                {
                    Frame(random.Next(3_000), checksum: i % 2 == 0, sized: true);
                }

                break;
        }

        return (source.ToArray(), content.ToArray());
    }

    /// <summary>The frames decoded one after the other with the one-shot call: the first error, and the bytes written before it.</summary>
    private static (ZstdError Error, int Written) Sequential(byte[] source, byte[] destination)
    {
        var decompressor = new ZstdDecompressor();
        int ip = 0;
        int op = 0;
        while (ip < source.Length)
        {
            if (decompressor.Decompress(source.AsSpan(ip), destination.AsSpan(op), out int consumed, out int written) != OperationStatus.Done)
            {
                return (decompressor.LastError, op);
            }

            ip += consumed;
            op += written;
        }

        return (ZstdError.None, op);
    }

    /// <summary>The source with one of its frames mutated, the others as they were.</summary>
    private static byte[] MutateOneFrame(byte[] source, FrameMutator mutator, Random random)
    {
        var frames = new List<byte[]>();
        int ip = 0;
        var decompressor = new ZstdDecompressor();
        byte[] scratch = new byte[8 << 20];
        while (ip < source.Length && decompressor.Decompress(source.AsSpan(ip), scratch, out int consumed, out _) == OperationStatus.Done)
        {
            frames.Add(source.AsSpan(ip, consumed).ToArray());
            ip += consumed;
        }

        int victim = random.Next(frames.Count);
        frames[victim] = mutator.Mutate(frames[victim]);
        var result = new MemoryStream();
        foreach (byte[] frame in frames)
        {
            result.Write(frame);
        }

        return result.ToArray();
    }
}
