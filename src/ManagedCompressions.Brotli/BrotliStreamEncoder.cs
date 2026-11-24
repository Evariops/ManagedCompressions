using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedCompressions.Brotli.Internal;
using ManagedCompressions.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// Compresses into a Brotli stream piece by piece, in fully managed code: the bytes
/// <see cref="System.IO.Compression.BrotliEncoder"/> writes given the same calls, those of brotli 1.1.1's
/// <c>BrotliEncoderCompressStream</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Compress"/> takes input and writes what brotli has ready; brotli keeps a block of input, and
/// at qualities 4 to 11 merges blocks into meta-blocks, before it writes them. With
/// <c>isFinalBlock</c>, it ends the stream once the source is consumed. <see cref="Flush"/> writes
/// everything taken so far, ending on a byte boundary, as brotli's <c>BROTLI_OPERATION_FLUSH</c> does:
/// a decoder given the stream so far decodes all of the content so far.
/// </para>
/// <para>
/// An encoder keeps its tables and buffers from one stream to the next, after <see cref="Reset"/>. It is
/// not thread-safe: use one per thread.
/// </para>
/// </remarks>
public sealed unsafe class BrotliStreamEncoder
{
    private const int OperationProcess = 0;
    private const int OperationFlush = 1;
    private const int OperationFinish = 2;

    private readonly byte[] _tinyArray = GC.AllocateArray<byte>(16, pinned: true);
    private EncoderState? _state;
    private FastEncoder? _fast;
    private bool _initialized;
    private StreamState _streamState;

    /// <summary>The output written and not taken yet: brotli's <c>next_out_</c> and <c>available_out_</c>.</summary>
    private byte* _nextOut;
    private nuint _availableOut;

    /// <summary>The last byte's bits at qualities 0 and 1, which keep no <see cref="EncoderState"/>.</summary>
    private ushort _fastLastBytes;
    private int _fastLastBytesBits;

    /// <summary>Creates an encoder at <see cref="BrotliCompressor.DefaultQuality"/> and <see cref="BrotliCompressor.DefaultWindowLog"/>.</summary>
    public BrotliStreamEncoder()
        : this(BrotliCompressor.DefaultQuality)
    {
    }

