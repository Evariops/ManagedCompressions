using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ManagedCompressions.Internal;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>Decompression of a whole source, its frames on several threads.</summary>
public sealed unsafe partial class ZstdDecompressor
{
    /// <summary>
    /// The smallest content whose checksum <see cref="DecompressAsync"/> verifies on a second thread
    /// while its frame decodes on the first; a smaller one is hashed after the frame, as
    /// <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/> does.
    /// </summary>
    /// <remarks>
    /// Measured on a Ryzen 9 7950X, frames of silesia at level 3 with their checksum, the helper against
    /// the hash after the frame: 0.93x at 512 KiB and 0.97x at 1 MiB (handing the work over costs more
    /// than the hash), 1.03x at 2 MiB, 1.04x at 4 MiB, 1.06x at 8 MiB, 1.08x to 1.16x at 424 MB.
    /// </remarks>
    internal const int ParallelChecksumMin = 4 << 20;

    /// <summary>
    /// The decompressors the frames of <see cref="DecompressAsync"/> run on, kept from one call to the next.
    /// Unlike the compressor's workers, whose tables and windows can take megabytes and which it lets go
    /// past <see cref="CodecPool.MaxRetainedBytes"/>, each holds a block's literals at most: none is let go.
    /// </summary>
    private Stack<ZstdDecompressor>? _idleWorkers;

    /// <summary>The helper that verifies a frame's checksum on a second thread, kept from one frame to the next.</summary>
    private ParallelChecksum? _parallelChecksum;

    /// <summary>The helper of the frame being decoded, if its checksum is verified on a second thread.</summary>
    private ParallelChecksum? _checksumHelper;

    /// <summary>A decompressor for the frames of <paramref name="owner"/>'s calls, with its dictionary.</summary>
    private ZstdDecompressor(ZstdDecompressor owner)
        : this()
    {
        _dictionary = owner._dictionary;
    }

    /// <summary>
    /// Decompresses every frame of <paramref name="source"/>, their contents one after the other into
    /// <paramref name="destination"/>, on up to <paramref name="maxDegreeOfParallelism"/> threads.
    /// </summary>
    /// <param name="source">
    /// Zstandard frames, and skippable frames, one after the other and nothing else: what a file
    /// compressed in several frames holds (pzstd's, a seekable archive's, concatenated files).
    /// </param>
    /// <param name="destination">Where the contents go, one after the other.</param>
    /// <param name="maxDegreeOfParallelism">
    /// The most threads the call uses at once; 0 or less for <see cref="Environment.ProcessorCount"/>, 1
    /// for the calling thread alone.
    /// </param>
    /// <param name="cancellationToken">Stops the frames not started yet.</param>
    /// <returns>The size of the contents.</returns>
    /// <remarks>
    /// <para>
    /// The frames are found first, from their headers and their blocks' headers, without decoding them.
    /// When every frame declares its content size, as libzstd's and this library's one-shot frames do,
    /// each one's place in the destination is known and the frames are decoded at once, each by a
    /// decompressor of its own, into its place: a source of several frames decodes as many times faster
    /// as there are threads and frames to share, up to what the memory can carry. The frames of a
    /// source compressed as one frame, by <see cref="ZstdCompressor.Compress"/> or
    /// <see cref="ZstdCompressor.CompressAsync"/> in its default format, cannot be shared out that way;
    /// <see cref="ZstdCompressor.CompressAsync"/> can write independent frames instead.
    /// </para>
    /// <para>
    /// A frame decoded alone, whose content is large and has a checksum, has that checksum (XXH64)
    /// verified on a second thread as the frame decodes, which takes it off the decoding thread.
    /// </para>
    /// <para>
    /// Frames decoded one after the other, a single frame among them, are decoded on the calling thread,
    /// before the call returns its task, completed: a caller that must not block runs the call on the
    /// thread pool. The decompressor keeps the decompressors its frames ran on for its next calls, as many
    /// as the threads it used, each holding some hundred kilobytes at most: a block's literals, a pair of blocks' sequences.
    /// </para>
    /// <para>
    /// The result is that of decoding the frames one after the other: the same content, and on invalid
    /// data the error of the first frame in the source that has one, which <see cref="LastError"/> then
    /// records. The destination's content past the end of the last frame decoded without an error is
    /// then unspecified. The source and the destination must not change until the task completes, and
    /// the decompressor must not be used meanwhile.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidDataException">The source is not valid Zstandard, or it ends inside a frame.</exception>
    /// <exception cref="ArgumentException">The contents do not fit in <paramref name="destination"/>.</exception>
    /// <exception cref="OperationCanceledException">The call was cancelled before its last frame started.</exception>
    public async ValueTask<int> DecompressAsync(
        ReadOnlyMemory<byte> source, Memory<byte> destination, int maxDegreeOfParallelism = -1, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int degree = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
        List<FrameSpan> frames = FrameScan.Scan(source.Span, out bool complete);
        int zstdFrames = 0;
        int sized = 0;
        long total = 0;
        foreach (FrameSpan frame in frames)
        {
            if (!frame.Skippable)
            {
                zstdFrames++;
                sized += frame.ContentSize >= 0 ? 1 : 0;
                total += Math.Max(frame.ContentSize, 0);
            }
        }

        // In parallel when the scan found every frame, every zstd frame declares its size, and there are
        // two of them at least; otherwise one after the other, which finds and reports any error in order.
        // A source whose contents do not fit goes one frame after the other too, which reports the first
        // error in the source's order, whatever it is.
        if (degree < 2 || !complete || zstdFrames < 2 || sized != zstdFrames || total > destination.Length)
        {
            return DecompressInOrder(source, destination, parallelChecksum: degree >= 2, cancellationToken);
        }

        // The decompressors share the dictionary: its double-symbol Huffman table built once here, they
        // only read it.
        _dictionary?.Entropy?.Huffman.BuildDoubleForSharing();
        _idleWorkers ??= new Stack<ZstdDecompressor>();
        int workers = Math.Min(degree, zstdFrames);
        var places = new long[frames.Count];
        long place = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            places[i] = place;
            place += frames[i].Skippable ? 0 : frames[i].ContentSize;
        }

