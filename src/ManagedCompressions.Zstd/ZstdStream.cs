using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace ManagedCompressions.Zstd;

/// <summary>
/// A <see cref="Stream"/> that decompresses Zstandard from another one, shaped as System.IO.Compression's
/// <c>ZstandardStream</c>: frame after frame, skippable ones skipped, through a <see cref="ZstdDecoder"/>.
/// It decompresses only, for now: compression comes with a streaming encoder.
/// </summary>
/// <remarks>
/// <para>
/// A read returns as soon as it has content, rather than wait for more of the underlying stream. The
/// end of the underlying stream ends this one, between frames; inside a frame, it is a truncation, and
/// the read throws <see cref="InvalidDataException"/>, as for any data that is not Zstandard.
/// </para>
/// <para>Not thread-safe, as streams are not.</para>
/// </remarks>
public sealed class ZstdStream : Stream
{
    /// <summary>The input read from the underlying stream at a time: a block and its header.</summary>
    private const int InputSize = (128 * 1024) + 3;

    private readonly ZstdDecoder _decoder;
    private readonly bool _leaveOpen;
    private Stream? _stream;
    private byte[]? _input;
    private int _inputStart;
    private int _inputEnd;
    private bool _ended;

    /// <summary>Decompresses <paramref name="stream"/>, which the stream disposes of with itself.</summary>
    /// <param name="stream">The compressed data.</param>
    /// <param name="mode"><see cref="CompressionMode.Decompress"/>: compression is not available yet.</param>
    public ZstdStream(Stream stream, CompressionMode mode)
        : this(stream, mode, leaveOpen: false)
    {
    }

    /// <summary>Decompresses <paramref name="stream"/>.</summary>
    /// <param name="stream">The compressed data.</param>
    /// <param name="mode"><see cref="CompressionMode.Decompress"/>: compression is not available yet.</param>
    /// <param name="leaveOpen">Whether <paramref name="stream"/> stays open when this stream is disposed of.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read, or <paramref name="mode"/> is not <see cref="CompressionMode.Decompress"/>.</exception>
    public ZstdStream(Stream stream, CompressionMode mode, bool leaveOpen)
        : this(stream, Decoder(mode), leaveOpen)
    {
    }

    /// <summary>Decompresses <paramref name="stream"/> with <paramref name="options"/>: a dictionary, a window limit.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    /// <exception cref="InvalidDataException">The dictionary has the zstd format but invalid tables.</exception>
    public ZstdStream(Stream stream, ZstdDecompressionOptions options, bool leaveOpen = false)
        : this(stream, new ZstdDecoder(options), leaveOpen)
    {
    }

    /// <summary>
    /// Decompresses <paramref name="stream"/> with <paramref name="decoder"/>, which the stream uses from
    /// its current state, and leaves to its owner.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="decoder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    public ZstdStream(Stream stream, ZstdDecoder decoder, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(decoder);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream cannot be read.", nameof(stream));
        }

        _stream = stream;
        _decoder = decoder;
        _leaveOpen = leaveOpen;
    }

    /// <summary>The stream the compressed data comes from.</summary>
    public Stream BaseStream => _stream ?? throw new ObjectDisposedException(nameof(ZstdStream));

    /// <inheritdoc/>
    public override bool CanRead => _stream is not null;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            if (Decode(buffer, out int written))
            {
                return written;
            }

            Fill(_stream.Read(InputBuffer().Span));
        }
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            if (Decode(buffer.Span, out int written))
            {
                return written;
            }

            Fill(await _stream.ReadAsync(InputBuffer(), cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>A read-only stream has nothing to flush.</summary>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stream? stream = _stream;
            _stream = null;
            if (stream is not null && !_leaveOpen)
            {
                stream.Dispose();
            }

            ReturnInput();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        Stream? stream = _stream;
        _stream = null;
        if (stream is not null && !_leaveOpen)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        ReturnInput();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ReturnInput()
    {
        if (_input is { } input)
        {
            _input = null;
            ArrayPool<byte>.Shared.Return(input);
        }
    }

    private static ZstdDecoder Decoder(CompressionMode mode) =>
        mode == CompressionMode.Decompress
            ? new ZstdDecoder()
            : throw new ArgumentException("ZstdStream decompresses only, for now.", nameof(mode));

    /// <summary>
    /// Decodes what the input holds into <paramref name="buffer"/>.
    /// </summary>
    /// <returns>
    /// Whether the read can return: some content written, the stream ended, or the buffer full; false
    /// when the input must be filled first.
    /// </returns>
    private bool Decode(Span<byte> buffer, out int written)
    {
        written = 0;
        while (true)
        {
            OperationStatus status = _decoder.Decompress(
                _input.AsSpan(_inputStart, _inputEnd - _inputStart), buffer.Slice(written), out int consumed, out int wrote);
            _inputStart += consumed;
            written += wrote;
            switch (status)
            {
                case OperationStatus.Done:
                    // The next frame, while there is input and room for its content.
                    if (written == buffer.Length || (_inputStart == _inputEnd && (written > 0 || _ended)))
                    {
                        return written > 0 || _ended;
                    }

                    break;

                case OperationStatus.DestinationTooSmall:
                    return true;

                case OperationStatus.NeedMoreData:
                    if (written > 0)
                    {
                        return true;
                    }

                    if (_ended)
                    {
                        return _decoder.InFrame
                            ? throw new InvalidDataException("The Zstandard stream ends inside a frame.")
                            : true;
                    }

                    return false;

                default:
                    throw new InvalidDataException($"The data is not valid Zstandard: {_decoder.LastError}.");
            }
        }
    }

    /// <summary>The input buffer, emptied, for the next read of the underlying stream.</summary>
    private Memory<byte> InputBuffer()
    {
        _input ??= ArrayPool<byte>.Shared.Rent(InputSize);
        _inputStart = 0;
        _inputEnd = 0;
        return _input.AsMemory(0, InputSize);
    }

    /// <summary>Takes the result of a read of the underlying stream: its byte count, 0 at its end.</summary>
    private void Fill(int read)
    {
        _inputEnd = read;
        _ended = read == 0;
    }
}
