using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>
/// Frames compressed chunk by chunk: libzstd's <c>ZSTD_compressContinue</c> and <c>ZSTD_compressEnd</c>,
/// which <see cref="ZstdEncoder"/> calls as <c>ZSTD_compressStream2</c> does.
/// </summary>
public sealed unsafe partial class ZstdCompressor
{
    /// <summary>A chunk that takes the frame past the size pledged for it: libzstd's <c>srcSize_wrong</c>.</summary>
    internal const int PledgedSizeExceeded = -2;

    /// <summary>
    /// libzstd's <c>ZSTD_CURRENT_MAX</c>: an index libzstd corrects its tables before reaching
    /// (<c>ZSTD_overflowCorrectIfNeeded</c>), which this compressor does not do yet.
    /// </summary>
    private const ulong CurrentMax = 3500UL << 20;

    private XxHash64? _streamChecksum;
    private CompressionParameters _streamParameters;
    private ulong _streamPledged;
    private ulong _streamConsumed;
    private ulong _streamProduced;
    private int _streamBlockSizeMax;
    private StreamStage _streamStage;
    private bool _streamWindowStarted;

    /// <summary>libzstd's <c>window.nextSrc</c>: where the last chunk ended, to tell whether the next one follows it.</summary>
    private byte* _streamNextSource;

    /// <summary>libzstd's <c>ZSTD_compressionStage_e</c> once a frame has begun.</summary>
    private enum StreamStage
    {
        /// <summary>The header is still to be written.</summary>
        Init,
        Ongoing,

        /// <summary>The last block is written: the end of the frame adds no empty one.</summary>
        Ending,
    }

    /// <summary>
    /// libzstd's <c>ZSTD_CCtx_init_compressStream2</c>, then <c>ZSTD_compressBegin_internal</c>, for
    /// a frame streamed through <c>ZSTD_compressStream2</c>: its parameters for a content of
    /// <paramref name="pledgedSize"/> bytes, or of unknown size (<see cref="CompressionParameters.UnknownSize"/>),
    /// and the sizes <c>ZSTD_resetCCtx_internal</c> derives from them. The window itself starts with
    /// the first chunk.
    /// </summary>
    /// <param name="pledgedSize">The content's size, as pledged; <see cref="CompressionParameters.UnknownSize"/> if it is not.</param>
    /// <param name="blockSizeMax">libzstd's <c>blockSizeMax</c>: the largest block, and the chunk the input buffer hands over.</param>
    /// <param name="windowSize">The window: the size the input buffer keeps, besides a block.</param>
    /// <exception cref="NotSupportedException">The frame needs a dictionary or the long-distance matcher.</exception>
    internal void BeginStream(ulong pledgedSize, out int blockSizeMax, out long windowSize)
    {
        if (_dictionary is not null)
        {
            throw new NotSupportedException("ZstdEncoder does not compress with a dictionary yet.");
        }

        CompressionParameters parameters = CompressionParameters.ForStream(Level, pledgedSize, LongDistanceMatching, _windowLog);
        if (UsesLongDistanceMatcher(parameters))
        {
            throw new NotSupportedException(
                "ZstdEncoder does not run the long-distance matcher yet, which libzstd turns on for windows of 2^27 bytes "
                + "and more at levels 16 and up: level 22 without a pledged size, for one.");
        }

        _streamParameters = parameters;
        windowSize = (long)Math.Max(1, Math.Min(1UL << parameters.WindowLog, pledgedSize));
        blockSizeMax = (int)Math.Min(FrameFormat.MaxBlockSize, windowSize);
        _streamBlockSizeMax = blockSizeMax;
        _streamPledged = pledgedSize;
        _streamConsumed = 0;
        _streamProduced = 0;
        _streamStage = StreamStage.Init;
        _streamWindowStarted = false;
        if (AppendChecksum)
        {
            _streamChecksum ??= new XxHash64();
            _streamChecksum.Reset();
        }
    }

