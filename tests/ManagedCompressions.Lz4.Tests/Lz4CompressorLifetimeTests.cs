using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace ManagedCompressions.Lz4.Tests;

/// <summary>
/// A compressor that nothing holds but the call it was created for: the call must keep it, its tables
/// and its state alive while it runs, or a full collection during the loop frees the pinned arrays it
/// writes through pointers, and another array gets their memory.
/// </summary>
/// <remarks>
/// Threads compress with compressors made for each call, while another thread forces blocking gen 2
/// collections and allocates pinned arrays over the memory they free. A compressor collected too early
/// writes into those arrays and corrupts the heap: the outputs differ, or the process fails. The
/// helper is optimized at once, so that its own frame does not keep the compressor alive (unoptimized
/// code reports every local live to the end of the method), and the library is warmed up first, to
/// its optimized code: only there does the JIT end a reference's life at its last use. A library built
/// without optimizations keeps everything alive, and the test then has nothing to find.
/// </remarks>
public sealed class Lz4CompressorLifetimeTests
{
    [Theory]
    [InlineData(1, 1 << 20)]
    [InlineData(1, 60_000)]
    [InlineData(-3, 1 << 20)]
    [InlineData(4, 1 << 20)]
    public void ACompressorNothingHoldsLivesThroughItsCall(int level, int size)
    {
        Random random = new(size + level);
        byte[] input = Lz4BlockTests.Shaped(random, size, 2);
        byte[] expected = new byte[Lz4Compressor.GetMaxCompressedLength(size)];
        int expectedLength = new Lz4Compressor(level).Compress(input, expected);

        // Every method on the way at its optimized tier: 30 calls, then the background compilation.
        byte[] small = input.AsSpan(0, Math.Min(size, 8192)).ToArray();
        byte[] smallOut = new byte[Lz4Compressor.GetMaxCompressedLength(small.Length)];
        for (int i = 0; i < 200; i++)
        {
            Compress(level, small, smallOut);
            if (i % 50 == 49)
            {
                Thread.Sleep(100);
            }
        }

        bool stop = false;
        long mismatches = 0;
        long calls = 0;
        var workers = new Thread[3];
        for (int t = 0; t < workers.Length; t++)
        {
            workers[t] = new Thread(() =>
            {
                byte[] output = new byte[expected.Length];
                while (!Volatile.Read(ref stop))
                {
                    int written = Compress(level, input, output);
                    if (written != expectedLength || !output.AsSpan(0, written).SequenceEqual(expected.AsSpan(0, expectedLength)))
                    {
                        Interlocked.Increment(ref mismatches);
                    }

                    Interlocked.Increment(ref calls);
                }
            });
            workers[t].Start();
        }

        var collector = new Thread(() =>
        {
            byte[][] kept = new byte[256][];
            int k = 0;
            while (!Volatile.Read(ref stop))
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                for (int i = 0; i < 32; i++)
                {
                    byte[] array = GC.AllocateUninitializedArray<byte>(i % 2 == 0 ? 64 : 16384, pinned: true);
                    array.AsSpan().Fill(0x41);
                    kept[k++ & 255] = array;
                }
            }
        });
        collector.Start();

        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 600 && Interlocked.Read(ref mismatches) == 0)
        {
            Thread.Sleep(20);
        }

        Volatile.Write(ref stop, true);
        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        collector.Join();
        Assert.True(Interlocked.Read(ref calls) > 0);
        Assert.Equal(0, Interlocked.Read(ref mismatches));
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int Compress(int level, byte[] source, byte[] destination) => new Lz4Compressor(level).Compress(source, destination);
}
