using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>
/// A <see cref="Stream"/> that compresses into, or decompresses from, another one, shaped as
/// System.IO.Compression's <c>ZstandardStream</c>: compression through a <see cref="ZstdEncoder"/>, the
/// frames of libzstd's streaming compressor, byte for byte; decompression through a
/// <see cref="ZstdDecoder"/>, frame after frame, skippable ones skipped.
/// </summary>
/// <remarks>
/// <para>
/// Compressing, the stream writes one frame, which <see cref="Flush()"/> ends a block of
/// (<c>ZSTD_e_flush</c>) and disposing ends (<c>ZSTD_e_end</c>), even when nothing was written:
/// the frame is then empty. Its bytes are those of <c>ZstandardStream</c> for the same writes and flushes.
/// </para>
/// <para>
/// Decompressing, a read returns as soon as it has content, rather than wait for more of the
/// underlying stream. The end of the underlying stream ends this one, between frames; inside a frame,
/// it is a truncation, and the read throws <see cref="InvalidDataException"/>, as for any data that is
/// not Zstandard.
/// </para>
/// <para>
/// A stream created without an encoder or a decoder of the caller's takes one, of its settings, from a
/// pool the library keeps, and puts it back, reset, once disposed of cleanly: a stream per request or
/// per file reuses the tables and buffers of the streams before it rather than allocate its own, some
/// megabytes for an encoder. A reused codec writes and reads the same bytes as a new one. The pool keeps
/// a few codecs per setting, none with a dictionary, none holding more than 16 MiB, and lets go of
/// those no stream took between two gen 2 collections.
/// </para>
/// <para>Not thread-safe, as streams are not.</para>
/// </remarks>
public sealed class ZstdStream : Stream
{
    /// <summary>The input read from the underlying stream at a time: a block and its header.</summary>
    private const int InputSize = (128 * 1024) + 3;

    /// <summary>
    /// The output handed to the encoder at a time: room for any block it compresses, the largest a
    /// byte over 128 KiB (<see cref="ZstdEncoder"/> gathers a pledged block of exactly that size a byte
    /// further), so that it compresses straight into it.
    /// </summary>
    private static readonly int OutputSize = (int)ZstdEncoder.GetMaxCompressedLength((128 * 1024) + 1);

    private readonly ZstdDecoder? _decoder;
    private readonly ZstdEncoder? _encoder;

    /// <summary>
    /// The settings the encoder was taken from <see cref="CodecPool"/> under, and goes back under once the
    /// stream ends; null when the encoder is the caller's, or a dictionary's.
    /// </summary>
    private readonly CodecPool.EncoderKey? _pooledEncoder;

    /// <summary>As <see cref="_pooledEncoder"/>, for the decoder: its largest window; 0 when it is not the pool's.</summary>
    private readonly int _pooledDecoder;

    private readonly bool _leaveOpen;
    private Stream? _stream;
    private byte[]? _input;
    private int _inputStart;
    private int _inputEnd;
    private bool _ended;
    private byte[]? _output;

    /// <summary>
    /// Compresses into <paramref name="stream"/>, at the default level, or decompresses from it; the stream
    /// disposes of <paramref name="stream"/> with itself.
    /// </summary>
    /// <param name="stream">Where the compressed data goes, or comes from.</param>
    /// <param name="mode">Whether the stream compresses or decompresses.</param>
    public ZstdStream(Stream stream, CompressionMode mode)
        : this(stream, mode, leaveOpen: false)
    {
    }