    /// <summary>Creates an encoder.</summary>
    /// <param name="quality">From <see cref="BrotliCompressor.MinQuality"/> to <see cref="BrotliCompressor.MaxQuality"/>.</param>
    /// <param name="windowLog">The window, as a power of two, from <see cref="BrotliCompressor.MinWindowLog"/> to <see cref="BrotliCompressor.MaxWindowLog"/>.</param>
    /// <param name="mode">What the input is.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is out of its range.</exception>
    /// <exception cref="PlatformNotSupportedException">The processor is big-endian.</exception>
    public BrotliStreamEncoder(int quality, int windowLog = BrotliCompressor.DefaultWindowLog, BrotliMode mode = BrotliMode.Generic)
    {
        LittleEndian.Require();
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, BrotliCompressor.MinQuality);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, BrotliCompressor.MaxQuality);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowLog, BrotliCompressor.MinWindowLog);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(windowLog, BrotliCompressor.MaxWindowLog);
        if (mode is not (BrotliMode.Generic or BrotliMode.Text or BrotliMode.Font))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a Brotli mode");
        }

        Quality = quality;
        WindowLog = windowLog;
        Mode = mode;
    }

    private enum StreamState
    {
        Processing,
        FlushRequested,
        Finished,
    }

    /// <summary>The quality, from <see cref="BrotliCompressor.MinQuality"/> to <see cref="BrotliCompressor.MaxQuality"/>.</summary>
    public int Quality { get; }

    /// <summary>The window, as a power of two.</summary>
    public int WindowLog { get; }

    /// <summary>What the input is.</summary>
    public BrotliMode Mode { get; }

    /// <summary>Whether the stream is ended: a call with <c>isFinalBlock</c> has written all of it.</summary>
    public bool IsFinished => _initialized && _streamState == StreamState.Finished && _availableOut == 0;

    private ushort LastBytes
    {
        get => Quality <= 1 ? _fastLastBytes : _state!.LastBytes;
        set
        {
            if (Quality <= 1)
            {
                _fastLastBytes = value;
            }
            else
            {
                _state!.LastBytes = value;
            }
        }
    }

    private int LastBytesBits
    {
        get => Quality <= 1 ? _fastLastBytesBits : _state!.LastBytesBits;
        set
        {
            if (Quality <= 1)
            {
                _fastLastBytesBits = value;
            }
            else
            {
                _state!.LastBytesBits = value;
            }
        }
    }

    /// <summary>Starts a new stream, with the same parameters, the tables and buffers kept.</summary>
    public void Reset() => _initialized = false;

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/>, as
    /// <see cref="System.IO.Compression.BrotliEncoder.Compress(ReadOnlySpan{byte}, Span{byte}, out int, out int, bool)"/> does.
    /// </summary>
    /// <param name="source">The next piece of content.</param>
    /// <param name="destination">Where the stream's next bytes are written.</param>
    /// <param name="bytesConsumed">The bytes of the source taken.</param>
    /// <param name="bytesWritten">The bytes of the stream written.</param>
    /// <param name="isFinalBlock">Whether the source is the content's end: the stream is then ended.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the source is taken and the output ready written;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when the destination is full: call again with
    /// the rest of the source and more room; <see cref="OperationStatus.InvalidData"/> for input after the
    /// stream's end, or during a flush that has not written everything.
    /// </returns>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
    {
        OperationStatus status = CompressCore(source, destination, out bytesConsumed, out bytesWritten, isFinalBlock ? OperationFinish : OperationProcess);

        // The tables and the output waiting, read through pointers, are pinned arrays only the encoder
        // holds: it stays alive until the call is done, whatever the code after the loops uses of it.
        GC.KeepAlive(this);
        return status;
    }

    /// <summary>
    /// Writes everything taken so far, ending on a byte boundary, as
    /// <see cref="System.IO.Compression.BrotliEncoder.Flush(Span{byte}, out int)"/> does.
    /// </summary>
    /// <returns><see cref="OperationStatus.Done"/>, or <see cref="OperationStatus.DestinationTooSmall"/> when the destination is full: call again.</returns>
    public OperationStatus Flush(Span<byte> destination, out int bytesWritten)
    {
        OperationStatus status = CompressCore(default, destination, out _, out bytesWritten, OperationFlush);
        GC.KeepAlive(this);
        return status;
    }

    /// <summary>The loop of .NET's <c>BrotliEncoder.Compress</c> around <c>BrotliEncoderCompressStream</c>.</summary>
    private OperationStatus CompressCore(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, int operation)
    {
        EnsureInitialized();
        bytesConsumed = 0;
        bytesWritten = 0;
        nuint availableOut = (nuint)destination.Length;
        nuint availableIn = (nuint)source.Length;
        fixed (byte* src = &MemoryMarshal.GetReference(source))
        fixed (byte* dst = &MemoryMarshal.GetReference(destination))
        {
            byte* nextIn = src;
            byte* nextOut = dst;
            while ((int)availableOut > 0)
            {
                nuint inBefore = availableIn;
                nuint outBefore = availableOut;
                if (!CompressStream(operation, ref availableIn, ref nextIn, ref availableOut, ref nextOut))
                {
                    return OperationStatus.InvalidData;
                }

                bytesConsumed += (int)(inBefore - availableIn);
                bytesWritten += (int)(outBefore - availableOut);

                // Nothing written, no input left and no output pending: done.
                if (availableOut == outBefore && _availableOut == 0 && availableIn == 0)
                {
                    return OperationStatus.Done;
                }
            }

            return OperationStatus.DestinationTooSmall;
        }
    }

    /// <summary>brotli's <c>EnsureInitialized</c>.</summary>
    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        if (Quality <= 1)
        {
            (_fast ??= new FastEncoder()).Reset(Quality);
            EncoderState.EncodeWindowBits(Math.Max(WindowLog, 18), out _fastLastBytes, out _fastLastBytesBits);
        }
        else
        {
            (_state ??= new EncoderState()).Reset(Quality, WindowLog, Mode, 0);
        }

        _streamState = StreamState.Processing;
        _nextOut = null;
        _availableOut = 0;
        _initialized = true;
    }

    /// <summary>brotli's <c>BrotliEncoderCompressStream</c>, without metadata: false where brotli fails.</summary>
    private bool CompressStream(int operation, ref nuint availableIn, ref byte* nextIn, ref nuint availableOut, ref byte* nextOut)
    {
        if (_streamState != StreamState.Processing && availableIn != 0)
        {
            return false;
        }

        if (Quality <= 1)
        {
            return CompressStreamFast(operation, ref availableIn, ref nextIn, ref availableOut, ref nextOut);
        }

        EncoderState state = _state!;
        while (true)
        {
            nuint remainingBlockSize = state.RemainingInputBlockSize;
            if (remainingBlockSize != 0 && availableIn != 0)
            {
                nuint copy = Math.Min(remainingBlockSize, availableIn);
                state.CopyInputToRingBuffer(nextIn, copy);
                nextIn += copy;
                availableIn -= copy;
                continue;
            }

            if (InjectFlushOrPushOutput(ref availableOut, ref nextOut))
            {
                continue;
            }

            // A block only when no output is pending, the stream is not ended, and no flush is pending.
            if (_availableOut == 0 && _streamState == StreamState.Processing && (remainingBlockSize == 0 || operation != OperationProcess))
            {
                bool isLast = availableIn == 0 && operation == OperationFinish;
                bool forceFlush = availableIn == 0 && operation == OperationFlush;
                state.UpdateSizeHint(availableIn);
                _availableOut = state.EncodeData(isLast, forceFlush, out _nextOut);
                if (forceFlush)
                {
                    _streamState = StreamState.FlushRequested;
                }

                if (isLast)
                {
                    _streamState = StreamState.Finished;
                }

                continue;
            }

            break;
        }

        CheckFlushComplete();
        return true;
    }

    /// <summary>
    /// brotli's <c>BrotliEncoderCompressStreamFast</c>, qualities 0 and 1: each piece of input, up to the
    /// window, compressed at once, into the destination when it has room for the worst case.
    /// </summary>
    private bool CompressStreamFast(int operation, ref nuint availableIn, ref byte* nextIn, ref nuint availableOut, ref byte* nextOut)
    {
        FastEncoder fast = _fast!;
        nuint blockSizeLimit = (nuint)1 << WindowLog;
        while (true)
        {
            if (InjectFlushOrPushOutput(ref availableOut, ref nextOut))
            {
                continue;
            }

            if (_availableOut == 0 && _streamState == StreamState.Processing && (availableIn != 0 || operation != OperationProcess))
            {
                nuint blockSize = Math.Min(blockSizeLimit, availableIn);
                bool isLast = availableIn == blockSize && operation == OperationFinish;
                bool forceFlush = availableIn == blockSize && operation == OperationFlush;
                nuint maxOutSize = (2 * blockSize) + 503;
                if (forceFlush && blockSize == 0)
                {
                    _streamState = StreamState.FlushRequested;
                    continue;
                }

                bool inPlace = maxOutSize <= availableOut;
                byte* storage = inPlace ? nextOut : fast.Storage(maxOutSize);
                storage[0] = (byte)_fastLastBytes;
                storage[1] = (byte)(_fastLastBytes >> 8);
                int storageIx = _fastLastBytesBits;
                fast.CompressFragment(nextIn, (int)blockSize, isLast, ref storageIx, storage);
                EncoderState.AssertWithinStorage(storageIx, (nuint)blockSize);
                nextIn += blockSize;
                availableIn -= blockSize;
                nuint outBytes = (nuint)(storageIx >> 3);
                if (inPlace)
                {
                    nextOut += outBytes;
                    availableOut -= outBytes;
                }
                else
                {
                    _nextOut = storage;
                    _availableOut = outBytes;
                }

                _fastLastBytes = storage[storageIx >> 3];
                _fastLastBytesBits = storageIx & 7;
                if (forceFlush)
                {
                    _streamState = StreamState.FlushRequested;
                }

                if (isLast)
                {
                    _streamState = StreamState.Finished;
                }

                continue;
            }

            break;
        }

        CheckFlushComplete();
        return true;
    }

    /// <summary>brotli's <c>InjectFlushOrPushOutput</c>: false when it does nothing.</summary>
    private bool InjectFlushOrPushOutput(ref nuint availableOut, ref byte* nextOut)
    {
        if (_streamState == StreamState.FlushRequested && LastBytesBits != 0)
        {
            InjectBytePaddingBlock();
            return true;
        }

        if (_availableOut != 0 && availableOut != 0)
        {
            nuint copy = Math.Min(_availableOut, availableOut);
            Buffer.MemoryCopy(_nextOut, nextOut, copy, copy);
            nextOut += copy;
            availableOut -= copy;
            _nextOut += copy;
            _availableOut -= copy;
            return true;
        }

        return false;
    }

    /// <summary>
    /// brotli's <c>InjectBytePaddingBlock</c>: the last byte's bits, then an empty metadata block that
    /// ends the stream so far on a byte boundary.
    /// </summary>
    private void InjectBytePaddingBlock()
    {
        uint seal = LastBytes;
        int sealBits = LastBytesBits;
        LastBytes = 0;
        LastBytesBits = 0;

        // ISLAST 0, MNIBBLES 11 (metadata), reserved 0, MSKIPBYTES 00.
        seal |= 0x6u << sealBits;
        sealBits += 6;
        byte* destination;
        if (_nextOut != null)
        {
            destination = _nextOut + _availableOut;
        }
        else
        {
            destination = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_tinyArray));
            _nextOut = destination;
        }

        destination[0] = (byte)seal;
        if (sealBits > 8)
        {
            destination[1] = (byte)(seal >> 8);
        }

        if (sealBits > 16)
        {
            destination[2] = (byte)(seal >> 16);
        }

        _availableOut += (nuint)((sealBits + 7) >> 3);
    }

    /// <summary>brotli's <c>CheckFlushComplete</c>.</summary>
    private void CheckFlushComplete()
    {
        if (_streamState == StreamState.FlushRequested && _availableOut == 0)
        {
            _streamState = StreamState.Processing;
            _nextOut = null;
        }
    }
}
