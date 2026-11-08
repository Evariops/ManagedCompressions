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
/// Not yet streamed: dictionaries, the long-distance matcher (which libzstd turns on by itself at level
/// 22 for a stream of unknown size), and frames past 3500 MiB, where libzstd corrects its indices. Those
/// throw <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed unsafe class ZstdEncoder
{
    private readonly ZstdCompressor _compressor;

    private Stage _stage;

    /// <summary>libzstd's <c>pledgedSrcSizePlusOne</c> less one: the size pledged, or unknown.</summary>
    private ulong _pledged = CompressionParameters.UnknownSize;

    /// <summary>libzstd's <c>inBuff</c>: the window, and the block being gathered after it.</summary>
    private byte[] _input = [];
    private byte* _inputStart;

    /// <summary>libzstd's <c>inBuffSize</c>: the frame's window and a block.</summary>
    private int _inputSize;

    /// <summary>libzstd's <c>inBuffPos</c>: where the next byte goes.</summary>
    private int _inputPosition;

    /// <summary>libzstd's <c>inToCompress</c>: where the bytes not yet compressed start.</summary>
    private int _inputToCompress;

    /// <summary>libzstd's <c>inBuffTarget</c>: the end of the block being gathered.</summary>
    private int _inputTarget;

    /// <summary>libzstd's <c>outBuff</c>: a compressed block that did not fit the destination.</summary>
    private byte[] _output = [];
    private byte* _outputStart;
    private int _outputContent;
    private int _outputFlushed;

    private int _blockSizeMax;
    private bool _frameEnded;
    private bool _failed;

    /// <summary>Creates an encoder at <see cref="ZstdCompressor.DefaultLevel"/>.</summary>
    public ZstdEncoder()
        : this(new ZstdCompressionOptions())
    {
    }

    /// <summary>Creates an encoder at <paramref name="level"/>.</summary>
    /// <param name="level">From <see cref="ZstdCompressor.MinLevel"/> to <see cref="ZstdCompressor.MaxLevel"/>; 0 is the default.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    public ZstdEncoder(int level)
        : this(new ZstdCompressionOptions { Level = level })
    {
    }

    /// <summary>Creates an encoder at <paramref name="level"/>, with a window of 2^<paramref name="windowLog"/> bytes.</summary>
    /// <param name="level">From <see cref="ZstdCompressor.MinLevel"/> to <see cref="ZstdCompressor.MaxLevel"/>; 0 is the default.</param>
    /// <param name="windowLog">From <see cref="ZstdCompressionOptions.MinWindowLog"/> to <see cref="ZstdCompressionOptions.MaxWindowLog"/>; 0 for the level's.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level or the window is out of range.</exception>
    public ZstdEncoder(int level, int windowLog)
        : this(new ZstdCompressionOptions { Level = level, WindowLog = windowLog })
    {
    }

    /// <summary>Creates an encoder with <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An experimental feature is unknown.</exception>
    /// <exception cref="NotSupportedException">A dictionary, or the long-distance matcher, asked for.</exception>
    /// <exception cref="PlatformNotSupportedException">The processor is big-endian: see the README.</exception>
    public ZstdEncoder(ZstdCompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Dictionary.IsEmpty)
        {
            throw new NotSupportedException("ZstdEncoder does not compress with a dictionary yet.");
        }

        if (options.LongDistanceMatching == true)
        {
            throw new NotSupportedException("ZstdEncoder does not run the long-distance matcher yet.");
        }

        _compressor = new ZstdCompressor(options);
    }

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
                        && destination.Length - op >= GetMaxCompressedLength(source.Length - ip)
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
                    bool direct = destination.Length - op >= GetMaxCompressedLength(chunk);
                    bool lastBlock = directive == Directive.End && ip == source.Length;
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
        if (_input.Length < _inputSize)
        {
            _input = GC.AllocateUninitializedArray<byte>(_inputSize, pinned: true);
            _inputStart = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_input));
        }

        int outputSize = (int)GetMaxCompressedLength(_blockSizeMax) + 1;
        if (_output.Length < outputSize)
        {
            _output = GC.AllocateUninitializedArray<byte>(outputSize, pinned: true);
            _outputStart = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_output));
        }

        _inputToCompress = 0;
        _inputPosition = 0;
        _inputTarget = _blockSizeMax + ((ulong)_blockSizeMax == _pledged ? 1 : 0);
        _outputContent = 0;
        _outputFlushed = 0;
        _frameEnded = false;
        _stage = Stage.Load;
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
