using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace ManagedCompressions.Brotli;

/// <summary>
/// A <see cref="Stream"/> that compresses what is written to it into a Brotli stream, or decompresses the
/// Brotli stream it reads, in fully managed code, as <see cref="BrotliStream"/> does.
/// </summary>
/// <remarks>
/// <para>
/// Writing compresses through <see cref="BrotliStreamEncoder"/>: the bytes <see cref="BrotliStream"/> writes
/// for the same writes, flushes and quality. Disposing the stream ends the Brotli stream. Reading
/// decompresses through <see cref="BrotliStreamDecoder"/>, and throws <see cref="InvalidDataException"/> for
/// an invalid or truncated stream.
/// </para>
/// <para>
/// The name does not clash with <see cref="BrotliStream"/>, so that both namespaces can be imported.
/// </para>
/// </remarks>
public sealed class BrotliCompressionStream : Stream
{
    /// <summary>The size of the buffer between the codec and the underlying stream, .NET's.</summary>
    private const int BufferSize = 65520;

    private readonly bool _leaveOpen;
    private readonly BrotliStreamEncoder? _encoder;
    private readonly BrotliStreamDecoder? _decoder;
    private byte[]? _buffer;
    private int _bufferOffset;
    private int _bufferCount;
    private bool _endOfInput;
    private bool _disposed;

    /// <summary>A stream that compresses at .NET's default quality, 4, or decompresses.</summary>
    /// <param name="stream">The underlying stream.</param>
    /// <param name="mode">Whether to compress into <paramref name="stream"/> or decompress from it.</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open once this stream is disposed.</param>
    public BrotliCompressionStream(Stream stream, CompressionMode mode, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        BaseStream = stream;
        _leaveOpen = leaveOpen;
        switch (mode)
        {
            case CompressionMode.Compress:
                if (!stream.CanWrite)
                {
                    throw new ArgumentException("the stream is not writable", nameof(stream));
                }

                _encoder = new BrotliStreamEncoder();
                break;
            case CompressionMode.Decompress:
                if (!stream.CanRead)
                {
                    throw new ArgumentException("the stream is not readable", nameof(stream));
                }

                _decoder = new BrotliStreamDecoder();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a compression mode");
        }
    }

    /// <summary>
    /// A stream that compresses at the quality of <paramref name="level"/>, as <see cref="BrotliStream"/>
    /// maps it: <see cref="CompressionLevel.NoCompression"/> 0, <see cref="CompressionLevel.Fastest"/> 1,
    /// <see cref="CompressionLevel.Optimal"/> 4 and <see cref="CompressionLevel.SmallestSize"/> 11.
    /// </summary>
    public BrotliCompressionStream(Stream stream, CompressionLevel level, bool leaveOpen = false)
        : this(stream, Quality(level), BrotliCompressor.DefaultWindowLog, BrotliMode.Generic, leaveOpen)
    {
    }

    /// <summary>A stream that compresses at a quality, a window and a mode.</summary>
    /// <param name="stream">The underlying stream.</param>
    /// <param name="quality">From <see cref="BrotliCompressor.MinQuality"/> to <see cref="BrotliCompressor.MaxQuality"/>.</param>
    /// <param name="windowLog">The window, as a power of two, from <see cref="BrotliCompressor.MinWindowLog"/> to <see cref="BrotliCompressor.MaxWindowLog"/>.</param>
    /// <param name="mode">What the input is.</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open once this stream is disposed.</param>
    public BrotliCompressionStream(Stream stream, int quality, int windowLog = BrotliCompressor.DefaultWindowLog, BrotliMode mode = BrotliMode.Generic, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("the stream is not writable", nameof(stream));
        }

