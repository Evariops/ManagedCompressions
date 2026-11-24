using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedCompressions.Brotli.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// Decompresses a Brotli stream piece by piece, in fully managed code, as
/// <see cref="System.IO.Compression.BrotliDecoder"/> does: any size, from input that comes in pieces of
/// any size, into destinations of any size.
/// </summary>
/// <remarks>
/// <para>
/// The decoder keeps the stream's window, the last 1 KiB to 16 MiB of content, and up to twice the window
/// and a meta-block besides, so that it moves the window once per window of content at most. It reads
/// each byte of input once, whatever pieces the stream comes in: of the input, it keeps the part of a
/// meta-block's header it has yet to read whole (a prefix code, a few hundred bytes at most), and once the
/// commands start, the last 64 bytes, or the command the input ends inside; an uncompressed meta-block's
/// bytes are content as they arrive. It accepts and refuses the streams <see cref="BrotliDecompressor"/>
/// does, with the same <see cref="LastError"/>.
/// </para>
/// <para>
/// A decoder keeps its buffers from one stream to the next, after <see cref="Reset"/>. It is not
/// thread-safe: use one per thread.
/// </para>
/// </remarks>
public sealed unsafe class BrotliStreamDecoder : IDisposable
{
    private const int CompactThreshold = 1 << 16;

    private readonly BrotliDecompressor _decoder = new();
    private byte[] _input = [];
    private long _inputLength;
    private byte[] _history = [];
    private int _delivered;
    private bool _started;
    private bool _finished;
    private bool _failed;

    /// <summary>Creates a decoder.</summary>
    /// <exception cref="PlatformNotSupportedException">The processor is big-endian.</exception>
    public BrotliStreamDecoder() => LittleEndian.Require();

    /// <summary>Why the stream was refused: brotli's error code; <see cref="BrotliError.None"/> otherwise.</summary>
    public BrotliError LastError => _decoder.LastError;

    /// <summary>Whether the stream is decoded and all of its content written.</summary>
    public bool IsFinished => _finished && _delivered == _decoder.StreamPosition;

    /// <summary>The input the decoder keeps between calls, for the tests.</summary>
    internal long BufferedInput => _inputLength;

    /// <summary>Returns the buffers to <see cref="ArrayPool{T}.Shared"/>; the decoder takes new ones if it is used again.</summary>
    public void Dispose()
    {
        Return(_input);
        Return(_history);
        _input = [];
        _history = [];
        Reset();
    }

