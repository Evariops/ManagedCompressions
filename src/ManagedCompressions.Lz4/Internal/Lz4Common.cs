using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Lz4.Internal;

/// <summary>The block format's constants and the primitives every encoder shares, as lz4 1.10.0 defines them.</summary>
internal static class Lz4Common
{
    public const int MinMatch = 4;

    /// <summary>The last five bytes of a block are always literals.</summary>
    public const int LastLiterals = 5;

    /// <summary>No match starts within the last twelve bytes.</summary>
    public const int MatchFindLimit = 12;

    /// <summary>A block shorter than this is all literals.</summary>
    public const int MinLength = MatchFindLimit + 1;

    public const int MaxDistance = 65535;

    public const int RunMask = 15;

    public const int MatchLengthMask = 15;

    /// <summary>The largest input lz4 accepts.</summary>
    public const int MaxInputSize = 0x7E000000;

    /// <summary>The most bytes a block of <paramref name="length"/> bytes takes: <c>LZ4_compressBound</c>.</summary>
    public static int CompressBound(int length) => (uint)length > MaxInputSize ? 0 : length + (length / 255) + 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read32(ReadOnlySpan<byte> data, int p) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read64(ReadOnlySpan<byte> data, int p) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p, 8));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Read16(ReadOnlySpan<byte> data, int p) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(p, 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write16(Span<byte> data, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(p, 2), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write32(Span<byte> data, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(p, 4), value);

    /// <summary>
    /// The bytes <paramref name="data"/> has in common at <paramref name="pIn"/> and <paramref name="pMatch"/>,
    /// reading no further than <paramref name="pInLimit"/>: <c>LZ4_count</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Count(ReadOnlySpan<byte> data, int pIn, int pMatch, int pInLimit)
    {
        int pStart = pIn;
        if (pIn < pInLimit - 7)
        {
            ulong diff = Read64(data, pMatch) ^ Read64(data, pIn);
            if (diff != 0)
            {
                return (uint)BitOperations.TrailingZeroCount(diff) >> 3;
            }

            pIn += 8;
            pMatch += 8;
        }

        return CountLong(data, pIn, pMatch, pInLimit, pStart);
    }

    /// <summary><see cref="Count"/> past its first eight bytes.</summary>
    public static uint CountLong(ReadOnlySpan<byte> data, int pIn, int pMatch, int pInLimit, int pStart)
    {
        while (pIn < pInLimit - 7)
        {
            ulong diff = Read64(data, pMatch) ^ Read64(data, pIn);
            if (diff == 0)
            {
                pIn += 8;
                pMatch += 8;
                continue;
            }

            pIn += BitOperations.TrailingZeroCount(diff) >> 3;
            return (uint)(pIn - pStart);
        }

        if (pIn < pInLimit - 3 && Read32(data, pMatch) == Read32(data, pIn))
        {
            pIn += 4;
            pMatch += 4;
        }

        if (pIn < pInLimit - 1 && Read16(data, pMatch) == Read16(data, pIn))
        {
            pIn += 2;
            pMatch += 2;
        }

        if (pIn < pInLimit && data[pMatch] == data[pIn])
        {
            pIn++;
        }

        return (uint)(pIn - pStart);
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes of <paramref name="source"/> from <paramref name="from"/> to
    /// <paramref name="destination"/> at <paramref name="to"/>, eight at a time, up to seven past them where
    /// both have room for it: <c>LZ4_wildCopy8</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WildCopy8(Span<byte> destination, int to, ReadOnlySpan<byte> source, int from, int length)
    {
        int end = to + length;
        if (length <= destination.Length - to - 8 && length <= source.Length - from - 8)
        {
            do
            {
                BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(to, 8), BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(from, 8)));
                to += 8;
                from += 8;
            }
            while (to < end);
        }
        else
        {
            source.Slice(from, length).CopyTo(destination.Slice(to));
        }
    }

    /// <summary>The last literals, a run with no match: its token, its length past 15, its bytes; the position past them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteLastLiterals(Span<byte> destination, int op, ReadOnlySpan<byte> source, int anchor, int lastRun)
    {
        if (lastRun >= RunMask)
        {
            int accumulator = lastRun - RunMask;
            destination[op++] = RunMask << 4;
            for (; accumulator >= 255; accumulator -= 255)
            {
                destination[op++] = 255;
            }

            destination[op++] = (byte)accumulator;
        }
        else
        {
            destination[op++] = (byte)(lastRun << 4);
        }

        source.Slice(anchor, lastRun).CopyTo(destination.Slice(op));
        return op + lastRun;
    }
}
