using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>Compression in parallel jobs, awaited: the frames of libzstd with workers.</summary>
public sealed partial class ZstdCompressor
{
    /// <summary>
    /// Compresses <paramref name="source"/> into one frame at the start of <paramref name="destination"/>,
    /// as libzstd does with workers (<c>ZSTD_c_nbWorkers</c> of 1 or more): the source cut into jobs of
    /// four windows (1 MiB to 1 GiB), compressed on the thread pool, up to
    /// <paramref name="maxDegreeOfParallelism"/> at a time, by compressors this one keeps for them.
    /// </summary>
    /// <param name="source">The content of the frame.</param>
    /// <param name="destination">Where the frame is written; <see cref="GetMaxCompressedLength"/> bytes always suffice.</param>
    /// <param name="maxDegreeOfParallelism">The most jobs compressed at once; 0 or less for <see cref="Environment.ProcessorCount"/>.</param>
    /// <param name="cancellationToken">Stops the jobs not started yet; the frame is then abandoned.</param>
    /// <returns>The size of the frame.</returns>
    /// <remarks>
    /// The frame is the one libzstd writes whatever the number of its workers, and so whatever the
    /// degree of parallelism; up to 512 KiB, it is the one <see cref="Compress"/> writes (libzstd runs
    /// no workers for it). The source and the destination must not change until the task completes,
    /// and the compressor must not be used meanwhile.
    /// </remarks>
    /// <exception cref="ArgumentException">The frame does not fit in <paramref name="destination"/>.</exception>
    public async ValueTask<int> CompressAsync(
        ReadOnlyMemory<byte> source, Memory<byte> destination, int maxDegreeOfParallelism = -1, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JobPlan plan = PlanJobs(source.Length);
        if (plan.Count == 0)
        {
            return Compress(source.Span, destination.Span, out _, out int frame) == OperationStatus.Done
                ? frame
                : throw new ArgumentException("The frame does not fit in the destination.", nameof(destination));
        }

        int degree = Math.Min(maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount, plan.Count);
        _idleWorkers ??= new Stack<ZstdCompressor>();
        using var slots = new SemaphoreSlim(degree);
        var jobs = new Task<JobOutput>[plan.Count];
        int started = 0;
        int written = 0;
        bool fits = true;
        using MemoryHandle pin = source.Pin();
        using MemoryHandle destinationPin = destination.Pin();
        nint address = AddressOf(pin);
        var output = new JobDestination(AddressOf(destinationPin), destination.Length);
        if (plan.LongDistance)
        {
            BeginJobsMatcher(plan, address);
        }

        // zstdmt's serial state sums the content as the jobs go: here a task of its own, which reads the
        // source beside the jobs rather than after them all, on one core while the others wait.
        Task<ulong>? checksum = AppendChecksum ? Task.Run(() => XxHash64.HashToUInt64(source.Span), CancellationToken.None) : null;
        try
        {
            // Each job writes into the destination itself, at the sum of the bounds of the jobs before it
            // (its slot), and is moved down to the end of the frame so far once the jobs before it are.
            long slot = 0;
            for (int flushed = 0; flushed < plan.Count; flushed++)
            {
                // The jobs written out oldest first (zstdmt's flush), up to twice the degree of them
                // started, the degree of them running: one held back by a slow core leaves the
                // others running.
                while (started < plan.Count && started < flushed + (2 * degree))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int job = started;
                    (RawSequence[]? sequences, int count) = plan.LongDistance
                        ? await Task.Run(() => GenerateJobSequences(plan, address, job), CancellationToken.None).ConfigureAwait(false)
                        : (null, 0);
                    jobs[job] = RunJobAsync(slots, plan, address, job, sequences, count, output, slot);
                    slot += JobCapacity(plan.Size(job));
                    started++;
                }

                JobOutput result = await jobs[flushed].ConfigureAwait(false);
                fits = fits && result.Size <= destination.Length - written;
                if (fits)
                {
                    // The frame so far ends at or before the job's slot, which no later job's overlaps.
                    Span<byte> frame = destination.Span;
                    ReadOnlySpan<byte> content = result.Buffer is null ? frame.Slice(result.Offset, result.Size) : result.Buffer.AsSpan(0, result.Size);
                    if (result.Buffer is not null || result.Offset != written)
                    {
                        content.CopyTo(frame[written..]);
                    }

                    written += result.Size;
                }

                result.Return();
                jobs[flushed] = null!;
            }

            if (checksum is not null)
            {
                ulong hash = await checksum.ConfigureAwait(false);
                checksum = null;
                fits = fits && FrameFormat.ChecksumSize <= destination.Length - written;
                if (fits)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(destination.Span.Slice(written, FrameFormat.ChecksumSize), (uint)hash);
                    written += FrameFormat.ChecksumSize;
                }
            }
        }
        finally
        {
            // A job, or the checksum, left running would read the source after its pin, or write the
            // destination after its own.
            for (int job = 0; job < started; job++)
            {
                if (jobs[job] is { } running)
                {
                    try
                    {
                        (await running.ConfigureAwait(false)).Return();
                    }
                    catch (Exception)
                    {
                        // Its failure is not the one reported.
                    }
                }
            }

            if (checksum is not null)
            {
                try
                {
                    await checksum.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Nor is the checksum's.
                }
            }
        }

        return fits ? written : throw new ArgumentException("The frame does not fit in the destination.", nameof(destination));
    }

    /// <summary>
    /// A job on the thread pool once one of the <paramref name="slots"/> is free, in the order asked, its
    /// long-distance sequences (if any) returned after.
    /// </summary>
    private async Task<JobOutput> RunJobAsync(
        SemaphoreSlim slots, JobPlan plan, nint address, int job, RawSequence[]? sequences, int count, JobDestination output, long slot)
    {
        try
        {
            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => RunJob(plan, address, job, sequences, count, output, slot)).ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        }
        finally
        {
            if (sequences is not null)
            {
                ArrayPool<RawSequence>.Shared.Return(sequences);
            }
        }
    }
}