    private static void Return(byte[] array)
    {
        if (array.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    /// <summary>Starts a new stream, the buffers kept.</summary>
    public void Reset()
    {
        _started = false;
        _inputLength = 0;
        _delivered = 0;
        _finished = false;
        _failed = false;
    }

    /// <summary>
    /// Decodes <paramref name="source"/>, the stream's next bytes, into <paramref name="destination"/>, as
    /// <see cref="System.IO.Compression.BrotliDecoder.Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/> does.
    /// </summary>
    /// <param name="source">The stream's next bytes.</param>
    /// <param name="destination">Where the content's next bytes are written.</param>
    /// <param name="bytesConsumed">The bytes of the source taken: all of them, but those past the stream's end.</param>
    /// <param name="bytesWritten">The bytes of content written.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the stream is decoded and its content written;
    /// <see cref="OperationStatus.NeedMoreData"/> when the source is taken and the stream goes on: call
    /// again with its next bytes; <see cref="OperationStatus.DestinationTooSmall"/> when the destination is
    /// full: call again with more room, and the rest of the source; <see cref="OperationStatus.InvalidData"/>
    /// when the stream is invalid, which <see cref="LastError"/> says why.
    /// </returns>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        if (!_started)
        {
            _decoder.StreamReset();
            _started = true;
        }

        if (_failed)
        {
            return OperationStatus.InvalidData;
        }

        int appended = 0;
        if (!_finished)
        {
            Append(source);
            appended = source.Length;
        }

        OperationStatus status;
        while (true)
        {
            // The content decoded and not written yet, first.
            int pending = _decoder.StreamPosition - _delivered;
            if (pending > 0)
            {
                int n = Math.Min(pending, destination.Length - bytesWritten);
                _history.AsSpan(_delivered, n).CopyTo(destination[bytesWritten..]);
                _delivered += n;
                bytesWritten += n;
                if (n < pending)
                {
                    status = OperationStatus.DestinationTooSmall;
                    break;
                }
            }

            if (_finished)
            {
                status = OperationStatus.Done;
                break;
            }

            BrotliDecompressor.StreamResult result;
            fixed (byte* input = _input)
            fixed (byte* history = _history)
            {
                result = _decoder.StreamStep(input, _inputLength, history, _history.Length);
            }

            if (result == BrotliDecompressor.StreamResult.Progress)
            {
                continue;
            }

            if (result == BrotliDecompressor.StreamResult.NeedRoom)
            {
                MakeRoom();
                continue;
            }

            if (result == BrotliDecompressor.StreamResult.Finished)
            {
                _finished = true;
                continue;
            }

            if (result == BrotliDecompressor.StreamResult.Failed)
            {
                _failed = true;
                status = OperationStatus.InvalidData;
                break;
            }

            // The content this step decoded, before asking for more input.
            status = OperationStatus.NeedMoreData;
            int decoded = _decoder.StreamPosition - _delivered;
            int room = Math.Min(decoded, destination.Length - bytesWritten);
            _history.AsSpan(_delivered, room).CopyTo(destination[bytesWritten..]);
            _delivered += room;
            bytesWritten += room;
            if (room < decoded)
            {
                status = OperationStatus.DestinationTooSmall;
            }

            break;
        }

        bytesConsumed = appended;
        if (_finished)
        {
            // Nothing past the stream's end.
            long unused = _inputLength - _decoder.StreamEnd;
            bytesConsumed = (int)Math.Max(0, appended - unused);
            _inputLength = _decoder.StreamEnd;
        }
        else if (status == OperationStatus.DestinationTooSmall)
        {
            // Back to the caller, the input the state does not hold yet: it comes again with the rest.
            long giveBack = Math.Min(appended, _inputLength - _decoder.StreamFrontier);
            _inputLength -= giveBack;
            bytesConsumed = appended - (int)giveBack;
        }

        Compact();
        return status;
    }

    /// <summary>The source after the input kept, the buffer doubled when it is full.</summary>
    private void Append(ReadOnlySpan<byte> source)
    {
        long needed = _inputLength + source.Length;
        if (needed > _input.Length)
        {
            long size = Math.Max(needed, Math.Max(_input.Length * 2L, 4096));
            byte[] input = ArrayPool<byte>.Shared.Rent(checked((int)Math.Min(size, Array.MaxLength)));
            _input.AsSpan(0, (int)_inputLength).CopyTo(input);
            Return(_input);
            _input = input;
        }

        source.CopyTo(_input.AsSpan((int)_inputLength));
        _inputLength = needed;
    }

    /// <summary>The input the state no longer reads dropped, once it is enough to be worth a copy.</summary>
    private void Compact()
    {
        long frontier = Math.Min(_decoder.StreamFrontier, _inputLength);
        if (frontier >= CompactThreshold || (frontier > 0 && frontier * 2 >= _inputLength))
        {
            _input.AsSpan((int)frontier, (int)(_inputLength - frontier)).CopyTo(_input);
            _inputLength -= frontier;
            _decoder.StreamDropInput(frontier);
        }
    }

    /// <summary>
    /// Room for the next meta-block in the history: the content past the window dropped when that leaves
    /// room for the meta-block and for a window more, so that the window moves once per window of
    /// content at most; otherwise a buffer twice as large, which keeps the window.
    /// </summary>
    private void MakeRoom()
    {
        int need = _decoder.StreamRoomNeeded;
        int pos = _decoder.StreamPosition;
        int keep = Math.Min(pos, _decoder.StreamWindow);
        int drop = pos - keep;
        if (drop > 0 && _history.Length - keep >= Math.Max(need, keep))
        {
            _history.AsSpan(drop, keep).CopyTo(_history);
        }
        else
        {
            // At once a window more than the meta-block, so that the window moves once per window of content.
            long size = Math.Max((long)keep + need, Math.Max(_history.Length * 2L, (long)_decoder.StreamWindow + need));
            byte[] history = ArrayPool<byte>.Shared.Rent(checked((int)Math.Min(size, Array.MaxLength)));
            _history.AsSpan(drop, keep).CopyTo(history);
            Return(_history);
            _history = history;
        }

        _decoder.StreamDropHistory(drop);
        _delivered -= drop;
    }
}
