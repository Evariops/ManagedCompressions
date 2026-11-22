using System;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's <c>Command</c> (<c>c/enc/command.h</c>): an insert of literals and a copy, with the codes
/// that store them.
/// </summary>
internal struct Command
{
    /// <summary>The number of literals.</summary>
    public uint InsertLen;

    /// <summary>The copy length in the low 25 bits, and the copy code less it in the high 7.</summary>
    public uint CopyLen;

    /// <summary>The distance's extra bits.</summary>
    public uint DistExtra;

    /// <summary>The insert-and-copy code.</summary>
    public ushort CmdPrefix;

    /// <summary>The distance code in the low 10 bits, and the number of its extra bits in the high 6.</summary>
    public ushort DistPrefix;
}

/// <summary>brotli's <c>BrotliDistanceParams</c>: the distance code's parameters, NPOSTFIX and NDIRECT.</summary>
internal struct DistanceParams
{
    public uint PostfixBits;
    public uint NumDirectCodes;
    public uint AlphabetSizeMax;
    public uint AlphabetSizeLimit;
    public nuint MaxDistance;

    /// <summary>brotli's <c>BrotliInitDistanceParams</c>, outside the large window.</summary>
    public static DistanceParams Create(uint npostfix, uint ndirect)
    {
        DistanceParams p;
        p.PostfixBits = npostfix;
        p.NumDirectCodes = ndirect;
        p.AlphabetSizeMax = (uint)Prefix.DistanceAlphabetSize((int)npostfix, (int)ndirect, Prefix.MaxDistanceBits);
        p.AlphabetSizeLimit = p.AlphabetSizeMax;
        p.MaxDistance = ndirect + (1u << (Prefix.MaxDistanceBits + (int)npostfix + 2)) - (1u << ((int)npostfix + 2));
        return p;
    }
}

/// <summary>The functions of <c>c/enc/command.h</c> and <c>c/enc/prefix.h</c>.</summary>
internal static class Commands
{
    private static ReadOnlySpan<uint> InsBase => [0, 1, 2, 3, 4, 5, 6, 8, 10, 14, 18, 26, 34, 50, 66, 98, 130, 194, 322, 578, 1090, 2114, 6210, 22594];

    private static ReadOnlySpan<byte> InsExtra => [0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 12, 14, 24];

    private static ReadOnlySpan<uint> CopyBase => [2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 18, 22, 30, 38, 54, 70, 102, 134, 198, 326, 582, 1094, 2118];

