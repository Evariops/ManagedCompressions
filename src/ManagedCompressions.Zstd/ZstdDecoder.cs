using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>
/// Decompresses Zstandard frames as their bytes arrive and as room comes for their content: a frame
/// may be cut anywhere across calls, may declare no content size, and may be larger than any buffer.
/// Its memory is bounded by the window the frame declares, at most
/// <see cref="ZstdDecompressionOptions.MaxWindowLog"/>. The streaming counterpart of
/// <see cref="ZstdDecompressor"/>, shaped as System.IO.Compression's <c>ZstandardDecoder</c>.
/// </summary>
/// <remarks>
/// <para>
/// It accepts and refuses what libzstd's <c>ZSTD_decompressStream</c> does. Where a call holds a whole
/// frame that declares its content size, and room for that content, the frame is decoded in place, as
/// <see cref="ZstdDecompressor"/> decodes it, and as libzstd does there too. Otherwise each block is
/// decoded into a window of the frame's size, then copied out: the window restarts from its start
/// when the next block may not fit, the bytes before the restart staying within reach.
/// </para>
/// <para>
/// Outside that single pass, libzstd lets two frames through that its one-shot decoder refuses: a
/// frame with a compressed block of size zero, which it skips as an empty block, and a frame whose
/// content does not match its declared size, which it checks only after a last block that is not
/// empty. This decoder refuses both, however they arrive, as <see cref="ZstdDecompressor"/> does.
/// </para>
/// <para>
/// A decoder serves one stream at a time, and is not thread-safe. Once warm, it allocates nothing.
/// </para>
/// </remarks>
public sealed class ZstdDecoder
{
    /// <summary>Room after the window, for the block decoder's reads and writes past an end, 16 bytes at a time.</summary>
    private const int Margin = 64;

    /// <summary>The largest frame header: magic, descriptor, window, dictionary identifier, content size.</summary>
    private const int MaxHeaderSize = 18;

    private readonly ZstdDecompressor _engine;
    private readonly ulong _maxWindowSize;

    /// <summary>The checksum of the frame's content, created by the first frame that has one.</summary>
    private XxHash64? _checksum;

    /// <summary>The header being read, then a block header or the checksum: whatever arrives across calls.</summary>
    private readonly byte[] _header = new byte[MaxHeaderSize];

    private Stage _stage;
    private int _headerLength;
    private FrameHeader _frame;
    private long _skip;
    private int _blockType;
    private int _blockSize;
    private bool _lastBlock;

    /// <summary>A block whose bytes arrive across calls, gathered; grown, never shrunk.</summary>
    private byte[] _block = [];
    private int _blockLength;

    /// <summary>The window: the frame's content, decoded, up to its window size behind the last block.</summary>
    private byte[] _window = [];

    /// <summary>The part of <see cref="_window"/> blocks are decoded into: libzstd's <c>outBuffSize</c>.</summary>
    private int _capacity;

    /// <summary>The frame's window, 1 KiB at least: libzstd's <c>fParams.windowSize</c>.</summary>
    private ulong _windowSize;

    /// <summary>Whether the content is larger than the window, which then restarts from its start.</summary>
    private bool _restarts;

    /// <summary>Where the next block goes.</summary>
    private int _out;

    /// <summary>How much of the window the caller has been given.</summary>
    private int _flushed;

    /// <summary>
    /// After a restart, the length of the window before it: the content that precedes the window's
    /// start, which the next blocks reach back into. 0 before the first restart, when the dictionary
    /// precedes the frame instead.
    /// </summary>
    private int _history;
    private bool _restarted;
    private ulong _produced;

    /// <summary>Creates a decoder without a dictionary, for windows of up to <see cref="ZstdDecompressionOptions.DefaultMaxWindowLog"/>.</summary>
    /// <exception cref="PlatformNotSupportedException">The processor is big-endian: see the README.</exception>
    public ZstdDecoder()
        : this(new ZstdDecompressor(), ZstdDecompressionOptions.DefaultMaxWindowLog)
    {
    }

