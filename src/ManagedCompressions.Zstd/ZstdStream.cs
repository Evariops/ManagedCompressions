using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

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
                _encoder = new ZstdEncoder();
                break;
            case CompressionMode.Decompress:
                _decoder = new ZstdDecoder();
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
        : this(stream, new ZstdEncoder(Level(level)), leaveOpen)
    {
    }

    /// <summary>Compresses into <paramref name="stream"/> with <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    public ZstdStream(Stream stream, ZstdCompressionOptions options, bool leaveOpen = false)
        : this(stream, new ZstdEncoder(options), leaveOpen)
    {
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
        }
        finally
        {
            if (disposing)
            {
                Release()?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <inheritdoc cref="Dispose(bool)"/>
    public override async ValueTask DisposeAsync()
    {
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
        }
        finally
        {
            if (Release() is { } stream)
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

    /// <summary>The stream, let go of: returned to be disposed of unless left open; the buffers returned.</summary>
    private Stream? Release()
    {
        Stream? stream = _stream;
        _stream = null;
        Return(ref _input);
        Return(ref _output);
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