        BaseStream = stream;
        _leaveOpen = leaveOpen;
        _encoder = new BrotliStreamEncoder(quality, windowLog, mode);
    }

    /// <summary>The underlying stream.</summary>
    public Stream BaseStream { get; }

    /// <inheritdoc/>
    public override bool CanRead => !_disposed && _decoder is not null && BaseStream.CanRead;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed && _encoder is not null && BaseStream.CanWrite;

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
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        BrotliStreamDecoder decoder = DecoderOrThrow();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        byte[] input = _buffer ??= new byte[BufferSize];
        while (true)
        {
            if (TryDecode(decoder, buffer, out int written))
            {
                return written;
            }

            int read = BaseStream.Read(input, 0, input.Length);
            NoteRead(read);
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
        BrotliStreamDecoder decoder = DecoderOrThrow();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        byte[] input = _buffer ??= new byte[BufferSize];
        while (true)
        {
            if (TryDecode(decoder, buffer.Span, out int written))
            {
                return written;
            }

            int read = await BaseStream.ReadAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            NoteRead(read);
        }
    }

    /// <summary>
    /// Decodes the input buffered into <paramref name="destination"/>: true with the bytes written, or with
    /// 0 at the stream's end; false when the decoder needs more input.
    /// </summary>
    private bool TryDecode(BrotliStreamDecoder decoder, Span<byte> destination, out int written)
    {
        OperationStatus status = decoder.Decompress(_buffer.AsSpan(_bufferOffset, _bufferCount), destination, out int consumed, out written);
        _bufferOffset += consumed;
        _bufferCount -= consumed;
        switch (status)
        {
            case OperationStatus.InvalidData:
                throw new InvalidDataException($"invalid Brotli stream: {decoder.LastError}");
            case OperationStatus.NeedMoreData when written == 0:
                if (_endOfInput)
                {
                    throw new InvalidDataException("truncated Brotli stream");
                }

                return false;
            default:
                return true;
        }
    }

    private void NoteRead(int read)
    {
        _bufferOffset = 0;
        _bufferCount = read;
        _endOfInput = read == 0;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value) => Write([value]);

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        BrotliStreamEncoder encoder = EncoderOrThrow();
        byte[] output = _buffer ??= new byte[BufferSize];
        OperationStatus status;
        do
        {
            status = encoder.Compress(buffer, output, out int consumed, out int written, isFinalBlock: false);
            ThrowIfInvalid(status);
            buffer = buffer[consumed..];
            BaseStream.Write(output, 0, written);
        }
        while (status == OperationStatus.DestinationTooSmall || !buffer.IsEmpty);
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BrotliStreamEncoder encoder = EncoderOrThrow();
        byte[] output = _buffer ??= new byte[BufferSize];
        OperationStatus status;
        do
        {
            status = encoder.Compress(buffer.Span, output, out int consumed, out int written, isFinalBlock: false);
            ThrowIfInvalid(status);
            buffer = buffer[consumed..];
            await BaseStream.WriteAsync(output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        }
        while (status == OperationStatus.DestinationTooSmall || !buffer.IsEmpty);
    }

    /// <summary>Writes everything written so far into the underlying stream, ending on a byte boundary, then flushes it.</summary>
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_encoder is not null)
        {
            byte[] output = _buffer ??= new byte[BufferSize];
            OperationStatus status;
            do
            {
                status = _encoder.Flush(output, out int written);
                ThrowIfInvalid(status);
                BaseStream.Write(output, 0, written);
            }
            while (status == OperationStatus.DestinationTooSmall);

            BaseStream.Flush();
        }
    }

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_encoder is not null)
        {
            byte[] output = _buffer ??= new byte[BufferSize];
            OperationStatus status;
            do
            {
                status = _encoder.Flush(output, out int written);
                ThrowIfInvalid(status);
                await BaseStream.WriteAsync(output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }
            while (status == OperationStatus.DestinationTooSmall);

            await BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ends the Brotli stream when compressing, then disposes of the underlying stream unless it is left open.</summary>
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && !_disposed && _encoder is not null)
            {
                byte[] output = _buffer ??= new byte[BufferSize];
                OperationStatus status;
                do
                {
                    status = _encoder.Compress(default, output, out _, out int written, isFinalBlock: true);
                    ThrowIfInvalid(status);
                    BaseStream.Write(output, 0, written);
                }
                while (status == OperationStatus.DestinationTooSmall);
            }
        }
        finally
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _decoder?.Dispose();
                if (!_leaveOpen)
                {
                    BaseStream.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Ends the Brotli stream when compressing, then disposes of the underlying stream unless it is left open.</summary>
    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (!_disposed && _encoder is not null)
            {
                byte[] output = _buffer ??= new byte[BufferSize];
                OperationStatus status;
                do
                {
                    status = _encoder.Compress(default, output, out _, out int written, isFinalBlock: true);
                    ThrowIfInvalid(status);
                    await BaseStream.WriteAsync(output.AsMemory(0, written)).ConfigureAwait(false);
                }
                while (status == OperationStatus.DestinationTooSmall);
            }
        }
        finally
        {
            if (!_disposed)
            {
                _disposed = true;
                _decoder?.Dispose();
                if (!_leaveOpen)
                {
                    await BaseStream.DisposeAsync().ConfigureAwait(false);
                }
            }

            GC.SuppressFinalize(this);
        }
    }

    private static int Quality(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => 0,
        CompressionLevel.Fastest => 1,
        CompressionLevel.Optimal => BrotliCompressor.DefaultQuality,
        CompressionLevel.SmallestSize => BrotliCompressor.MaxQuality,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "not a compression level"),
    };

    private static void ThrowIfInvalid(OperationStatus status)
    {
        if (status == OperationStatus.InvalidData)
        {
            throw new InvalidOperationException("the Brotli encoder refused the call");
        }
    }

    private BrotliStreamDecoder DecoderOrThrow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _decoder ?? throw new InvalidOperationException("the stream compresses: it cannot be read");
    }

    private BrotliStreamEncoder EncoderOrThrow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _encoder ?? throw new InvalidOperationException("the stream decompresses: it cannot be written");
    }
}