    /// <summary>
    /// Creates a decoder for frames compressed with <paramref name="dictionary"/>: a dictionary in the
    /// zstd format, or any other bytes as raw content. The bytes are copied.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">The dictionary has the zstd format but invalid tables.</exception>
    public ZstdDecoder(ReadOnlySpan<byte> dictionary)
        : this(dictionary, ZstdDecompressionOptions.DefaultMaxWindowLog)
    {
    }

    /// <summary>
    /// Creates a decoder for frames compressed with <paramref name="dictionary"/>, prepared once and
    /// shared, for windows of up to <see cref="ZstdDecompressionOptions.DefaultMaxWindowLog"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="dictionary"/> is null.</exception>
    public ZstdDecoder(ZstdDictionary dictionary)
        : this(new ZstdDecompressor(dictionary), ZstdDecompressionOptions.DefaultMaxWindowLog)
    {
    }

    /// <summary>Creates a decoder with <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options give the dictionary both as bytes and prepared.</exception>
    /// <exception cref="System.IO.InvalidDataException">The dictionary has the zstd format but invalid tables.</exception>
    public ZstdDecoder(ZstdDecompressionOptions options)
        : this(Engine(options ?? throw new ArgumentNullException(nameof(options))), options.MaxWindowLog)
    {
    }

    private ZstdDecoder(ReadOnlySpan<byte> dictionary, int maxWindowLog)
        : this(dictionary.IsEmpty ? new ZstdDecompressor() : new ZstdDecompressor(dictionary), maxWindowLog)
    {
    }

    private ZstdDecoder(ZstdDecompressor engine, int maxWindowLog)
    {
        _engine = engine;
        _maxWindowSize = 1UL << maxWindowLog;
    }

    /// <summary>The one-shot decoder of the options' dictionary, prepared or given as bytes, if any.</summary>
    private static ZstdDecompressor Engine(ZstdDecompressionOptions options)
    {
        if (options.PreparedDictionary is { } prepared)
        {
            return options.Dictionary.IsEmpty
                ? new ZstdDecompressor(prepared)
                : throw new ArgumentException("A dictionary is given as bytes or prepared, not both.", nameof(options));
        }

        return options.Dictionary.IsEmpty ? new ZstdDecompressor() : new ZstdDecompressor(options.Dictionary.Span);
    }

    private enum Stage
    {
        /// <summary>Before a frame, or inside its header.</summary>
        FrameHeader,
        SkippableContent,
        BlockHeader,
        Block,
        Checksum,

        /// <summary>The frame is decoded: once its content is out, the call returns.</summary>
        FrameEnd,
        Failed,
    }

    /// <summary>
    /// Why the decoder refused its stream, after <see cref="OperationStatus.InvalidData"/>: the check
    /// that failed. <see cref="ZstdError.None"/> otherwise.
    /// </summary>
    public ZstdError LastError { get; private set; }

    /// <summary>Whether the decoder is inside a frame: a stream that ends here is cut short.</summary>
    internal bool InFrame => _stage != Stage.FrameHeader || _headerLength != 0;

    /// <summary>The memory the decoder holds on to, its window and buffers, roughly: for <see cref="CodecPool"/>.</summary>
    internal long RetainedBytes => (long)_window.Length + _block.Length + _engine.RetainedBytes;

    /// <summary>
    /// Decodes as much of <paramref name="source"/> as it can into <paramref name="destination"/>, up to
    /// the end of the frame it is in.
    /// </summary>
    /// <param name="source">The stream's next bytes; those taken need not be passed again.</param>
    /// <param name="destination">Where the content goes, from its start.</param>
    /// <param name="bytesConsumed">The bytes of <paramref name="source"/> taken.</param>
    /// <param name="bytesWritten">The bytes of content written to <paramref name="destination"/>.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when a frame ends and all its content has been written: the
    /// next call starts the next frame. <see cref="OperationStatus.NeedMoreData"/> when the source has
    /// been taken whole inside a frame, whose content so far has been written.
    /// <see cref="OperationStatus.DestinationTooSmall"/> when the destination is full, with more
    /// content to come. <see cref="OperationStatus.InvalidData"/> when the stream is not valid
    /// Zstandard (see <see cref="LastError"/>): from then on, until <see cref="Reset"/>, the decoder
    /// refuses every call.
    /// </returns>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        int consumed = 0;
        int written = 0;
        OperationStatus status;
        try
        {
            status = Run(source, destination, ref consumed, ref written);
        }
        catch (ZstdException e)
        {
            _stage = Stage.Failed;
            LastError = e.Error;
            status = OperationStatus.InvalidData;
        }