    /// <summary>
    /// libzstd's <c>ZSTD_compressContinue_internal</c> for a frame, and with <paramref name="last"/>
    /// <c>ZSTD_compressEnd</c>: the header before the first chunk; the chunk's place in the window
    /// (<c>ZSTD_window_update</c>); its blocks (<c>ZSTD_compress_frameChunk</c>), the last one flagged
    /// when the chunk ends the frame; then the end of the frame, an empty last block if no block was
    /// last, the checksum. The pledged size is checked as libzstd checks it, after the chunk.
    /// </summary>
    /// <param name="source">The chunk: its bytes stay where they are while the window holds them.</param>
    /// <param name="size">Its size; 0 writes the header alone, or with <paramref name="last"/> ends the frame.</param>
    /// <param name="last">Whether the chunk ends the frame.</param>
    /// <param name="destination">Where the frame's next bytes go.</param>
    /// <param name="capacity">Room for them: <see cref="GetMaxCompressedLength"/> of the chunk always suffices.</param>
    /// <returns>The bytes written, or <see cref="PledgedSizeExceeded"/>.</returns>
    internal int CompressStreamChunk(byte* source, int size, bool last, byte* destination, int capacity)
    {
        int headerSize = 0;
        if (_streamStage == StreamStage.Init)
        {
            bool contentSize = _writeContentSize && _streamPledged != CompressionParameters.UnknownSize;
            headerSize = WriteFrameHeader(
                destination, _streamParameters.WindowLog, _streamPledged, AppendChecksum, 0, contentSize, _writeDictionaryId);
            _streamStage = StreamStage.Ongoing;
        }

        int op = headerSize;
        if (size != 0)
        {
            if (!_streamWindowStarted)
            {
                BeginFrame(_streamParameters, source, 0, longDistance: false, stream: true);
                _streamNextSource = source;
                _streamWindowStarted = true;
            }

            UpdateWindow(source, size);
            if ((ulong)(_streamNextSource - _matchState.Base) > CurrentMax)
            {
                throw new NotSupportedException(
                    "ZstdEncoder does not correct its indices yet, as libzstd does past 3500 MiB: compress less in a frame, "
                    + "or start a new encoder.");
            }

            _streamChecksum?.Append(new ReadOnlySpan<byte>(source, size));
            long savings = (long)_streamConsumed - (long)_streamProduced;
            int blocks = CompressBlocks(source, size, destination + op, capacity - op, _streamBlockSizeMax, last, savings);
            if (last && blocks != 0)
            {
                _streamStage = StreamStage.Ending;
            }

            op += blocks;
            _nextIndex = (uint)(_streamNextSource - _matchState.Base);
            _streamConsumed += (ulong)size;
            _streamProduced += (ulong)op;
            if (_streamPledged != CompressionParameters.UnknownSize && _streamConsumed > _streamPledged)
            {
                return PledgedSizeExceeded;
            }
        }

        if (!last)
        {
            return op;
        }

        // libzstd's ZSTD_writeEpilogue.
        if (_streamStage != StreamStage.Ending)
        {
            WriteBlockHeader(destination + op, 1, 0);
            op += FrameFormat.BlockHeaderSize;
        }

        if (_streamChecksum is not null)
        {
            uint checksum = (uint)_streamChecksum.GetCurrentHashAsUInt64();
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(destination + op, FrameFormat.ChecksumSize), checksum);
            op += FrameFormat.ChecksumSize;
        }

        _streamStage = StreamStage.Init;
        _streamWindowStarted = false;
        return _streamPledged != CompressionParameters.UnknownSize && _streamConsumed != _streamPledged ? PledgedSizeExceeded : op;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_window_update</c>: a chunk that does not follow the last one starts a segment
    /// of its own, the window's bytes before it becoming an extDict below it (unless too few to search);
    /// an extDict the chunk overlaps, being the buffer the chunk was copied into, shrinks past it.
    /// </summary>
    private void UpdateWindow(byte* source, int size)
    {
        ref MatchState window = ref _matchState;
        if (source != _streamNextSource)
        {
            uint distanceFromBase = (uint)(_streamNextSource - window.Base);
            window.LowLimit = window.DictLimit;
            window.DictLimit = distanceFromBase;
            window.DictionaryBase = window.Base;
            window.Base = source - distanceFromBase;
            if (window.DictLimit - window.LowLimit < MatchFinder.HashReadSize)
            {
                window.LowLimit = window.DictLimit;
            }

            // As ZSTD_compressContinue_internal: the tables resume at the new segment.
            window.NextToUpdate = window.DictLimit;
        }

        _streamNextSource = source + size;
        window.End = (uint)(_streamNextSource - window.Base);
        if (window.LowLimit < window.DictLimit
            && source + size > window.DictionaryBase + window.LowLimit
            && source < window.DictionaryBase + window.DictLimit)
        {
            ulong highInputIndex = (ulong)(source + size - window.DictionaryBase);
            window.LowLimit = highInputIndex > window.DictLimit ? window.DictLimit : (uint)highInputIndex;
        }
    }
}