    /// <summary>Compresses into <paramref name="stream"/>, at the default level, or decompresses from it.</summary>
    /// <param name="stream">Where the compressed data goes, or comes from.</param>
    /// <param name="mode">Whether the stream compresses or decompresses.</param>
    /// <param name="leaveOpen">Whether <paramref name="stream"/> stays open when this stream is disposed of.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written, or read, as the mode needs.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is neither mode.</exception>
    public ZstdStream(Stream stream, CompressionMode mode, bool leaveOpen)
    {
        switch (mode)
        {
            case CompressionMode.Compress:
                _pooledEncoder = CodecPool.EncoderKey.Of(ZstdCompressor.DefaultLevel);
                _encoder = CodecPool.RentEncoder(_pooledEncoder.Value, null);
                break;
            case CompressionMode.Decompress:
                _pooledDecoder = ZstdDecompressionOptions.DefaultMaxWindowLog;
                _decoder = CodecPool.RentDecoder(_pooledDecoder);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown compression mode.");
        }

        _stream = Open(stream, mode == CompressionMode.Compress);
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Compresses into <paramref name="stream"/> at <paramref name="level"/>: <see cref="CompressionLevel.Optimal"/>
    /// the default level, <see cref="CompressionLevel.Fastest"/> level 1, <see cref="CompressionLevel.SmallestSize"/>
    /// level 22, and <see cref="CompressionLevel.NoCompression"/> the fastest level of all, which stores the
    /// content nearly as it is, as System.IO.Compression's <c>ZstandardStream</c> maps them.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a level.</exception>
    public ZstdStream(Stream stream, CompressionLevel level, bool leaveOpen = false)
    {
        _pooledEncoder = CodecPool.EncoderKey.Of(Level(level));
        _encoder = CodecPool.RentEncoder(_pooledEncoder.Value, null);
        _stream = Open(stream, compress: true);
        _leaveOpen = leaveOpen;
    }

    /// <summary>Compresses into <paramref name="stream"/> with <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    public ZstdStream(Stream stream, ZstdCompressionOptions options, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pooledEncoder = CodecPool.EncoderKey.Of(options);
        _encoder = _pooledEncoder is { } key ? CodecPool.RentEncoder(key, options) : new ZstdEncoder(options);
        _stream = Open(stream, compress: true);
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Compresses into <paramref name="stream"/> with <paramref name="encoder"/>, which the stream uses from
    /// its current state, and leaves to its owner.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="encoder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    public ZstdStream(Stream stream, ZstdEncoder encoder, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        _stream = Open(stream, compress: true);
        _encoder = encoder;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Decompresses <paramref name="stream"/> with <paramref name="options"/>: a dictionary, a window limit.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    /// <exception cref="InvalidDataException">The dictionary has the zstd format but invalid tables.</exception>
    public ZstdStream(Stream stream, ZstdDecompressionOptions options, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Dictionary.IsEmpty)
        {
            _pooledDecoder = options.MaxWindowLog;
            _decoder = CodecPool.RentDecoder(_pooledDecoder);
        }
        else
        {
            _decoder = new ZstdDecoder(options);
        }

        _stream = Open(stream, compress: false);
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Decompresses <paramref name="stream"/> with <paramref name="decoder"/>, which the stream uses from
    /// its current state, and leaves to its owner.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="decoder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    public ZstdStream(Stream stream, ZstdDecoder decoder, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        _stream = Open(stream, compress: false);
        _decoder = decoder;
        _leaveOpen = leaveOpen;
    }

    /// <summary>The stream the compressed data goes to, or comes from.</summary>
    public Stream BaseStream => _stream ?? throw new ObjectDisposedException(nameof(ZstdStream));

    /// <inheritdoc/>
    public override bool CanRead => _stream is not null && _decoder is not null;

    /// <inheritdoc/>
    public override bool CanWrite => _stream is not null && _encoder is not null;

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

    /// <summary>
    /// Pledges the size of the content the stream compresses, which the frame then declares, as
    /// <see cref="ZstdEncoder.SetSourceLength"/>; a content of another size is refused, with an
    /// <see cref="InvalidDataException"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The stream decompresses, or has begun its frame.</exception>
    public void SetSourceLength(long length)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        if (_encoder is null)
        {
            throw new InvalidOperationException("A decompression stream has no source length to set.");
        }