        // The window and the decompressor's tables, read through pointers, are arrays only the decoder
        // holds: it stays alive until the call is done, whatever the code after the loops uses of it.
        GC.KeepAlive(this);
        bytesConsumed = consumed;
        bytesWritten = written;
        return status;
    }

    /// <summary>
    /// <see cref="Decompress"/> for a caller that writes the content out itself, as
    /// <see cref="ZstdStream.CopyTo(System.IO.Stream, int)"/> does: no destination, each block decoded
    /// into the window, and the content not given out yet returned from there, which spares copying
    /// it through a buffer of the caller's. The content stays valid until the next call.
    /// </summary>
    /// <returns>
    /// <see cref="OperationStatus.DestinationTooSmall"/> when there may be more content to decode from
    /// the source, <see cref="OperationStatus.Done"/> at the end of a frame, otherwise as <see cref="Decompress"/>.
    /// </returns>
    internal OperationStatus DecompressInWindow(ReadOnlySpan<byte> source, out int bytesConsumed, out ReadOnlyMemory<byte> content)
    {
        OperationStatus status = Decompress(source, default, out bytesConsumed, out _);
        content = _window.AsMemory(_flushed, _out - _flushed);
        _flushed = _out;
        return status;
    }

    /// <summary>
    /// Forgets the frame in progress and any refusal: the next call starts a new stream. The buffers
    /// and the dictionary are kept.
    /// </summary>
    public void Reset()
    {
        _stage = Stage.FrameHeader;
        _headerLength = 0;
        _blockLength = 0;
        _skip = 0;
        _out = 0;
        _flushed = 0;
        LastError = ZstdError.None;
    }

    private OperationStatus Run(ReadOnlySpan<byte> source, Span<byte> destination, ref int consumed, ref int written)
    {
        if (_stage == Stage.Failed)
        {
            return OperationStatus.InvalidData;
        }

        while (true)
        {
            // The content the caller has not been given yet, first: no block is decoded over it.
            if (_flushed < _out)
            {
                int count = Math.Min(_out - _flushed, destination.Length - written);
                _window.AsSpan(_flushed, count).CopyTo(destination.Slice(written));
                _flushed += count;
                written += count;
                if (_flushed < _out)
                {
                    return OperationStatus.DestinationTooSmall;
                }
            }

            switch (_stage)
            {
                case Stage.FrameHeader:
                    if (_headerLength == 0 && TryDecodeInPlace(source.Slice(consumed), destination.Slice(written), ref consumed, ref written))
                    {
                        return OperationStatus.Done;
                    }

                    if (!ReadHeader(source, ref consumed))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    break;

                case Stage.SkippableContent:
                {
                    long count = Math.Min(_skip, source.Length - consumed);
                    consumed += (int)count;
                    _skip -= count;
                    if (_skip > 0)
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    _stage = Stage.FrameHeader;
                    return OperationStatus.Done;
                }

                case Stage.BlockHeader:
                    if (!Gather(source, ref consumed, FrameFormat.BlockHeaderSize))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    BeginBlock();
                    break;

                case Stage.Block:
                {
                    int size = _blockType == 1 ? 1 : _blockSize;
                    ReadOnlySpan<byte> block;
                    if (_blockLength == 0 && source.Length - consumed >= size)
                    {
                        block = source.Slice(consumed, size);
                        consumed += size;
                    }
                    else
                    {
                        if (_block.Length < size)
                        {
                            _block = new byte[Math.Max(size, Math.Min(2 * _block.Length, FrameFormat.MaxBlockSize))];
                        }

                        int count = Math.Min(size - _blockLength, source.Length - consumed);
                        source.Slice(consumed, count).CopyTo(_block.AsSpan(_blockLength));
                        _blockLength += count;
                        consumed += count;

                        // libzstd hands a raw block out as it arrives: into a window the declared size
                        // bounds, a part past its end is refused as soon as it arrives.
                        if (_blockType == 0 && !_restarts && _blockLength > _capacity - _out)
                        {
                            Throw.Error(ZstdError.ContentSizeMismatch);
                        }

                        if (_blockLength < size)
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        block = _block.AsSpan(0, size);
                        _blockLength = 0;
                    }

                    DecodeBlock(block);
                    if (_lastBlock)
                    {
                        EndContent();
                    }
                    else
                    {
                        _stage = Stage.BlockHeader;
                    }

                    break;
                }

                case Stage.Checksum:
                    if (!Gather(source, ref consumed, FrameFormat.ChecksumSize))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    if (BinaryPrimitives.ReadUInt32LittleEndian(_header) != (uint)_checksum!.GetCurrentHashAsUInt64())
                    {
                        Throw.Error(ZstdError.ChecksumMismatch);
                    }

                    _stage = Stage.FrameEnd;
                    break;

                case Stage.FrameEnd:
                    _stage = Stage.FrameHeader;
                    _out = 0;
                    _flushed = 0;
                    return OperationStatus.Done;
            }
        }
    }

    /// <summary>
    /// libzstd's single-pass shortcut: a frame that starts the source, lies in it whole, and declares
    /// a content size the destination holds, decoded in place by the one-shot decoder, which checks it
    /// as <c>ZSTD_decompress_usingDDict</c> does. libzstd takes it before it checks the window.
    /// </summary>
    private bool TryDecodeInPlace(ReadOnlySpan<byte> source, Span<byte> destination, ref int consumed, ref int written)
    {
        if (source.Length < FrameFormat.StartingInputLength
            || BinaryPrimitives.ReadUInt32LittleEndian(source) != FrameFormat.Magic
            || FrameHeader.Parse(source, out FrameHeader header) != ZstdError.None
            || !header.HasContentSize
            || header.ContentSize > (ulong)destination.Length)
        {
            return false;
        }

        long size = ZstdDecompressor.FindFrameSize(source);
        if (size < 0)
        {
            return false;
        }

        if (_engine.Decompress(source.Slice(0, (int)size), destination, out int frameConsumed, out int frameWritten) != OperationStatus.Done)
        {
            // The content does not fit what the header declares, which the destination holds.
            Throw.Error(_engine.LastError == ZstdError.DestinationTooSmall ? ZstdError.ContentSizeMismatch : _engine.LastError);
        }

        consumed += frameConsumed;
        written += frameWritten;
        return true;
    }

    /// <summary>
    /// Gathers the frame header, or a skippable frame's, and starts the frame once it is whole.
    /// </summary>
    /// <returns>Whether the header is whole; false when the source ran out first, all of it taken.</returns>
    private bool ReadHeader(ReadOnlySpan<byte> source, ref int consumed)
    {
        while (true)
        {
            int size = HeaderSize();
            if (_headerLength >= size)
            {
                break;
            }

            int count = Math.Min(size - _headerLength, source.Length - consumed);
            if (count == 0)
            {
                // libzstd refuses early what cannot begin a frame.
                if (_headerLength < 4 && !FrameHeader.IsMagicPrefix(_header.AsSpan(0, _headerLength)))
                {
                    Throw.Error(ZstdError.UnknownMagic);
                }

                return false;
            }

            source.Slice(consumed, count).CopyTo(_header.AsSpan(_headerLength));
            _headerLength += count;
            consumed += count;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(_header);
        if ((magic & FrameFormat.SkippableMagicMask) == FrameFormat.SkippableMagic)
        {
            _skip = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(4));
            _headerLength = 0;
            _stage = Stage.SkippableContent;
            return true;
        }

        ZstdError error = FrameHeader.Parse(_header.AsSpan(0, _headerLength), out _frame);
        if (error != ZstdError.None)
        {
            Throw.Error(error);
        }

        _headerLength = 0;
        BeginFrame();
        return true;
    }

    /// <summary>The size of the header being gathered, as far as its first bytes tell.</summary>
    private int HeaderSize()
    {
        if (_headerLength < 4)
        {
            return 4;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(_header);
        if ((magic & FrameFormat.SkippableMagicMask) == FrameFormat.SkippableMagic)
        {
            return FrameFormat.SkippableHeaderSize;
        }

        if (magic != FrameFormat.Magic)
        {
            Throw.Error(ZstdError.UnknownMagic);
        }

        if (_headerLength < FrameFormat.StartingInputLength)
        {
            return FrameFormat.StartingInputLength;
        }

        byte descriptor = _header[4];
        bool singleSegment = ((descriptor >> 5) & 1) != 0;
        int dictionaryIdCode = descriptor & 3;
        int contentSizeCode = descriptor >> 6;
        return FrameFormat.StartingInputLength + (singleSegment ? 0 : 1) + (dictionaryIdCode == 3 ? 4 : dictionaryIdCode)
            + (contentSizeCode == 0 ? (singleSegment ? 1 : 0) : 1 << contentSizeCode);
    }

    /// <summary>
    /// libzstd's checks of a frame header (<c>ZSTD_decodeFrameHeader</c>, the window against
    /// <c>ZSTD_d_windowLogMax</c>) and its window, <c>ZSTD_decodingBufferSize_internal</c>: the window
    /// and two blocks, and their margins, or the content size when it is smaller. libzstd's second block
    /// is room for the literals it splits into the buffer, which the block decoder keeps apart; but it is
    /// room for content too, and where the buffer restarts sets how far back an offset may reach before
    /// the restart (see <see cref="History"/> after it), so the window is as large as libzstd's.
    /// </summary>
    private void BeginFrame()
    {
        DecoderDictionary? dictionary = _engine.CreatedDictionary;
        if (_frame.DictionaryId != 0 && _frame.DictionaryId != (dictionary?.Id ?? 0))
        {
            Throw.Error(ZstdError.DictionaryMismatch);
        }

        ulong window = Math.Max(_frame.WindowSize, 1UL << FrameFormat.WindowLogMin);
        if (window > _maxWindowSize)
        {
            Throw.Error(ZstdError.WindowTooLarge);
        }

        ulong block = Math.Min((ulong)_frame.BlockSizeMax, window);
        ulong ring = window + (2 * block) + (2 * 32);
        ulong capacity = _frame.HasContentSize ? Math.Min(_frame.ContentSize, ring) : ring;
        _capacity = (int)capacity;
        _windowSize = window;
        _restarts = !_frame.HasContentSize || capacity < _frame.ContentSize;
        if (_window.Length < _capacity + Margin)
        {
            _window = new byte[_capacity + Margin];
        }

        _out = 0;
        _flushed = 0;
        _history = 0;
        _restarted = false;
        _produced = 0;
        _engine.BeginStreamFrame(dictionary?.Entropy, _frame.BlockSizeMax);
        if (_frame.HasChecksum)
        {
            (_checksum ??= new XxHash64()).Reset();
        }

        _stage = Stage.BlockHeader;
    }

    /// <summary>Reads the block header gathered in <see cref="_header"/>, as the one-shot decoder does.</summary>
    private void BeginBlock()
    {
        int blockHeader = _header[0] | (_header[1] << 8) | (_header[2] << 16);
        _lastBlock = (blockHeader & 1) != 0;
        _blockType = (blockHeader >> 1) & 3;
        _blockSize = blockHeader >> 3;
        if (_blockType == 3)
        {
            Throw.Error(ZstdError.ReservedBlockType);
        }

        // The content of an RLE block, which libzstd checks once decoded; and as ZSTD_decompressContinue,
        // the block's size in the stream, one byte for an RLE block: more than a frame that declares no
        // content allows, whose maximum block size is 0.
        if (_blockSize > _frame.BlockSizeMax || (_blockType == 1 && _frame.BlockSizeMax == 0))
        {
            Throw.Error(ZstdError.BlockTooLarge);
        }

        _blockLength = 0;
        _stage = Stage.Block;
    }

    /// <summary>
    /// Decodes a block into the window. When the window is smaller than the content and the block may
    /// not fit before its end, the window restarts from its start, as libzstd's does: what it held
    /// becomes the history the block reaches back into (see <see cref="History"/>).
    /// </summary>
    private void DecodeBlock(ReadOnlySpan<byte> block)
    {
        if (_restarts && _out + _frame.BlockSizeMax > _capacity)
        {
            _history = _out;
            _restarted = true;
            _out = 0;
            _flushed = 0;

            // The copies that read past the history's end read zeros, not what an earlier frame left
            // there. The window has more than this margin after its capacity.
            _window.AsSpan(_history, DecoderDictionary.Margin).Clear();
        }

        int start = _out;
        switch (_blockType)
        {
            case 0: // raw
                if (_blockSize > _capacity - _out)
                {
                    Throw.Error(ZstdError.ContentSizeMismatch);
                }

                block.CopyTo(_window.AsSpan(_out));
                _out += _blockSize;
                break;

            case 1: // RLE: the size is the content's, the block holds one byte
                if (_blockSize > _capacity - _out)
                {
                    Throw.Error(ZstdError.ContentSizeMismatch);
                }

                _window.AsSpan(_out, _blockSize).Fill(block[0]);
                _out += _blockSize;
                break;

            default: // compressed
            {
                ReadOnlySpan<byte> history = History();
                try
                {
                    _out += _engine.DecodeStreamBlock(block, _window.AsSpan(0, _capacity), _out, _frame.BlockSizeMax, history);
                }
                catch (ZstdException e) when (e.Error == ZstdError.DestinationTooSmall)
                {
                    // Only a window the declared content size bounds can be too small: the content is larger.
                    Throw.Error(ZstdError.ContentSizeMismatch);
                }

                break;
            }
        }

        int produced = _out - start;
        _produced += (ulong)produced;
        if (_frame.HasContentSize && _produced > _frame.ContentSize)
        {
            Throw.Error(ZstdError.ContentSizeMismatch);
        }

        if (_frame.HasChecksum)
        {
            _checksum!.Append(_window.AsSpan(start, produced));
        }
    }

    /// <summary>
    /// What the next block may reach back into before the start of the window: the dictionary, or
    /// after a restart, the window before it. Once a window of content is decoded, only as much of it
    /// as the frame's window leaves beside the content since the start or the restart, as libzstd
    /// 1.6.0's <c>ZSTD_checkContinuity</c> bounds it.
    /// </summary>
    /// <remarks>
    /// After a restart, the window before it is the very buffer the block is decoded into. Whole, it let
    /// an offset past the frame's window read ahead of the block's output: bytes the frame wrote before
    /// the restart, or bytes that copies running past their end had left there, read from the
    /// literals of an earlier frame or from what an earlier frame left in the window (libzstd 1.5.1 to
    /// 1.5.7 did the same, until zstd's commit 0c9d2975). Bounded, it starts more than a block and its
    /// margin past the block's start: the restart leaves more than the window and a block behind it, of
    /// which the window keeps at most the window less the content since the restart, and a block writes
    /// at most a block and a copy's overrun.
    /// </remarks>
    private ReadOnlySpan<byte> History()
    {
        ReadOnlySpan<byte> history = _restarted ? _window.AsSpan(0, _history) : _engine.CreatedDictionary is { } dictionary ? dictionary.Content : default;
        if (_produced >= _windowSize)
        {
            ulong room = (ulong)_out < _windowSize ? _windowSize - (ulong)_out : 0;
            history = history.Slice(history.Length - (int)Math.Min((ulong)history.Length, room));
        }

        return history;
    }

    /// <summary>After the last block: the content size checked, then the checksum, if any, read.</summary>
    private void EndContent()
    {
        if (_frame.HasContentSize && _produced != _frame.ContentSize)
        {
            Throw.Error(ZstdError.ContentSizeMismatch);
        }

        _stage = _frame.HasChecksum ? Stage.Checksum : Stage.FrameEnd;
    }

    /// <summary>
    /// Gathers <paramref name="size"/> bytes into <see cref="_header"/>, across calls if need be.
    /// </summary>
    /// <returns>Whether they are all there; false when the source ran out first, all of it taken.</returns>
    private bool Gather(ReadOnlySpan<byte> source, ref int consumed, int size)
    {
        int count = Math.Min(size - _headerLength, source.Length - consumed);
        source.Slice(consumed, count).CopyTo(_header.AsSpan(_headerLength));
        _headerLength += count;
        consumed += count;
        if (_headerLength < size)
        {
            return false;
        }

        _headerLength = 0;
        return true;
    }
}
