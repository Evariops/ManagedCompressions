using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ManagedCompressions.Lz4.Internal;

/// <summary>The block format's constants and the primitives every encoder shares, as lz4 1.10.0 defines them.</summary>
internal static unsafe class Lz4Common
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
    public static uint Read32(byte* p) => Unsafe.ReadUnaligned<uint>(p);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read64(byte* p) => Unsafe.ReadUnaligned<ulong>(p);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Read16(byte* p) => Unsafe.ReadUnaligned<ushort>(p);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write16(byte* p, ushort value) => Unsafe.WriteUnaligned(p, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write32(byte* p, uint value) => Unsafe.WriteUnaligned(p, value);

    /// <summary>
    /// The bytes <paramref name="pIn"/> and <paramref name="pMatch"/> have in common, reading no further
    /// than <paramref name="pInLimit"/>: <c>LZ4_count</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Count(byte* pIn, byte* pMatch, byte* pInLimit)
    {
        byte* pStart = pIn;
        if (pIn < pInLimit - 7)
        {
            ulong diff = Read64(pMatch) ^ Read64(pIn);
            if (diff != 0)
            {
                return (uint)BitOperations.TrailingZeroCount(diff) >> 3;
            }

            pIn += 8;
            pMatch += 8;
        }

        return CountLong(pIn, pMatch, pInLimit, pStart);
    }

    private static uint CountLong(byte* pIn, byte* pMatch, byte* pInLimit, byte* pStart)
    {
        while (pIn < pInLimit - 7)
        {
            ulong diff = Read64(pMatch) ^ Read64(pIn);
            if (diff == 0)
            {
                pIn += 8;
                pMatch += 8;
                continue;
            }

            pIn += BitOperations.TrailingZeroCount(diff) >> 3;
            return (uint)(pIn - pStart);
        }

        if (pIn < pInLimit - 3 && Read32(pMatch) == Read32(pIn))
        {
            pIn += 4;
            pMatch += 4;
        }

        if (pIn < pInLimit - 1 && Read16(pMatch) == Read16(pIn))
        {
            pIn += 2;
            pMatch += 2;
        }

        if (pIn < pInLimit && *pMatch == *pIn)
        {
            pIn++;
        }

        return (uint)(pIn - pStart);
    }

    /// <summary>Copies eight bytes at a time until <paramref name="end"/>, up to seven past it: <c>LZ4_wildCopy8</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WildCopy8(byte* destination, byte* source, byte* end)
    {
        do
        {
            Unsafe.WriteUnaligned(destination, Unsafe.ReadUnaligned<ulong>(source));
            destination += 8;
            source += 8;
        }
        while (destination < end);
    }

    /// <summary>Copies sixteen bytes at a time until <paramref name="end"/>, up to fifteen past it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WildCopy16(byte* destination, byte* source, byte* end)
    {
        do
        {
            Vector128.Store(Vector128.Load(source), destination);
            destination += 16;
            source += 16;
        }
        while (destination < end);
    }

    /// <summary>The last literals, a run with no match: its token, its length past 15, its bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* WriteLastLiterals(byte* op, byte* anchor, nuint lastRun)
    {
        if (lastRun >= RunMask)
        {
            nuint accumulator = lastRun - RunMask;
            *op++ = RunMask << 4;
            for (; accumulator >= 255; accumulator -= 255)
            {
                *op++ = 255;
            }

            *op++ = (byte)accumulator;
        }
        else
        {
            *op++ = (byte)(lastRun << 4);
        }

        Unsafe.CopyBlockUnaligned(op, anchor, (uint)lastRun);
        return op + lastRun;
    }
}