        // What the workers rely on to decode a frame into its place without a check: the places end with
        // the destination's room or before it.
        Debug.Assert(place == total && total <= destination.Length, "the frames' places within the destination");

        var shared = new SharedFrames(frames, places);
        using MemoryHandle sourcePin = source.Pin();
        using MemoryHandle destinationPin = destination.Pin();
        nint sourceAddress = (nint)sourcePin.Pointer;
        nint destinationAddress = (nint)destinationPin.Pointer;
        var tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            ZstdDecompressor worker = TakeWorker();
            tasks[w] = Task.Run(() =>
            {
                try
                {
                    worker.DecompressShared(shared, sourceAddress, source.Length, destinationAddress, cancellationToken);
                }
                finally
                {
                    ReturnWorker(worker);
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // A frame that failed is decoded again on this thread, and the frames after it: the error, or the
        // contents, are then exactly those of the frames decoded one after the other (a frame decoded into
        // a place of the size it declares fails otherwise than with the whole rest of the destination).
        if (shared.FirstFailed is int failed)
        {
            int done = (int)places[failed];
            return done + DecompressInOrder(source.Slice(frames[failed].Offset), destination.Slice(done), parallelChecksum: false, cancellationToken);
        }

        if (shared.Decoded < zstdFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        LastError = ZstdError.None;
        return (int)total;
    }

    /// <summary>
    /// The frames one after the other on the calling thread, each with <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/>'s
    /// result; a large frame's checksum verified on a second thread when <paramref name="parallelChecksum"/>.
    /// </summary>
    private int DecompressInOrder(ReadOnlyMemory<byte> source, Memory<byte> destination, bool parallelChecksum, CancellationToken cancellationToken)
    {
        using MemoryHandle pin = destination.Pin();
        int ip = 0;
        int op = 0;
        while (ip < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> frame = source.Span.Slice(ip);
            ParallelChecksum? helper = null;
            if (parallelChecksum && ChecksumWorthHelping(frame, destination.Length - op))
            {
                helper = _parallelChecksum ??= new ParallelChecksum();
                helper.Start((byte*)pin.Pointer + op);
            }

            OperationStatus status;
            int consumed;
            int written;
            _checksumHelper = helper;
            try
            {
                status = Decompress(frame, destination.Span.Slice(op), out consumed, out written);
            }
            finally
            {
                _checksumHelper = null;
                helper?.Abandon();
            }

            if (status != OperationStatus.Done)
            {
                ThrowFor(LastError, nameof(destination));
            }

            ip += consumed;
            op += written;
        }

        LastError = ZstdError.None;
        return op;
    }

    /// <summary>Whether the frame starting <paramref name="source"/> has a checksum and a content large enough for a second thread to verify it.</summary>
    private static bool ChecksumWorthHelping(ReadOnlySpan<byte> source, int room)
    {
        if (source.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(source) != FrameFormat.Magic
            || FrameHeader.Parse(source, out FrameHeader header) != ZstdError.None || !header.HasChecksum)
        {
            return false;
        }

        ulong size = header.HasContentSize ? header.ContentSize : (ulong)room;
        return size >= ParallelChecksumMin;
    }

    /// <summary>A worker's part of a parallel call: the frames it takes, one at a time, until none is left.</summary>
    private void DecompressShared(SharedFrames shared, nint source, int sourceLength, nint destination, CancellationToken cancellationToken)
    {
        while (shared.TryTake(out int index))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            FrameSpan frame = shared.Frames[index];
            if (frame.Skippable)
            {
                continue;
            }

            // SAFETY: the scan found every frame within the source, and the caller placed the contents
            // within the destination, as the sizes the frames declare add up.
            Bounds.Within((byte*)source + frame.Offset, frame.Length, (byte*)source, (byte*)source + sourceLength);
            var input = new ReadOnlySpan<byte>((byte*)source + frame.Offset, frame.Length);
            var output = new Span<byte>((byte*)destination + shared.Places[index], (int)frame.ContentSize);
            if (Decompress(input, output, out int consumed, out int written) != OperationStatus.Done
                || consumed != frame.Length || written != frame.ContentSize)
            {
                shared.Fail(index);
            }
            else
            {
                shared.Done();
            }
        }
    }

    private ZstdDecompressor TakeWorker()
    {
        lock (_idleWorkers!)
        {
            return _idleWorkers.Count > 0 ? _idleWorkers.Pop() : new ZstdDecompressor(this);
        }
    }

    private void ReturnWorker(ZstdDecompressor worker)
    {
        lock (_idleWorkers!)
        {
            _idleWorkers.Push(worker);
        }
    }

    /// <summary>The exception a refusal becomes in <see cref="DecompressAsync"/>.</summary>
    private static void ThrowFor(ZstdError error, string destinationName)
    {
        throw error switch
        {
            ZstdError.DestinationTooSmall => new ArgumentException("The contents do not fit in the destination.", destinationName),
            ZstdError.Truncated => new InvalidDataException("The Zstandard source ends inside a frame."),
            _ => new InvalidDataException($"The data is not valid Zstandard: {error}."),
        };
    }

    /// <summary>The frames of a parallel call, handed out one at a time, and the first error in their order.</summary>
    private sealed class SharedFrames(List<FrameSpan> frames, long[] places)
    {
        private readonly Lock _lock = new();
        private int _next = -1;
        private int _firstFailed = int.MaxValue;
        private int _decoded;

        public List<FrameSpan> Frames { get; } = frames;

        public long[] Places { get; } = places;

        /// <summary>The first frame in the source that failed; null when none did.</summary>
        public int? FirstFailed => _firstFailed == int.MaxValue ? null : _firstFailed;

        /// <summary>The frames decoded without an error.</summary>
        public int Decoded => Volatile.Read(ref _decoded);

        public void Done() => Interlocked.Increment(ref _decoded);

        /// <summary>The next frame to decode; none once they are all taken, or past a frame that failed.</summary>
        public bool TryTake(out int index)
        {
            index = Interlocked.Increment(ref _next);
            return index < Frames.Count && index < Volatile.Read(ref _firstFailed);
        }

        public void Fail(int index)
        {
            lock (_lock)
            {
                if (index < _firstFailed)
                {
                    Volatile.Write(ref _firstFailed, index);
                }
            }
        }
    }
}
