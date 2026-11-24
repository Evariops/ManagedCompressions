using System;
using System.Buffers;
using System.Runtime.InteropServices;
using ManagedCompressions.Brotli.Internal;
using ManagedCompressions.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// Compresses into Brotli streams (RFC 7932) in fully managed code, writing the bytes brotli 1.1.1
/// writes: those of <c>BrotliEncoderCompress</c>, which <see cref="System.IO.Compression.BrotliEncoder.TryCompress(ReadOnlySpan{byte}, Span{byte}, out int, int, int)"/>
/// calls, at every quality, window and mode.
/// </summary>
/// <remarks>
/// <para>
/// An instance keeps its tables and buffers from one call to the next: brotli creates and clears them
/// for every stream. It is not thread-safe: use one per thread.
/// </para>
/// <para>
/// <see cref="Compress"/> compresses the whole source into one stream per call. When the stream does not
/// fit the destination, it reports so, the destination then holding the part of the stream written
/// before, which is no stream, unless the destination holds <see cref="GetMaxCompressedLength"/> bytes:
/// then, as brotli does, it stores the source uncompressed.
/// </para>
/// <para>
/// The bytes are those of the brotli of the platform the code runs on, and differ between platforms at
/// qualities 10 and 11: .NET's brotli computes a logarithm otherwise on Windows than on Linux and macOS,
/// which changes the encoder's choices for a few streams. Any brotli decodes them all.
/// </para>
/// </remarks>
public sealed unsafe partial class BrotliCompressor
{
    /// <summary>The lowest quality: the fastest.</summary>
    public const int MinQuality = 0;

    /// <summary>The highest quality: the densest.</summary>
    public const int MaxQuality = 11;

    /// <summary>.NET's default quality, of <c>BrotliEncoder.TryCompress</c> and <c>CompressionLevel.Optimal</c>.</summary>
    public const int DefaultQuality = 4;

    /// <summary>The smallest window: 1 KiB, less 16 bytes.</summary>
    public const int MinWindowLog = 10;

    /// <summary>The largest window of RFC 7932: 16 MiB, less 16 bytes.</summary>
    public const int MaxWindowLog = 24;

    /// <summary>The default window of brotli and .NET: 4 MiB.</summary>
    public const int DefaultWindowLog = 22;

    /// <summary>Creates a compressor at <see cref="DefaultQuality"/> and <see cref="DefaultWindowLog"/>.</summary>
    public BrotliCompressor()
        : this(DefaultQuality)
    {
    }

    /// <summary>Creates a compressor.</summary>
    /// <param name="quality">From <see cref="MinQuality"/> to <see cref="MaxQuality"/>.</param>
    /// <param name="windowLog">The window, as a power of two, from <see cref="MinWindowLog"/> to <see cref="MaxWindowLog"/>.</param>
    /// <param name="mode">What the input is.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is out of its range.</exception>
    /// <exception cref="PlatformNotSupportedException">The processor is big-endian: see the README.</exception>
    public BrotliCompressor(int quality, int windowLog = DefaultWindowLog, BrotliMode mode = BrotliMode.Generic)
    {
        LittleEndian.Require();
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, MinQuality);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, MaxQuality);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowLog, MinWindowLog);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(windowLog, MaxWindowLog);
        if (mode is not (BrotliMode.Generic or BrotliMode.Text or BrotliMode.Font))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a Brotli mode");
        }

        Quality = quality;
        WindowLog = windowLog;
        Mode = mode;
    }

    /// <summary>The quality, from <see cref="MinQuality"/> to <see cref="MaxQuality"/>.</summary>
    public int Quality { get; }

    /// <summary>The window, as a power of two.</summary>
    public int WindowLog { get; }

    /// <summary>What the input is.</summary>
    public BrotliMode Mode { get; }

    /// <summary>
    /// The largest stream a source of <paramref name="sourceLength"/> bytes compresses to, brotli's
    /// <c>BrotliEncoderMaxCompressedSize</c>: the source stored uncompressed, with its headers.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sourceLength"/> is negative, or too large for a span.</exception>
    public static int GetMaxCompressedLength(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        long length = BitStream.MaxCompressedSize(sourceLength);
        if (length > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceLength), sourceLength, "the bound exceeds the largest array");
        }

        return (int)length;
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into one stream at the start of <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">The content.</param>
    /// <param name="destination">Where the stream is written.</param>
    /// <param name="bytesConsumed">The size of the source, once compressed; otherwise 0.</param>
    /// <param name="bytesWritten">The size of the stream, once written; otherwise 0.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the stream is written;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when it does not fit, and the destination holds
    /// fewer than <see cref="GetMaxCompressedLength"/> bytes: what it holds then is not a stream.
    /// </returns>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        int written;
        fixed (byte* src = &MemoryMarshal.GetReference(source))
        fixed (byte* dst = &MemoryMarshal.GetReference(destination))
        {
            written = CompressOneShot(src, source.Length, dst, destination.Length);
        }

        // The tables and buffers, read through pointers, are pinned arrays only the compressor holds: it
        // stays alive until the stream is written, whatever the code after the loops uses of it.
        GC.KeepAlive(this);
        if (written < 0)
        {
            return OperationStatus.DestinationTooSmall;
        }

        bytesConsumed = source.Length;
        bytesWritten = written;
        return OperationStatus.Done;
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into one stream at the start of <paramref name="destination"/>,
    /// as <c>BrotliEncoder.TryCompress</c> does: false when it does not fit.
    /// </summary>
    public bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten) =>
        Compress(source, destination, out _, out bytesWritten) == OperationStatus.Done;

    /// <summary>
    /// brotli's <c>BrotliEncoderCompress</c>: the stream's size, or -1 when it does not fit and the
    /// destination is smaller than the bound of an uncompressed stream.
    /// </summary>
    private int CompressOneShot(byte* src, int srcLength, byte* dst, int dstLength)
    {
        if (dstLength == 0)
        {
            return -1;
        }

        if (srcLength == 0)
        {
            // The empty stream: window bits 16, last and empty.
            dst[0] = 6;
            return 1;
        }

        long maxOut = BitStream.MaxCompressedSize(srcLength);
        int written = Quality <= 1
            ? CompressFast(src, srcLength, dst, dstLength)
            : CompressStream(src, srcLength, dst, dstLength);
        if (written >= 0 && written <= maxOut)
        {
            return written;
        }

        // The fallback: the source in uncompressed meta-blocks, which the bound always holds.
        if (dstLength >= maxOut)
        {
            return BitStream.MakeUncompressedStream(src, srcLength, dst);
        }

        return -1;
    }
}