        _encoder.SetSourceLength(length);
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The stream compresses.</exception>
    public override int Read(Span<byte> buffer)
    {
        Stream stream = Reading();
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

            Fill(stream.Read(InputBuffer().Span));
        }
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The stream compresses.</exception>
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }

    /// <summary>
    /// Decompresses the rest of the stream into <paramref name="destination"/>, written straight from
    /// the decoder's window rather than copied through a buffer first.
    /// </summary>
    /// <exception cref="InvalidOperationException">The stream compresses.</exception>
    public override void CopyTo(Stream destination, int bufferSize)
    {
        ValidateCopyToArguments(destination, bufferSize);
        Stream stream = Reading();
        while (true)
        {
            OperationStatus status = DecodeInWindow(out ReadOnlyMemory<byte> content);
            if (!content.IsEmpty)
            {
                destination.Write(content.Span);
            }

            if (status == OperationStatus.NeedMoreData)
            {
                if (_ended)
                {
                    return;
                }

                Fill(stream.Read(InputBuffer().Span));
            }
        }
    }

    /// <inheritdoc cref="CopyTo(Stream, int)"/>
    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        ValidateCopyToArguments(destination, bufferSize);
        Stream stream = Reading();
        while (true)
        {
            OperationStatus status = DecodeInWindow(out ReadOnlyMemory<byte> content);
            if (!content.IsEmpty)
            {
                await destination.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }

            if (status == OperationStatus.NeedMoreData)
            {
                if (_ended)
                {
                    return;
                }

                Fill(await stream.ReadAsync(InputBuffer(), cancellationToken).ConfigureAwait(false));
            }
        }
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The stream compresses.</exception>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Stream stream = Reading();
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

            Fill(await stream.ReadAsync(InputBuffer(), cancellationToken).ConfigureAwait(false));
        }
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The stream decompresses.</exception>
    /// <exception cref="InvalidDataException">The content goes past the source length set.</exception>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Stream stream = Writing();
        while (true)
        {
            OperationStatus status = Encode(buffer, out int consumed, out int written);
            WriteOutput(stream, written);
            buffer = buffer.Slice(consumed);
            if (status == OperationStatus.Done)
            {
                return;
            }
        }
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value) => Write([value]);

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The stream decompresses.</exception>
    /// <exception cref="InvalidDataException">The content goes past the source length set.</exception>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Stream stream = Writing();
        while (true)
        {
            OperationStatus status = Encode(buffer.Span, out int consumed, out int written);
            if (written > 0)
            {
                await stream.WriteAsync(_output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }

            buffer = buffer.Slice(consumed);
            if (status == OperationStatus.Done)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Compressing, ends a block with what was written so far (<c>ZSTD_e_flush</c>), so that it can be
    /// decoded without the rest, and flushes the underlying stream. Decompressing, does nothing.
    /// </summary>
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        if (_encoder is null)
        {
            return;
        }

        OperationStatus status;
        do
        {
            status = Checked(_encoder.Flush(OutputBuffer(), out int written));
            WriteOutput(_stream, written);
        }
        while (status != OperationStatus.Done);

        _stream.Flush();
    }

    /// <inheritdoc cref="Flush()"/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        if (_encoder is null)
        {
            return;
        }

        OperationStatus status;
        do
        {
            status = Checked(_encoder.Flush(OutputBuffer(), out int written));
            if (written > 0)
            {
                await _stream.WriteAsync(_output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }
        }
        while (status != OperationStatus.Done);

        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Compressing, ends the frame (<c>ZSTD_e_end</c>), then releases the underlying stream, unless it is
    /// left open.
    /// </summary>
    /// <exception cref="InvalidDataException">The content differs from the source length set.</exception>
    protected override void Dispose(bool disposing)
    {
        bool ended = false;
        try
        {
            if (disposing && _stream is { } stream && _encoder is not null)
            {
                OperationStatus status;
                do
                {
                    status = EndFrame(out int written);
                    WriteOutput(stream, written);
                }
                while (status != OperationStatus.Done);
            }

            ended = true;
        }
        finally
        {
            if (disposing)
            {
                Release(ended)?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <inheritdoc cref="Dispose(bool)"/>
    public override async ValueTask DisposeAsync()
    {
        bool ended = false;
        try
        {
            if (_stream is { } stream && _encoder is not null)
            {
                OperationStatus status;
                do
                {
                    status = EndFrame(out int written);
                    if (written > 0)
                    {
                        await stream.WriteAsync(_output.AsMemory(0, written)).ConfigureAwait(false);
                    }
                }
                while (status != OperationStatus.Done);
            }

            ended = true;
        }
        finally
        {
            if (Release(ended) is { } stream)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Stream Open(Stream stream, bool compress)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (compress ? !stream.CanWrite : !stream.CanRead)
        {
            throw new ArgumentException(compress ? "The stream cannot be written." : "The stream cannot be read.", nameof(stream));
        }

        return stream;
    }

    /// <summary>System.IO.Compression's <c>ZstandardStream</c> mapping of <see cref="CompressionLevel"/>.</summary>
    private static int Level(CompressionLevel level) => level switch
    {
        CompressionLevel.Optimal => ZstdCompressor.DefaultLevel,
        CompressionLevel.Fastest => 1,
        CompressionLevel.NoCompression => ZstdCompressor.MinLevel,
        CompressionLevel.SmallestSize => ZstdCompressor.MaxLevel,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown compression level."),
    };

    private Stream Reading()
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        return _decoder is not null ? _stream : throw new InvalidOperationException("A compression stream cannot be read.");
    }

    private Stream Writing()
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        return _encoder is not null ? _stream : throw new InvalidOperationException("A decompression stream cannot be written.");
    }

    /// <summary>
    /// The stream, let go of: returned to be disposed of unless left open; the buffers returned, and the
    /// codec the stream took from <see cref="CodecPool"/>, when it <paramref name="ended"/> cleanly. Once
    /// only: a stream let go of has nothing left to release.
    /// </summary>
    private Stream? Release(bool ended)
    {
        Stream? stream = _stream;
        if (stream is null)
        {
            return null;
        }

        _stream = null;
        Return(ref _input);
        Return(ref _output);
        if (ended)
        {
            if (_pooledEncoder is { } key)
            {
                CodecPool.Return(key, _encoder!);
            }
            else if (_pooledDecoder != 0)
            {
                CodecPool.Return(_pooledDecoder, _decoder!);
            }
        }

        return _leaveOpen ? null : stream;

        static void Return(ref byte[]? buffer)
        {
            if (buffer is { } rented)
            {
                buffer = null;
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// What the encoder wrote, passed on: nothing when it wrote nothing, as most writes smaller than a
    /// block do, rather than an empty write the underlying stream would still pay for.
    /// </summary>
    private void WriteOutput(Stream stream, int written)
    {
        if (written > 0)
        {
            stream.Write(_output.AsSpan(0, written));
        }
    }

    /// <summary>The output buffer, for the encoder to compress into.</summary>
    private Span<byte> OutputBuffer() => (_output ??= ArrayPool<byte>.Shared.Rent(OutputSize)).AsSpan(0, OutputSize);

    /// <summary><see cref="ZstdEncoder.Compress"/>, not final, into the output buffer.</summary>
    private OperationStatus Encode(ReadOnlySpan<byte> source, out int consumed, out int written) =>
        Checked(_encoder!.Compress(source, OutputBuffer(), out consumed, out written, isFinalBlock: false));

    /// <summary>The final <see cref="ZstdEncoder.Compress"/>, of nothing more, into the output buffer.</summary>
    private OperationStatus EndFrame(out int written) =>
        Checked(_encoder!.Compress(default, OutputBuffer(), out _, out written, isFinalBlock: true));

    /// <summary>The encoder's status, but its refusal of a content that differs from the source length set.</summary>
    private static OperationStatus Checked(OperationStatus status) =>
        status != OperationStatus.InvalidData
            ? status
            : throw new InvalidDataException("The content differs from the source length set, which the frame declares.");

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
            OperationStatus status = _decoder!.Decompress(
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

    /// <summary>
    /// Decodes what the input holds into the decoder's window, for <see cref="CopyTo(Stream, int)"/>.
    /// </summary>
    /// <returns>
    /// <see cref="OperationStatus.NeedMoreData"/> when the input must be filled first, unless the
    /// stream has ended between frames; any other status, to be called again.
    /// </returns>
    private OperationStatus DecodeInWindow(out ReadOnlyMemory<byte> content)
    {
        OperationStatus status = _decoder!.DecompressInWindow(_input.AsSpan(_inputStart, _inputEnd - _inputStart), out int consumed, out content);
        _inputStart += consumed;
        switch (status)
        {
            case OperationStatus.Done:
            case OperationStatus.DestinationTooSmall:
                return status;

            case OperationStatus.NeedMoreData:
                // The window's content is out before the decoder asks for more: none comes with it.
                return _ended && _decoder.InFrame
                    ? throw new InvalidDataException("The Zstandard stream ends inside a frame.")
                    : status;

            default:
                throw new InvalidDataException($"The data is not valid Zstandard: {_decoder.LastError}.");
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
