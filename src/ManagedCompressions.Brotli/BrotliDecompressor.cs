using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedCompressions.Brotli.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// Decompresses Brotli streams (RFC 7932) in fully managed code.
/// </summary>
/// <remarks>
/// <para>
/// An instance keeps its tables from one stream to the next, so that a warm decoder allocates
/// nothing: they grow to the prefix codes of the largest meta-block it has decoded, some 8 MB at most
/// (256 codes of each kind), and do not shrink. It is not thread-safe: use one per thread.
/// </para>
/// <para>
/// <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/> decodes one whole stream
/// per call, from the start of the source into the start of the destination, which must hold all of
/// its content: a Brotli stream does not record its size. When the source ends before the stream
/// does, or the destination cannot hold it, it reports so without consuming or writing anything: call
/// again with the whole stream, or with a larger destination.
/// </para>
/// <para>
/// It accepts and refuses the streams brotli 1.1.1's decoder does, and decodes them to the same bytes.
/// Like brotli by default, it refuses the large window extension (window sizes above 16 MiB), which is
/// not part of RFC 7932.
/// </para>
/// </remarks>
public sealed unsafe partial class BrotliDecompressor
{
    /// <summary>Creates a decoder.</summary>
    /// <exception cref="PlatformNotSupportedException">The processor is big-endian: see the README.</exception>
    public BrotliDecompressor()
    {
        LittleEndian.Require();
        AllocateHeaderTables();
    }

    /// <summary>
    /// Why the last call refused its stream: the check that failed, where the status it returned says
    /// what to do next. <see cref="BrotliError.None"/> after a call that returned
    /// <see cref="OperationStatus.Done"/>.
    /// </summary>
    public BrotliError LastError { get; private set; }

    /// <summary>
    /// Decompresses the stream that starts <paramref name="source"/> into the start of
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">A Brotli stream, possibly followed by other data, which is left alone.</param>
    /// <param name="destination">Where the stream's content is written; it must hold all of it.</param>
    /// <param name="bytesConsumed">The size of the stream, once it is decoded; otherwise 0.</param>
    /// <param name="bytesWritten">The size of the stream's content, once it is decoded; otherwise 0.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the stream is decoded;
    /// <see cref="OperationStatus.NeedMoreData"/> when the source ends inside the stream;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when the content does not fit;
    /// <see cref="OperationStatus.InvalidData"/> when the stream is not valid Brotli.
    /// </returns>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        BrotliError error;
        int consumed;
        int written;
        fixed (byte* src = &MemoryMarshal.GetReference(source))
        fixed (byte* dst = &MemoryMarshal.GetReference(destination))
        {
            error = DecodeStream(src, source.Length, dst, destination.Length, out consumed, out written);
        }

        LastError = error;
        switch (error)
        {
            case BrotliError.None:
                bytesConsumed = consumed;
                bytesWritten = written;
                return OperationStatus.Done;
            case BrotliError.Truncated:
                return OperationStatus.NeedMoreData;
            case BrotliError.DestinationTooSmall:
                return OperationStatus.DestinationTooSmall;
            default:
                return OperationStatus.InvalidData;
        }
    }

    /// <summary>
    /// Decompresses the stream that starts <paramref name="source"/> into the start of
    /// <paramref name="destination"/>, as <see cref="System.IO.Compression.BrotliDecoder.TryDecompress"/>
    /// does: false when the stream is truncated, invalid, or does not fit.
    /// </summary>
    public bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten) =>
        Decompress(source, destination, out _, out bytesWritten) == OperationStatus.Done;

    /// <summary>Clears <see cref="LastError"/>; the decoder holds no other state between calls.</summary>
    public void Reset() => LastError = BrotliError.None;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T* Pointer<T>(T[] array)
        where T : unmanaged =>
        (T*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));

    /// <summary>An array on the pinned heap, so that the pointers taken to it hold for its life.</summary>
    private static T[] Pinned<T>(int length)
        where T : unmanaged =>
        GC.AllocateUninitializedArray<T>(length, pinned: true);
}
