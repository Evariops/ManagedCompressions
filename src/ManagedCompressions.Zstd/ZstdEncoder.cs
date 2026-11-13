using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>
/// Compresses data into Zstandard frames as it arrives, in fully managed code: the streaming
/// counterpart of <see cref="ZstdCompressor"/>, shaped as System.IO.Compression's <c>ZstandardEncoder</c>.
/// </summary>
/// <remarks>
/// <para>
/// The frames are those libzstd 1.5.7's <c>ZSTD_compressStream2</c> writes for the same calls, byte for
/// byte: <see cref="Compress"/> is <c>ZSTD_e_continue</c>, or <c>ZSTD_e_end</c> for the final block, and
/// <see cref="Flush"/> is <c>ZSTD_e_flush</c>. A streamed frame is not the one-shot frame of the same
/// content: the input is gathered a block at a time, each block compressed once it is whole, or at a
/// flush; and unless its size is known, from <see cref="SetSourceLength"/> or because the first call is
/// the final one, the frame takes the parameters of a source of unknown size, and its header declares
/// no size. A first call that is final, with room for <see cref="GetMaxCompressedLength"/> of its source,
/// writes the one-shot frame, as libzstd does.
/// </para>
/// <para>
/// The encoder keeps a window of input, up to the frame's window and a block (the level's window for a
/// stream of unknown size: 2 MiB at the default level), and the compressed block it has not handed out
/// yet. Once a frame ends, the next call starts another. It is not thread-safe.
/// </para>
/// <para>
/// The long-distance matcher runs as libzstd runs it: on at level 22 for a stream of unknown size,
/// whose window is then 2^27 bytes, or when <see cref="ZstdCompressionOptions.LongDistanceMatching"/>
/// asks for it. A dictionary is used as libzstd uses it, decided by the size pledged: a stream of
/// unknown size attaches it. A stream has no size limit: past 3500 MiB, the indices are corrected as
/// libzstd corrects them, which changes no byte.
/// </para>
/// </remarks>
public sealed unsafe class ZstdEncoder
{
    private readonly ZstdCompressor _compressor;

    private Stage _stage;

    /// <summary>libzstd's <c>pledgedSrcSizePlusOne</c> less one: the size pledged, or unknown.</summary>
    private ulong _pledged = CompressionParameters.UnknownSize;

    /// <summary>The first size of <see cref="_input"/>, which grows from there, doubling, as the input comes.</summary>
    private const int MinInputCapacity = 4 << 10;

    /// <summary>
    /// libzstd's <c>inBuff</c>: the window, and the block being gathered after it. Grown as the input
    /// comes, up to <see cref="_inputSize"/>, rather than allocated at that size before the first
    /// byte: a stream of a few kilobytes keeps a buffer of their size, not of the level's window.
    /// </summary>
    private byte[] _input = [];
    private byte* _inputStart;

    /// <summary>libzstd's <c>inBuffSize</c>: the frame's window and a block, where the input wraps.</summary>
    private int _inputSize;

    /// <summary>libzstd's <c>inBuffPos</c>: where the next byte goes.</summary>
    private int _inputPosition;

    /// <summary>libzstd's <c>inToCompress</c>: where the bytes not yet compressed start.</summary>
    private int _inputToCompress;

    /// <summary>libzstd's <c>inBuffTarget</c>: the end of the block being gathered.</summary>
    private int _inputTarget;

    /// <summary>
    /// libzstd's <c>outBuff</c>: a compressed block that did not fit the destination. Allocated by the
    /// first such block: a caller that gives room for a block, as <see cref="ZstdStream"/> does, never needs it.
    /// </summary>
    private byte[] _output = [];
    private byte* _outputStart;
    private int _outputContent;
    private int _outputFlushed;

    private int _blockSizeMax;
    private bool _frameEnded;
    private bool _failed;

    /// <summary>Creates an encoder at <see cref="ZstdCompressor.DefaultLevel"/>.</summary>
    public ZstdEncoder()
        : this(ZstdCompressor.DefaultLevel)
    {
    }

    /// <summary>Creates an encoder at <paramref name="level"/>.</summary>
    /// <param name="level">From <see cref="ZstdCompressor.MinLevel"/> to <see cref="ZstdCompressor.MaxLevel"/>; 0 is the default.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    public ZstdEncoder(int level)
    {
        _compressor = new ZstdCompressor(level);
    }

