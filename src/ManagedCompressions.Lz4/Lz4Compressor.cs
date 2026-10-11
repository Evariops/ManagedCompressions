using System;
using ManagedCompressions.Lz4.Internal;

namespace ManagedCompressions.Lz4;

/// <summary>
/// An LZ4 block compressor at one level, which keeps its tables from block to block. It writes the
/// blocks lz4 1.10.0 writes at the same level, byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// The levels are lz4's: 1 is <c>LZ4_compress_default</c>; below 1, <c>LZ4_compress_fast</c> with an
/// acceleration of 1 - level (lz4frame's convention, so -1 is acceleration 2); 2 to 12 are
/// <c>LZ4_compress_HC</c> at that level: 2 its two-table search, 3 to 9 its hash chains, 10 to 12 its
/// optimal parser.
/// </para>
/// <para>
/// An instance is not thread-safe: use one per thread, or one per concurrent operation. A warm
/// instance allocates nothing, and does not clear its tables before each block, as lz4 does.
/// </para>
/// </remarks>
public sealed unsafe class Lz4Compressor
{
    /// <summary>The lowest level: the highest acceleration lz4 takes.</summary>
    public const int MinLevel = -65536;

    /// <summary>The highest level: the optimal parser at its most thorough.</summary>
    public const int MaxLevel = 12;

    /// <summary>The default level: <c>LZ4_compress_default</c>.</summary>
    public const int DefaultLevel = 1;

    private readonly Lz4FastEncoder? _fast;
    private readonly Lz4HighEncoder? _high;
    private readonly int _acceleration;

    /// <summary>A compressor at <paramref name="level"/>, from <see cref="MinLevel"/> to <see cref="MaxLevel"/>.</summary>
    public Lz4Compressor(int level = DefaultLevel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, MaxLevel);
        Level = level;
        if (level >= 2)
        {
            _high = new Lz4HighEncoder(level);
        }
        else
        {
            _fast = new Lz4FastEncoder();
            _acceleration = level >= 1 ? 1 : 1 - level;
        }
    }

    /// <summary>The level this compressor writes at.</summary>
    public int Level { get; }

    /// <summary>The most bytes a block of <paramref name="length"/> bytes takes: <c>LZ4_compressBound</c>.</summary>
    public static int GetMaxCompressedLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Lz4Common.MaxInputSize);
        return Lz4Common.CompressBound(length);
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into one block, and returns its length. The destination
    /// must hold <see cref="GetMaxCompressedLength"/> bytes; use <see cref="TryCompress"/> for a
    /// smaller one.
    /// </summary>
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < GetMaxCompressedLength(source.Length))
        {
            throw new ArgumentException("The destination is shorter than the longest a compression can take.", nameof(destination));
        }

        TryCompress(source, destination, out int written);
        return written;
    }

    /// <summary>Compresses <paramref name="source"/> into one block; false when it does not fit in <paramref name="destination"/>.</summary>
    public bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(source.Length, Lz4Common.MaxInputSize, nameof(source));
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            bytesWritten = _fast is not null
                ? _fast.Compress(src, source.Length, dst, destination.Length, _acceleration)
                : _high!.Compress(src, source.Length, dst, destination.Length);
        }

        return bytesWritten > 0;
    }
}
