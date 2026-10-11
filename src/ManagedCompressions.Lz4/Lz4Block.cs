using System;
using System.IO;
using ManagedCompressions.Lz4.Internal;

namespace ManagedCompressions.Lz4;

/// <summary>
/// The LZ4 block format (Parquet's <c>LZ4_RAW</c>): sequences of a token, literals and a match, as
/// lz4's <c>lz4_Block_format.md</c> describes them, a block fed whole with no framing.
/// </summary>
/// <remarks>
/// <para>
/// The decoder reads any valid block, whatever wrote it, and refuses a literal or a match past either
/// end, an offset of zero or before the start, and a block that does not end with literals. Its fast
/// loop copies sixteen or thirty-two bytes at a time while the destination has room for it.
/// </para>
/// <para>
/// <see cref="Compress(ReadOnlySpan{byte}, Span{byte})"/> writes the bytes <c>LZ4_compress_default</c>
/// writes; <see cref="Lz4Compressor"/> takes the other levels, and keeps its tables between blocks.
/// </para>
/// </remarks>
public static unsafe class Lz4Block
{
    [ThreadStatic]
    private static Lz4Compressor? t_compressor;

    /// <summary>The most bytes <paramref name="length"/> bytes compress to: <c>LZ4_compressBound</c>.</summary>
    public static int MaxCompressedLength(int length) => Lz4Compressor.GetMaxCompressedLength(length);

    /// <summary>Decompresses <paramref name="source"/> into all of <paramref name="destination"/>.</summary>
    /// <exception cref="InvalidDataException">The block is corrupt, or does not decode to exactly the destination's length.</exception>
    public static void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        nint written;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            written = Lz4Decoder.Decode(src, source.Length, dst, destination.Length);
        }

        if (written < 0)
        {
            ThrowCorrupt();
        }

        if (written != destination.Length)
        {
            throw new InvalidDataException("An LZ4 block holds fewer bytes than its destination.");
        }
    }

    /// <summary>
    /// Decompresses <paramref name="source"/>, a whole block, into <paramref name="destination"/>;
    /// false when the block is corrupt or its content does not fit.
    /// </summary>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        nint written;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            written = Lz4Decoder.Decode(src, source.Length, dst, destination.Length);
        }

        bytesWritten = written < 0 ? 0 : (int)written;
        return written >= 0;
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/>, which holds at least
    /// <see cref="MaxCompressedLength"/>, and returns the bytes written: <c>LZ4_compress_default</c>.
    /// </summary>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination) =>
        (t_compressor ??= new Lz4Compressor()).Compress(source, destination);

    private static void ThrowCorrupt() => throw new InvalidDataException("An LZ4 block is corrupt: a literal or a match reaches past its data.");
}