    /// <summary>Creates an encoder at <paramref name="level"/>, with a window of 2^<paramref name="windowLog"/> bytes.</summary>
    /// <param name="level">From <see cref="ZstdCompressor.MinLevel"/> to <see cref="ZstdCompressor.MaxLevel"/>; 0 is the default.</param>
    /// <param name="windowLog">From <see cref="ZstdCompressionOptions.MinWindowLog"/> to <see cref="ZstdCompressionOptions.MaxWindowLog"/>; 0 for the level's.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level or the window is out of range.</exception>
    public ZstdEncoder(int level, int windowLog)
    {
        _compressor = new ZstdCompressor(level, windowLog);
    }

    /// <summary>
    /// Creates an encoder with <paramref name="options"/>. A dictionary is prepared once, here, at the
    /// level, as <see cref="ZstdCompressor"/> prepares it; its frames are those libzstd streams with the
    /// dictionary prepared alike (<c>ZSTD_createCDict</c>, then <c>ZSTD_CCtx_refCDict</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Experimental features asked for with a dictionary.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An experimental feature is unknown.</exception>
    /// <exception cref="System.IO.InvalidDataException">A zstd-format dictionary whose tables are invalid.</exception>
    /// <exception cref="PlatformNotSupportedException">The processor is big-endian: see the README.</exception>
    public ZstdEncoder(ZstdCompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _compressor = new ZstdCompressor(options);
    }

    /// <summary>The compressor's <see cref="ZstdCompressor.CorrectOverflowFrequently"/>, for the tests.</summary>
    internal bool CorrectOverflowFrequently
    {
        get => _compressor.CorrectOverflowFrequently;
        set => _compressor.CorrectOverflowFrequently = value;
    }

    /// <summary>The compressor's <see cref="ZstdCompressor.OverflowCorrections"/>, for the tests.</summary>
    internal uint OverflowCorrections => _compressor.OverflowCorrections;

    /// <summary>The memory the encoder holds on to, its buffers and the compressor's tables, roughly: for <see cref="CodecPool"/>.</summary>
    internal long RetainedBytes => (long)_input.Length + _output.Length + _compressor.RetainedBytes;

    private enum Stage
    {
        /// <summary>No frame begun: the next call begins one, with the parameters it then knows.</summary>
        Init,

        /// <summary>Gathering input.</summary>
        Load,

        /// <summary>Handing out a compressed block that did not fit.</summary>
        Flush,
    }

    private enum Directive
    {
        Continue,
        Flush,
        End,
    }