    private static ReadOnlySpan<byte> CopyExtra => [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 24];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetInsertLengthCode(nuint insertLen)
    {
        if (insertLen < 6)
        {
            return (uint)insertLen;
        }

        if (insertLen < 130)
        {
            int nbits = FastLog.Log2FloorNonZero(insertLen - 2) - 1;
            return (uint)((nbits << 1) + (int)((insertLen - 2) >> nbits) + 2);
        }

        if (insertLen < 2114)
        {
            return (uint)(FastLog.Log2FloorNonZero(insertLen - 66) + 10);
        }

        return insertLen < 6210 ? 21u : insertLen < 22594 ? 22u : 23u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetCopyLengthCode(nuint copyLen)
    {
        if (copyLen < 10)
        {
            return (uint)(copyLen - 2);
        }

        if (copyLen < 134)
        {
            int nbits = FastLog.Log2FloorNonZero(copyLen - 6) - 1;
            return (uint)((nbits << 1) + (int)((copyLen - 6) >> nbits) + 4);
        }

        if (copyLen < 2118)
        {
            return (uint)(FastLog.Log2FloorNonZero(copyLen - 70) + 12);
        }

        return 23;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort CombineLengthCodes(uint insCode, uint copyCode, bool useLastDistance)
    {
        uint bits64 = (copyCode & 0x7) | ((insCode & 0x7) << 3);
        if (useLastDistance && insCode < 8 && copyCode < 16)
        {
            return (ushort)(copyCode < 8 ? bits64 : bits64 | 64);
        }

        uint offset = 2 * ((copyCode >> 3) + (3 * (insCode >> 3)));
        offset = (offset << 5) + 0x40 + ((0x520D40u >> (int)offset) & 0xC0);
        return (ushort)(offset | bits64);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort GetLengthCode(nuint insertLen, nuint copyLen, bool useLastDistance) =>
        CombineLengthCodes(GetInsertLengthCode(insertLen), GetCopyLengthCode(copyLen), useLastDistance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetInsertBase(uint insCode) => InsBase[(int)insCode];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetInsertExtra(uint insCode) => InsExtra[(int)insCode];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetCopyBase(uint copyCode) => CopyBase[(int)copyCode];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetCopyExtra(uint copyCode) => CopyExtra[(int)copyCode];

    /// <summary>
    /// brotli's <c>PrefixEncodeCopyDistance</c>: <paramref name="distanceCode"/> is a short code, or the
    /// distance plus 15.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrefixEncodeCopyDistance(nuint distanceCode, nuint numDirectCodes, int postfixBits, out ushort code, out uint extraBits)
    {
        if (distanceCode < Prefix.NumDistanceShortCodes + numDirectCodes)
        {
            code = (ushort)distanceCode;
            extraBits = 0;
            return;
        }

        nuint dist = ((nuint)1 << (postfixBits + 2)) + (distanceCode - Prefix.NumDistanceShortCodes - numDirectCodes);
        int bucket = FastLog.Log2FloorNonZero(dist) - 1;
        nuint postfixMask = (1u << postfixBits) - 1;
        nuint postfix = dist & postfixMask;
        nuint prefix = (dist >> bucket) & 1;
        nuint offset = (2 + prefix) << bucket;
        int nbits = bucket - postfixBits;
        code = (ushort)(((nuint)nbits << 10) | (Prefix.NumDistanceShortCodes + numDirectCodes + (((nuint)(2 * (nbits - 1)) + prefix) << postfixBits) + postfix));
        extraBits = (uint)((dist - offset) >> postfixBits);
    }

    /// <summary>brotli's <c>InitCommand</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Init(ref Command self, in DistanceParams dist, nuint insertLen, nuint copyLen, int copyLenCodeDelta, nuint distanceCode)
    {
        uint delta = (byte)(sbyte)copyLenCodeDelta;
        self.InsertLen = (uint)insertLen;
        self.CopyLen = (uint)(copyLen | ((nuint)delta << 25));
        PrefixEncodeCopyDistance(distanceCode, dist.NumDirectCodes, (int)dist.PostfixBits, out self.DistPrefix, out self.DistExtra);
        self.CmdPrefix = GetLengthCode(insertLen, (nuint)(nint)((int)copyLen + copyLenCodeDelta), (self.DistPrefix & 0x3FF) == 0);
    }

    /// <summary>brotli's <c>InitInsertCommand</c>: literals and no copy.</summary>
    public static void InitInsert(ref Command self, nuint insertLen)
    {
        self.InsertLen = (uint)insertLen;
        self.CopyLen = 4 << 25;
        self.DistExtra = 0;
        self.DistPrefix = Prefix.NumDistanceShortCodes;
        self.CmdPrefix = GetLengthCode(insertLen, 4, false);
    }

    /// <summary>brotli's <c>CommandRestoreDistanceCode</c>.</summary>
    public static uint RestoreDistanceCode(in Command self, in DistanceParams dist)
    {
        uint dcode = self.DistPrefix & 0x3FFu;
        if (dcode < Prefix.NumDistanceShortCodes + dist.NumDirectCodes)
        {
            return dcode;
        }

        int nbits = self.DistPrefix >> 10;
        uint extra = self.DistExtra;
        uint postfixMask = (1u << (int)dist.PostfixBits) - 1;
        uint hcode = (dcode - dist.NumDirectCodes - Prefix.NumDistanceShortCodes) >> (int)dist.PostfixBits;
        uint lcode = (dcode - dist.NumDirectCodes - Prefix.NumDistanceShortCodes) & postfixMask;
        uint offset = ((2u + (hcode & 1u)) << nbits) - 4u;
        return ((offset + extra) << (int)dist.PostfixBits) + lcode + dist.NumDirectCodes + Prefix.NumDistanceShortCodes;
    }

    /// <summary>brotli's <c>CommandDistanceContext</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DistanceContext(in Command self)
    {
        uint r = (uint)self.CmdPrefix >> 6;
        uint c = self.CmdPrefix & 7u;
        if ((r == 0 || r == 2 || r == 4 || r == 7) && c <= 2)
        {
            return c;
        }

        return 3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CopyLen(in Command self) => self.CopyLen & 0x1FFFFFF;

    /// <summary>brotli's <c>CommandCopyLenCode</c>: the length the copy code stores.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CopyLenCode(in Command self)
    {
        uint modifier = self.CopyLen >> 25;
        int delta = (sbyte)(byte)(modifier | ((modifier & 0x40) << 1));
        return (uint)((int)(self.CopyLen & 0x1FFFFFF) + delta);
    }
}