    /// <summary>
    /// The largest frame <paramref name="inputLength"/> bytes become, however they are handed over:
    /// libzstd's <c>ZSTD_compressBound</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative, or its bound does not fit a <see cref="long"/>.</exception>
    public static long GetMaxCompressedLength(long inputLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputLength);
        UInt128 bound = (UInt128)(ulong)inputLength + ((ulong)inputLength >> 8)
            + (inputLength < FrameFormat.MaxBlockSize ? (ulong)(FrameFormat.MaxBlockSize - inputLength) >> 11 : 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bound, (UInt128)long.MaxValue, nameof(inputLength));
        return (long)bound;
    }

    /// <summary>
    /// <see cref="GetMaxCompressedLength"/> of a span's length, as each call asks it: in 64 bits, where
    /// it always fits, without the checks.
    /// </summary>
    private static long Bound(int length) =>
        length + ((long)length >> 8) + (length < FrameFormat.MaxBlockSize ? (FrameFormat.MaxBlockSize - length) >> 11 : 0);

    /// <summary>
    /// Pledges the size of the next frame's content, which the frame then declares (unless
    /// <see cref="ZstdCompressionOptions.WriteContentSize"/> is off) and its parameters fit, as
    /// <c>ZSTD_CCtx_setPledgedSrcSize</c>. A frame whose content differs from it is refused.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative.</exception>
    /// <exception cref="InvalidOperationException">A frame has begun.</exception>
    public void SetSourceLength(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (_stage != Stage.Init)
        {
            throw new InvalidOperationException("The source length is set before a frame begins.");
        }

        _pledged = (ulong)length;
    }

    /// <summary>
    /// Abandons the frame in progress and any refusal, as <c>ZSTD_CCtx_reset</c> does with
    /// <c>ZSTD_reset_session_only</c>: the next call begins a new frame, of unknown size. The options,
    /// the tables and the buffers are kept.
    /// </summary>
    public void Reset()
    {
        EndSession();
        _failed = false;
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/>, as far as the
    /// destination allows: <c>ZSTD_compressStream2</c> with <c>ZSTD_e_continue</c>, or with
    /// <c>ZSTD_e_end</c> when <paramref name="isFinalBlock"/>.
    /// </summary>
    /// <param name="source">The content's next bytes. An empty source that is not final does nothing.</param>
    /// <param name="destination">Where the frame's next bytes go.</param>
    /// <param name="bytesConsumed">The bytes of <paramref name="source"/> taken: gathered, or compressed.</param>
    /// <param name="bytesWritten">The bytes written to <paramref name="destination"/>.</param>
    /// <param name="isFinalBlock">Whether the source ends the frame: the frame is then written to its end.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when the source is taken whole and no compressed byte waits,
    /// and with <paramref name="isFinalBlock"/> the frame written to its end: the next call begins
    /// another. <see cref="OperationStatus.DestinationTooSmall"/> when the destination filled first: call
    /// again with the rest of the source. <see cref="OperationStatus.InvalidData"/> when the content
    /// differs from the size pledged (<see cref="SetSourceLength"/>): nothing is reported taken or
    /// written, and every call is refused until <see cref="Reset"/>.
    /// </returns>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
    {
        if (source.IsEmpty && !isFinalBlock)
        {
            // As System.IO.Compression's encoder: nothing to gather, nothing handed out.
            bytesConsumed = 0;
            bytesWritten = 0;
            return _failed ? OperationStatus.InvalidData : OperationStatus.Done;
        }

        return Run(source, destination, isFinalBlock ? Directive.End : Directive.Continue, out bytesConsumed, out bytesWritten);
    }

    /// <summary>
    /// Compresses what has been gathered, a block of its own, and hands out what waits:
    /// <c>ZSTD_compressStream2</c> with <c>ZSTD_e_flush</c>. The frame goes on.
    /// </summary>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once all of it is written; <see cref="OperationStatus.DestinationTooSmall"/>
    /// when the destination filled first: flush again. <see cref="OperationStatus.InvalidData"/> as for <see cref="Compress"/>.
    /// </returns>
    public OperationStatus Flush(Span<byte> destination, out int bytesWritten) =>
        Run(default, destination, Directive.Flush, out _, out bytesWritten);

    /// <summary>libzstd's <c>ZSTD_compressStream2</c> over <c>ZSTD_compressStream_generic</c>, with buffered input and output.</summary>
    private OperationStatus Run(ReadOnlySpan<byte> source, Span<byte> destination, Directive directive, out int bytesConsumed, out int bytesWritten)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        if (_failed)
        {
            return OperationStatus.InvalidData;
        }

        int ip = 0;
        int op = 0;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            if (_stage == Stage.Init)
            {
                BeginFrame(directive, source.Length);
            }

            bool moreWork = true;
            while (moreWork)
            {
                if (_stage == Stage.Load)
                {
                    if (directive == Directive.End
                        && destination.Length - op >= Bound(source.Length - ip)
                        && _inputPosition == 0)
                    {
                        // The shortcut: the rest of the source compressed straight into the destination.
                        int size = _compressor.CompressStreamChunk(src + ip, source.Length - ip, last: true, dst + op, destination.Length - op);
                        if (size < 0)
                        {
                            return Fail();
                        }

                        ip = source.Length;
                        op += size;
                        _frameEnded = true;
                        EndSession();
                        break;
                    }

                    // Gathers the block.
                    int loaded = Math.Min(_inputTarget - _inputPosition, source.Length - ip);
                    if (_inputPosition + loaded > _input.Length)
                    {
                        GrowInput(_inputPosition + loaded);
                    }

                    source.Slice(ip, loaded).CopyTo(new Span<byte>(_inputStart + _inputPosition, loaded));
                    _inputPosition += loaded;
                    ip += loaded;
                    if ((directive == Directive.Continue && _inputPosition < _inputTarget)
                        || (directive == Directive.Flush && _inputPosition == _inputToCompress))
                    {
                        break;
                    }

                    // Compresses it, into the destination when it has the room, else into the output buffer.
                    int chunk = _inputPosition - _inputToCompress;
                    bool direct = destination.Length - op >= Bound(chunk);
                    bool lastBlock = directive == Directive.End && ip == source.Length;
                    if (!direct && _output.Length == 0)
                    {
                        _output = GC.AllocateUninitializedArray<byte>((int)Bound(_blockSizeMax) + 1, pinned: true);
                        _outputStart = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_output));
                    }

                    int compressed = _compressor.CompressStreamChunk(
                        _inputStart + _inputToCompress,
                        chunk,
                        lastBlock,
                        direct ? dst + op : _outputStart,
                        direct ? destination.Length - op : _output.Length);
                    if (compressed < 0)
                    {
                        return Fail();
                    }

                    _frameEnded = lastBlock;
                    _inputTarget = _inputPosition + _blockSizeMax;
                    if (_inputTarget > _inputSize)
                    {
                        // The window wraps: the next block goes to the buffer's start.
                        _inputPosition = 0;
                        _inputTarget = _blockSizeMax;
                    }

                    _inputToCompress = _inputPosition;
                    if (direct)
                    {
                        op += compressed;
                        if (_frameEnded)
                        {
                            EndSession();
                            moreWork = false;
                        }

                        continue;
                    }

                    _outputContent = compressed;
                    _outputFlushed = 0;
                    _stage = Stage.Flush;
                }

                // Hands out the compressed block.
                int toFlush = _outputContent - _outputFlushed;
                int flushed = Math.Min(toFlush, destination.Length - op);
                new ReadOnlySpan<byte>(_outputStart + _outputFlushed, flushed).CopyTo(destination.Slice(op));
                op += flushed;
                _outputFlushed += flushed;
                if (flushed != toFlush)
                {
                    break;
                }

                _outputContent = 0;
                _outputFlushed = 0;
                if (_frameEnded)
                {
                    EndSession();
                    break;
                }

                _stage = Stage.Load;
            }
        }

        bytesConsumed = ip;
        bytesWritten = op;
        return ip < source.Length || _outputContent != _outputFlushed ? OperationStatus.DestinationTooSmall : OperationStatus.Done;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_CCtx_init_compressStream2</c>: a final first call pledges its source; the
    /// frame's parameters, the buffers sized for them, the first block's target. A block that is the
    /// whole pledged content is gathered a byte further, so that only the final call compresses it,
    /// as the last block, rather than a full block followed by an empty one.
    /// </summary>
    private void BeginFrame(Directive directive, int sourceLength)
    {
        if (directive == Directive.End)
        {
            _pledged = (ulong)sourceLength;
        }

        _compressor.BeginStream(_pledged, out _blockSizeMax, out long windowSize);
        long bufferSize = windowSize + _blockSizeMax;
        if (bufferSize > Array.MaxLength)
        {
            throw new NotSupportedException("ZstdEncoder keeps its window in an array, which a window of 2^31 bytes does not fit.");
        }

        _inputSize = (int)bufferSize;

        // A larger block than the output buffer was sized for: it is allocated again when needed.
        if (_output.Length < (int)Bound(_blockSizeMax) + 1)
        {
            _output = [];
        }

        _inputToCompress = 0;
        _inputPosition = 0;
        _inputTarget = _blockSizeMax + ((ulong)_blockSizeMax == _pledged ? 1 : 0);
        _outputContent = 0;
        _outputFlushed = 0;
        _frameEnded = false;
        _stage = Stage.Load;
    }

    /// <summary>
    /// Grows the input buffer to hold <paramref name="size"/> bytes: doubling up to a block, then, for a
    /// stream longer than that, the frame's whole <see cref="_inputSize"/> at once, so that a long stream
    /// leaves some hundred kilobytes behind, not a copy of its window. The window's bytes move with it,
    /// and the compressor's pointers into them: their indices stay, and so do the frame's bytes.
    /// </summary>
    private void GrowInput(int size)
    {
        int capacity = size > _blockSizeMax
            ? _inputSize
            : Math.Min(_blockSizeMax, Math.Max(size, Math.Max(2 * _input.Length, MinInputCapacity)));
        byte[] grown = GC.AllocateUninitializedArray<byte>(capacity, pinned: true);
        byte* start = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(grown));
        if (_input.Length != 0)
        {
            Buffer.MemoryCopy(_inputStart, start, capacity, _input.Length);
            _compressor.MoveStreamBuffer(_inputStart, start);
        }

        _input = grown;
        _inputStart = start;
    }

    /// <summary>libzstd's <c>ZSTD_CCtx_reset</c> with <c>ZSTD_reset_session_only</c>: no frame, no size pledged.</summary>
    private void EndSession()
    {
        _stage = Stage.Init;
        _pledged = CompressionParameters.UnknownSize;
        _outputContent = 0;
        _outputFlushed = 0;
    }

    /// <summary>The content differs from the size pledged: libzstd's <c>srcSize_wrong</c>, which sticks until <see cref="Reset"/>.</summary>
    private OperationStatus Fail()
    {
        _failed = true;
        return OperationStatus.InvalidData;
    }
}
