using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// The state quality 0 keeps from one fragment to the next, brotli's <c>BrotliOnePassArena</c>: the
/// command and distance codes the next fragment starts with, and their stored form.
/// </summary>
internal sealed class OnePassArena
{
    public readonly byte[] LitDepth = new byte[256];
    public readonly ushort[] LitBits = new ushort[256];

    /// <summary>
    /// The command and distance codes, 64 symbols each, back to back. The command code is over a smaller
    /// alphabet: 0-15 insert code 0 and copy codes 0-15 with the last distance; 16-39 insert code 0 and
    /// copy codes 0-23; 40-63 insert codes 0-23 and copy code 0.
    /// </summary>
    public readonly byte[] CmdDepth = new byte[128];
    public readonly ushort[] CmdBits = new ushort[128];
    public readonly uint[] CmdHisto = new uint[128];

    /// <summary>The stored form of the command and distance codes for the next fragment, with a write's room past it.</summary>
    public readonly byte[] CmdCode = new byte[512 + 8];
    public int CmdCodeNumBits;

    public readonly HuffmanTree[] Tree = new HuffmanTree[(2 * 256) + 1];
    public readonly uint[] Histogram = new uint[256];
    public readonly byte[] TmpDepth = new byte[Prefix.NumCommandSymbols];
    public readonly ushort[] TmpBits = new ushort[64 + 8];

    /// <summary>
    /// The literal and the command codes, each its bits with its depth from bit 16: one load a symbol, and one
    /// table each in the commands' loop. Combined from the depths and bits whenever these change.
    /// </summary>
    public readonly uint[] LitCodes = new uint[256];
    public readonly uint[] CmdCodes = new uint[128];

    private static ReadOnlySpan<byte> DefaultCommandDepths =>
    [
        0, 4, 4, 5, 6, 6, 7, 7, 7, 7, 7, 8, 8, 8, 8, 8,
        0, 0, 0, 4, 4, 4, 4, 4, 5, 5, 6, 6, 6, 6, 7, 7,
        7, 7, 10, 10, 10, 10, 10, 10, 0, 4, 4, 5, 5, 5, 6, 6,
        7, 8, 8, 9, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
        5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        6, 6, 6, 6, 6, 6, 5, 5, 5, 5, 5, 5, 4, 4, 4, 4,
        4, 4, 4, 5, 5, 5, 5, 5, 5, 6, 6, 7, 7, 7, 8, 10,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
    ];

    private static ReadOnlySpan<ushort> DefaultCommandBits =>
    [
        0, 0, 8, 9, 3, 35, 7, 71,
        39, 103, 23, 47, 175, 111, 239, 31,
        0, 0, 0, 4, 12, 2, 10, 6,
        13, 29, 11, 43, 27, 59, 87, 55,
        15, 79, 319, 831, 191, 703, 447, 959,
        0, 14, 1, 25, 5, 21, 19, 51,
        119, 159, 95, 223, 479, 991, 63, 575,
        127, 639, 383, 895, 255, 767, 511, 1023,
        14, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        27, 59, 7, 39, 23, 55, 30, 1, 17, 9, 25, 5, 0, 8, 4, 12,
        2, 10, 6, 21, 13, 29, 3, 19, 11, 15, 47, 31, 95, 63, 127, 255,
        767, 2815, 1791, 3839, 511, 2559, 1535, 3583, 1023, 3071, 2047, 4095,
    ];

    private static ReadOnlySpan<byte> DefaultCommandCode =>
    [
        0xff, 0x77, 0xd5, 0xbf, 0xe7, 0xde, 0xea, 0x9e, 0x51, 0x5d, 0xde, 0xc6,
        0x70, 0x57, 0xbc, 0x58, 0x58, 0x58, 0xd8, 0xd8, 0x58, 0xd5, 0xcb, 0x8c,
        0xea, 0xe0, 0xc3, 0x87, 0x1f, 0x83, 0xc1, 0x60, 0x1c, 0x67, 0xb2, 0xaa,
        0x06, 0x83, 0xc1, 0x60, 0x30, 0x18, 0xcc, 0xa1, 0xce, 0x88, 0x54, 0x94,
        0x46, 0xe1, 0xb0, 0xd0, 0x4e, 0xb2, 0xf7, 0x04, 0x00,
    ];

    /// <summary>brotli's <c>InitCommandPrefixCodes</c>: the codes of the first fragment of a stream.</summary>
    public void InitCommandPrefixCodes()
    {
        DefaultCommandDepths.CopyTo(CmdDepth);
        DefaultCommandBits.CopyTo(CmdBits);
        BitWriter.CombineCodes(CmdDepth, CmdBits, CmdCodes, 128);
        DefaultCommandCode.CopyTo(CmdCode);
        CmdCodeNumBits = 448;
    }
}

/// <summary>
/// Quality 0, brotli's <c>BrotliCompressFragmentFast</c> (<c>c/enc/compress_fragment.c</c>): one pass, a
/// match found is emitted at once, with codes built from the fragment's start and from the previous
/// fragment's commands.
/// </summary>
internal static class CompressFragment
{
    private const long MaxDistance = (1L << 18) - Prefix.WindowGap;
    private const uint HashMul32 = 0x1E35A7BD;
    private const int FirstBlockSize = 3 << 15;
    private const int MergeBlockSize = 1 << 16;
    private const int InputMarginBytes = Prefix.WindowGap;
    private const int MinMatchLength = 5;

    /// <summary>Acceptable loss for an uncompressible fragment's speedup: 2 %.</summary>
    private const int MinRatio = 980;


    private static ReadOnlySpan<uint> CmdHistoSeed =>
    [
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 0, 0, 0, 0,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(ReadOnlySpan<byte> data, int p, int shift) => (uint)(((BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p, 8)) << 24) * HashMul32) >> shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashBytesAtOffset(ulong v, int offset, int shift) => (uint)((((v >> (8 * offset)) << 24) * HashMul32) >> shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsMatch(ReadOnlySpan<byte> data, int p1, int p2)
    {
        ReadOnlySpan<byte> a = data.Slice(p1, 5);
        ReadOnlySpan<byte> b = data.Slice(p2, 5);
        return BinaryPrimitives.ReadUInt32LittleEndian(a) == BinaryPrimitives.ReadUInt32LittleEndian(b) && a[4] == b[4];
    }

    /// <summary>
    /// brotli's <c>BuildAndStoreLiteralPrefixCode</c>: a literal code from the fragment's bytes, before
    /// LZ77, sampled when it is long. Returns the estimated ratio, in millibytes a byte.
    /// </summary>
    private static nuint BuildAndStoreLiteralPrefixCode(OnePassArena s, ReadOnlySpan<byte> input, Span<byte> depths, Span<ushort> bits, ref int storageIx, Span<byte> storage)
    {
        Span<uint> histogram = s.Histogram.AsSpan(0, 256);
        int inputSize = input.Length;
        nuint histogramTotal;
        if (inputSize < (1 << 15))
        {
            // Every byte, in four histograms interleaved: one would make each repeated byte wait for the
            // increment before. The first 11 samples weigh three times, for the balance LZ77 brings.
            Span<uint> parts = stackalloc uint[4 * 256];
            int i = 0;
            for (; i + 4 <= inputSize; i += 4)
            {
                ReadOnlySpan<byte> four = input.Slice(i, 4);
                ++parts[four[0]];
                ++parts[256 + four[1]];
                ++parts[512 + four[2]];
                ++parts[768 + four[3]];
            }

            for (; i < inputSize; ++i)
            {
                ++parts[input[i]];
            }

            histogramTotal = (nuint)inputSize + MergeAndAdjust(parts, 4, histogram, 0);
        }
        else
        {
            // A sample every 29 bytes, one more for each byte, as a sample says nothing of the bytes it misses.
            const int SampleRate = 29;
            histogram.Clear();
            for (int i = 0; i < inputSize; i += SampleRate)
            {
                ++histogram[input[i]];
            }

            histogramTotal = (nuint)((inputSize + SampleRate - 1) / SampleRate) + MergeAndAdjust(histogram, 1, histogram, 1);
        }

        BitStream.BuildAndStoreHuffmanTreeFast(s.Tree, histogram, histogramTotal, 8, depths, bits, ref storageIx, storage);
        BitWriter.CombineCodes(depths, bits, s.LitCodes, 256);

        // The bits the histogram takes: brotli skips the empty counts, which add nothing. Each product takes
        // 19 bits at most, so 64 of them sum in 32.
        ReadOnlySpan<byte> depth = depths.Slice(0, 256);
        nuint literalRatio = 0;
        int at = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<uint> sum = Vector128<uint>.Zero;
            for (; at < 256; at += 16)
            {
                (Vector128<ushort> low, Vector128<ushort> high) = Vector128.Widen(Vector128.Create(depth.Slice(at, 16)));
                (Vector128<uint> d0, Vector128<uint> d1) = Vector128.Widen(low);
                (Vector128<uint> d2, Vector128<uint> d3) = Vector128.Widen(high);
                ReadOnlySpan<uint> counts = histogram.Slice(at, 16);
                sum += (Vector128.Create(counts.Slice(0, 4)) * d0) + (Vector128.Create(counts.Slice(4, 4)) * d1) + (Vector128.Create(counts.Slice(8, 4)) * d2) + (Vector128.Create(counts.Slice(12, 4)) * d3);
            }

            literalRatio = Vector128.Sum(sum);
        }

        for (; at < 256; ++at)
        {
            literalRatio += histogram[at] * (nuint)depth[at];
        }

        return literalRatio * 125 / histogramTotal;
    }

    /// <summary>
    /// The <paramref name="numParts"/> histograms of <paramref name="parts"/> summed into
    /// <paramref name="histogram"/> (which may be the first), each count raised by <paramref name="extra"/>
    /// and by twice its first 11: returns what that adds to the total, as brotli's loop does.
    /// </summary>
    private static nuint MergeAndAdjust(ReadOnlySpan<uint> parts, int numParts, Span<uint> histogram, uint extra)
    {
        nuint added = 0;
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<uint> sum = Vector128<uint>.Zero;
            for (; i < 256; i += 4)
            {
                Vector128<uint> count = Vector128.Create(parts.Slice(i, 4));
                for (int p = 1; p < numParts; ++p)
                {
                    count += Vector128.Create(parts.Slice((p * 256) + i, 4));
                }

                Vector128<uint> adjust = Vector128.Create(extra) + (Vector128.Min(count, Vector128.Create(11u)) << 1);
                (count + adjust).CopyTo(histogram.Slice(i, 4));
                sum += adjust;
            }

            added = Vector128.Sum(sum);
        }

        for (; i < 256; ++i)
        {
            uint count = parts[i];
            for (int p = 1; p < numParts; ++p)
            {
                count += parts[(p * 256) + i];
            }

            uint adjust = extra + (2 * Math.Min(count, 11u));
            histogram[i] = count + adjust;
            added += adjust;
        }

        return added;
    }

    /// <summary>
    /// brotli's <c>BuildAndStoreCommandPrefixCode</c> of quality 0: the command and distance codes from the
    /// histogram, mapped between the small alphabet and the full one.
    /// </summary>
    public static void BuildAndStoreCommandPrefixCode(OnePassArena s, ref int storageIx, Span<byte> storage)
    {
        ReadOnlySpan<uint> histogram = s.CmdHisto;
        Span<byte> depth = s.CmdDepth;
        Span<ushort> bits = s.CmdBits;
        Span<byte> tmpDepth = s.TmpDepth;
        Span<ushort> tmpBits = s.TmpBits;
        tmpDepth.Clear();
        EntropyEncode.CreateHuffmanTree(histogram.Slice(0, 64), 15, s.Tree, depth);
        EntropyEncode.CreateHuffmanTree(histogram.Slice(64, 64), 14, s.Tree, depth.Slice(64));

        // The command codes' bits, through the full alphabet's order.
        depth.Slice(0, 24).CopyTo(tmpDepth);
        depth.Slice(40, 8).CopyTo(tmpDepth.Slice(24));
        depth.Slice(24, 8).CopyTo(tmpDepth.Slice(32));
        depth.Slice(48, 8).CopyTo(tmpDepth.Slice(40));
        depth.Slice(32, 8).CopyTo(tmpDepth.Slice(48));
        depth.Slice(56, 8).CopyTo(tmpDepth.Slice(56));
        EntropyEncode.ConvertBitDepthsToSymbols(tmpDepth.Slice(0, 64), tmpBits);
        tmpBits.Slice(0, 24).CopyTo(bits);
        tmpBits.Slice(32, 8).CopyTo(bits.Slice(24));
        tmpBits.Slice(48, 8).CopyTo(bits.Slice(32));
        tmpBits.Slice(24, 8).CopyTo(bits.Slice(40));
        tmpBits.Slice(40, 8).CopyTo(bits.Slice(48));
        tmpBits.Slice(56, 8).CopyTo(bits.Slice(56));
        EntropyEncode.ConvertBitDepthsToSymbols(depth.Slice(64, 64), bits.Slice(64));

        // The depths over the full command alphabet.
        tmpDepth.Slice(0, 64).Clear();
        depth.Slice(0, 8).CopyTo(tmpDepth);
        depth.Slice(8, 8).CopyTo(tmpDepth.Slice(64));
        depth.Slice(16, 8).CopyTo(tmpDepth.Slice(128));
        depth.Slice(24, 8).CopyTo(tmpDepth.Slice(192));
        depth.Slice(32, 8).CopyTo(tmpDepth.Slice(384));
        for (int i = 0; i < 8; ++i)
        {
            tmpDepth[128 + (8 * i)] = depth[40 + i];
            tmpDepth[256 + (8 * i)] = depth[48 + i];
            tmpDepth[448 + (8 * i)] = depth[56 + i];
        }

        BitStream.StoreHuffmanTree(tmpDepth.Slice(0, Prefix.NumCommandSymbols), s.Tree, ref storageIx, storage);
        BitStream.StoreHuffmanTree(depth.Slice(64, 64), s.Tree, ref storageIx, storage);
        BitWriter.CombineCodes(depth, bits, s.CmdCodes, 128);
    }

    /// <summary>A combined code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteCode(ref BitSink sink, uint code) => sink.Write((int)(code >> 16), code & 0xFFFF);

    /// <summary>
    /// A combined code and its <paramref name="nbits"/> extra bits, in one write: the bits brotli writes in
    /// two. A code takes 15 bits at most, its extra bits 24.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteCode(ref BitSink sink, uint code, int nbits, nuint extra)
    {
        int depth = (int)(code >> 16);
        sink.Write(depth + nbits, (code & 0xFFFF) | ((ulong)extra << depth));
    }

    /// <summary>A combined code, its extra bits, then the code of the last distance, in one write.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteCode(ref BitSink sink, uint code, int nbits, nuint extra, uint lastDistanceCode)
    {
        int depth = (int)(code >> 16);
        int length = depth + nbits;
        sink.Write(length + (int)(lastDistanceCode >> 16), (code & 0xFFFF) | ((ulong)extra << depth) | ((ulong)(lastDistanceCode & 0xFFFF) << length));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitInsertLen(nuint insertLen, ReadOnlySpan<uint> codes, Span<uint> histo, ref BitSink sink)
    {
        if (insertLen < 6)
        {
            int code = (int)insertLen + 40;
            WriteCode(ref sink, codes[code]);
            ++histo[code];
        }
        else if (insertLen < 130)
        {
            nuint tail = insertLen - 2;
            int nbits = FastLog.Log2FloorNonZero(tail) - 1;
            nuint prefix = tail >> nbits;
            int code = (int)(((nuint)nbits << 1) + prefix + 42);
            WriteCode(ref sink, codes[code], nbits, tail - (prefix << nbits));
            ++histo[code];
        }
        else if (insertLen < 2114)
        {
            nuint tail = insertLen - 66;
            int nbits = FastLog.Log2FloorNonZero(tail);
            int code = nbits + 50;
            WriteCode(ref sink, codes[code], nbits, tail - ((nuint)1 << nbits));
            ++histo[code];
        }
        else
        {
            WriteCode(ref sink, codes[61], 12, insertLen - 2114);
            ++histo[61];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitLongInsertLen(nuint insertLen, ReadOnlySpan<uint> codes, Span<uint> histo, ref BitSink sink)
    {
        if (insertLen < 22594)
        {
            WriteCode(ref sink, codes[62], 14, insertLen - 6210);
            ++histo[62];
        }
        else
        {
            WriteCode(ref sink, codes[63], 24, insertLen - 22594);
            ++histo[63];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitCopyLen(nuint copyLen, ReadOnlySpan<uint> codes, Span<uint> histo, ref BitSink sink)
    {
        if (copyLen < 10)
        {
            int code = (int)copyLen + 14;
            WriteCode(ref sink, codes[code]);
            ++histo[code];
        }
        else if (copyLen < 134)
        {
            nuint tail = copyLen - 6;
            int nbits = FastLog.Log2FloorNonZero(tail) - 1;
            nuint prefix = tail >> nbits;
            int code = (int)(((nuint)nbits << 1) + prefix + 20);
            WriteCode(ref sink, codes[code], nbits, tail - (prefix << nbits));
            ++histo[code];
        }
        else if (copyLen < 2118)
        {
            nuint tail = copyLen - 70;
            int nbits = FastLog.Log2FloorNonZero(tail);
            int code = nbits + 28;
            WriteCode(ref sink, codes[code], nbits, tail - ((nuint)1 << nbits));
            ++histo[code];
        }
        else
        {
            WriteCode(ref sink, codes[39], 24, copyLen - 2118);
            ++histo[39];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitCopyLenLastDistance(nuint copyLen, ReadOnlySpan<uint> codes, Span<uint> histo, ref BitSink sink)
    {
        if (copyLen < 12)
        {
            int code = (int)copyLen - 4;
            WriteCode(ref sink, codes[code]);
            ++histo[code];
        }
        else if (copyLen < 72)
        {
            nuint tail = copyLen - 8;
            int nbits = FastLog.Log2FloorNonZero(tail) - 1;
            nuint prefix = tail >> nbits;
            int code = (int)(((nuint)nbits << 1) + prefix + 4);
            WriteCode(ref sink, codes[code], nbits, tail - (prefix << nbits));
            ++histo[code];
        }
        else if (copyLen < 136)
        {
            nuint tail = copyLen - 8;
            int code = (int)(tail >> 5) + 30;
            WriteCode(ref sink, codes[code], 5, tail & 31, codes[64]);
            ++histo[code];
            ++histo[64];
        }
        else if (copyLen < 2120)
        {
            nuint tail = copyLen - 72;
            int nbits = FastLog.Log2FloorNonZero(tail);
            int code = nbits + 28;
            WriteCode(ref sink, codes[code], nbits, tail - ((nuint)1 << nbits), codes[64]);
            ++histo[code];
            ++histo[64];
        }
        else
        {
            WriteCode(ref sink, codes[39], 24, copyLen - 2120, codes[64]);
            ++histo[39];
            ++histo[64];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitDistance(nuint distance, ReadOnlySpan<uint> codes, Span<uint> histo, ref BitSink sink)
    {
        nuint d = distance + 3;
        int nbits = FastLog.Log2FloorNonZero(d) - 1;
        nuint prefix = (d >> nbits) & 1;
        nuint offset = (2 + prefix) << nbits;
        int distCode = (int)((2 * ((nuint)nbits - 1)) + prefix + 80);
        WriteCode(ref sink, codes[distCode], nbits, d - offset);
        ++histo[distCode];
    }

    /// <summary>
    /// The literals, four codes a write: their codes take 14 bits at most, so four fit in the 56 bits a
    /// write takes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitLiterals(ReadOnlySpan<byte> input, ReadOnlySpan<uint> codes, ref BitSink sink)
    {
        codes = codes.Slice(0, 256);
        int i = 0;
        for (; i + 4 <= input.Length; i += 4)
        {
            // One code after the other, so that few values are live at once.
            ReadOnlySpan<byte> four = input.Slice(i, 4);
            uint code = codes[four[0]];
            ulong bits = code & 0xFFFF;
            int count = (int)(code >> 16);
            code = codes[four[1]];
            bits |= (ulong)(code & 0xFFFF) << count;
            count += (int)(code >> 16);
            code = codes[four[2]];
            bits |= (ulong)(code & 0xFFFF) << count;
            count += (int)(code >> 16);
            code = codes[four[3]];
            bits |= (ulong)(code & 0xFFFF) << count;
            count += (int)(code >> 16);
            sink.Write(count, bits);
        }

        for (; i < input.Length; i++)
        {
            WriteCode(ref sink, codes[input[i]]);
        }
    }

    /// <summary>brotli's <c>BrotliStoreMetaBlockHeader</c> of the fast qualities: never the last meta-block.</summary>
    public static void StoreMetaBlockHeader(int length, bool isUncompressed, ref int storageIx, Span<byte> storage)
    {
        int nibbles = 6;
        BitWriter.Write(1, 0, ref storageIx, storage);
        if (length <= (1 << 16))
        {
            nibbles = 4;
        }
        else if (length <= (1 << 20))
        {
            nibbles = 5;
        }

        BitWriter.Write(2, (ulong)(nibbles - 4), ref storageIx, storage);
        BitWriter.Write(nibbles * 4, (ulong)(length - 1), ref storageIx, storage);
        BitWriter.Write(1, isUncompressed ? 1u : 0u, ref storageIx, storage);
    }

    /// <summary>brotli's <c>UpdateBits</c>: overwrites <paramref name="numBits"/> bits at <paramref name="position"/>.</summary>
    private static void UpdateBits(int numBits, uint bits, int position, Span<byte> array)
    {
        while (numBits > 0)
        {
            int bytePos = position >> 3;
            int unchanged = position & 7;
            int changed = Math.Min(numBits, 8 - unchanged);
            int totalBits = unchanged + changed;
            uint mask = ~((1u << totalBits) - 1u) | ((1u << unchanged) - 1u);
            uint unchangedBits = array[bytePos] & mask;
            uint changedBits = bits & ((1u << changed) - 1u);
            array[bytePos] = (byte)((changedBits << unchanged) | unchangedBits);
            numBits -= changed;
            bits >>= changed;
            position += changed;
        }
    }

    private static bool ShouldMergeBlock(OnePassArena s, ReadOnlySpan<byte> data, ReadOnlySpan<byte> depths)
    {
        Span<uint> histo = s.Histogram.AsSpan(0, 256);
        const int SampleRate = 43;
        histo.Clear();
        for (int i = 0; i < data.Length; i += SampleRate)
        {
            ++histo[data[i]];
        }

        nuint total = (nuint)((data.Length + SampleRate - 1) / SampleRate);
        double r = ((FastLog.FastLog2(total) + 0.5) * (double)total) + 200;
        for (int i = 0; i < 256; ++i)
        {
            r -= (double)histo[i] * (depths[i] + FastLog.FastLog2(histo[i]));
        }

        return r >= 0.0;
    }

    private static bool ShouldUseUncompressedMode(int metaBlockStart, int nextEmit, nuint insertLen, nuint literalRatio)
    {
        nuint compressed = (nuint)(nextEmit - metaBlockStart);
        if (compressed * 50 > insertLen)
        {
            return false;
        }

        return literalRatio > MinRatio;
    }

    /// <summary>The meta-block from its header at <paramref name="storageIxStart"/> stored uncompressed: the position past it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int EmitUncompressedMetaBlock(ReadOnlySpan<byte> bytes, int storageIxStart, int storageIx, Span<byte> storage)
    {
        int length = bytes.Length;
        BitWriter.Rewind(storageIxStart, ref storageIx, storage);
        StoreMetaBlockHeader(length, true, ref storageIx, storage);
        storageIx = (storageIx + 7) & ~7;
        bytes.CopyTo(storage.Slice(storageIx >> 3));
        storageIx += length << 3;
        storage[storageIx >> 3] = 0;
        return storageIx;
    }

    /// <summary>
    /// brotli's <c>BrotliCompressFragmentFast</c>: <paramref name="input"/>, then, if it is the last
    /// fragment, the empty last meta-block. <paramref name="table"/> holds <paramref name="tableSize"/>
    /// zeros, an odd power of two from 2^9 to 2^15.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Compress(OnePassArena s, ReadOnlySpan<byte> input, bool isLast, Span<int> table, int tableSize, ref int storageIx, Span<byte> storage)
    {
        int initialStorageIx = storageIx;
        int tableBits = BitOperations.Log2((uint)tableSize);
        if (input.IsEmpty)
        {
            BitWriter.Write(1, 1, ref storageIx, storage);
            BitWriter.Write(1, 1, ref storageIx, storage);
            storageIx = (storageIx + 7) & ~7;
            return;
        }

        CompressImpl(s, input, table.Slice(0, tableSize), tableBits, ref storageIx, storage, isLast);

        // Larger than the fragment stored as it is: stored as it is.
        if (storageIx - initialStorageIx > 31 + (input.Length << 3))
        {
            storageIx = EmitUncompressedMetaBlock(input, initialStorageIx, storageIx, storage);
        }

        if (isLast)
        {
            BitWriter.Write(1, 1, ref storageIx, storage);
            BitWriter.Write(1, 1, ref storageIx, storage);
            storageIx = (storageIx + 7) & ~7;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompressImpl(OnePassArena s, ReadOnlySpan<byte> data, Span<int> table, int tableBits, ref int storageIxRef, Span<byte> storage, bool isLast)
    {
        // The bit position in a local, which the JIT keeps in a register.
        int storageIx = storageIxRef;
        Span<uint> cmdHisto = s.CmdHisto;
        byte[] litDepth = s.LitDepth;
        ushort[] litBits = s.LitBits;

        // Positions in the data. The first byte not covered by a copy: the bytes from there to the next copy are literals.
        int input = 0;
        int inputSize = data.Length;
        int nextEmit = 0;
        int metaBlockStart = 0;

        int blockSize = Math.Min(inputSize, FirstBlockSize);
        int totalBlockSize = blockSize;

        // Where the MLEN of the meta-block header is, to extend the meta-block later.
        int mlenStorageIx = storageIx + 3;
        nuint literalRatio;
        int ipEnd;
        int shift = 64 - tableBits;

        StoreMetaBlockHeader(blockSize, false, ref storageIx, storage);

        // No block splits, no contexts.
        BitWriter.Write(13, 0, ref storageIx, storage);
        literalRatio = BuildAndStoreLiteralPrefixCode(s, data.Slice(input, blockSize), litDepth, litBits, ref storageIx, storage);

        // The command and distance codes, stored ahead.
        for (int i = 0; i + 7 < s.CmdCodeNumBits; i += 8)
        {
            BitWriter.Write(8, s.CmdCode[i >> 3], ref storageIx, storage);
        }

        BitWriter.Write(s.CmdCodeNumBits & 7, s.CmdCode[s.CmdCodeNumBits >> 3], ref storageIx, storage);

    EmitCommands:
        // The histograms of this block's commands and distances, for the next block's codes.
        CmdHistoSeed.CopyTo(cmdHisto);
        ipEnd = input + blockSize;
        if (blockSize >= InputMarginBytes)
        {
            // The last block keeps a margin of 16 bytes, so that every distance stays within the window
            // less 16; the others 5, so that no copy goes past the block.
            int lenLimit = Math.Min(blockSize - MinMatchLength, inputSize - InputMarginBytes);
            int uncompressedEnd = EmitCommandsLoop(s, data, input, ipEnd, input + lenLimit, table, shift, ref nextEmit, ref storageIx, storage, metaBlockStart, literalRatio, mlenStorageIx);
            if (uncompressedEnd >= 0)
            {
                // The block so far stored as it is, the rest a new block.
                inputSize -= uncompressedEnd - input;
                input = uncompressedEnd;
                nextEmit = input;
                goto NextBlock;
            }
        }

        input += blockSize;
        inputSize -= blockSize;
        blockSize = Math.Min(inputSize, MergeBlockSize);

        // Whether to extend this meta-block rather than end it with an insert-only command.
        if (inputSize > 0 && totalBlockSize + blockSize <= (1 << 20) && ShouldMergeBlock(s, data.Slice(input, blockSize), litDepth))
        {
            // The current size and the new one both take 5 nibbles.
            totalBlockSize += blockSize;
            UpdateBits(20, (uint)(totalBlockSize - 1), mlenStorageIx, storage);
            goto EmitCommands;
        }

        // The remaining bytes, as literals.
        if (nextEmit < ipEnd)
        {
            nuint insert = (nuint)(ipEnd - nextEmit);
            if (insert < 6210)
            {
                var sink = new BitSink(storageIx, storage);
                EmitInsertLen(insert, s.CmdCodes, cmdHisto, ref sink);
                EmitLiterals(data.Slice(nextEmit, (int)insert), s.LitCodes, ref sink);
                storageIx = sink.Finish();
            }
            else if (ShouldUseUncompressedMode(metaBlockStart, nextEmit, insert, literalRatio))
            {
                storageIx = EmitUncompressedMetaBlock(data.Slice(metaBlockStart, ipEnd - metaBlockStart), mlenStorageIx - 3, storageIx, storage);
            }
            else
            {
                var sink = new BitSink(storageIx, storage);
                EmitLongInsertLen(insert, s.CmdCodes, cmdHisto, ref sink);
                EmitLiterals(data.Slice(nextEmit, (int)insert), s.LitCodes, ref sink);
                storageIx = sink.Finish();
            }
        }

        nextEmit = ipEnd;

    NextBlock:
        // More data: a new meta-block header and codes, then its commands.
        if (inputSize > 0)
        {
            metaBlockStart = input;
            blockSize = Math.Min(inputSize, FirstBlockSize);
            totalBlockSize = blockSize;
            mlenStorageIx = storageIx + 3;
            StoreMetaBlockHeader(blockSize, false, ref storageIx, storage);
            BitWriter.Write(13, 0, ref storageIx, storage);
            literalRatio = BuildAndStoreLiteralPrefixCode(s, data.Slice(input, blockSize), litDepth, litBits, ref storageIx, storage);
            BuildAndStoreCommandPrefixCode(s, ref storageIx, storage);
            goto EmitCommands;
        }

        storageIxRef = storageIx;
        if (!isLast)
        {
            // The command and distance codes of the next fragment, stored ahead.
            s.CmdCode[0] = 0;
            s.CmdCodeNumBits = 0;
            int numBits = 0;
            BuildAndStoreCommandPrefixCode(s, ref numBits, s.CmdCode);
            s.CmdCodeNumBits = numBits;
        }
    }

    /// <summary>
    /// The commands of a block, the hot loop of brotli's <c>BrotliCompressFragmentFastImpl</c>, in a method of
    /// its own, so that the JIT keeps its variables in registers: -1 at the block's end, or the start of
    /// the copy before which the meta-block was stored uncompressed.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int EmitCommandsLoop(OnePassArena s, ReadOnlySpan<byte> data, int ip, int ipEnd, int ipLimit, Span<int> table, int shift, ref int nextEmitRef, ref int storageIxRef, Span<byte> storage, int metaBlockStart, nuint literalRatio, int mlenStorageIx)
    {
        var sink = new BitSink(storageIxRef, storage);
        int nextEmit = nextEmitRef;
        ReadOnlySpan<uint> cmdCodes = s.CmdCodes;
        Span<uint> cmdHisto = s.CmdHisto;
        ReadOnlySpan<uint> litCodes = s.LitCodes;
        int lastDistance = -1;
        int result = -1;
        uint nextHash = Hash(data, ++ip, shift);
        while (true)
        {
            // Step 1: the next match of 5 bytes.
            ulong found = FindMatch(data, ip, ipLimit, nextHash, table, shift, lastDistance);
            if (found == NoMatch)
            {
                goto Done;
            }

            ip = (int)(uint)found;
            int candidate = (int)(uint)(found >> 32);

            // Step 2: the match and the literals before it, then the matches right after it.
            {
                int baseMatch = ip;
                nuint matched = 5 + FindMatchLength.WithLimit(data, (nuint)(candidate + 5), (nuint)(ip + 5), (nuint)(ipEnd - ip) - 5);
                int distance = baseMatch - candidate;
                nuint insert = (nuint)(baseMatch - nextEmit);
                ip += (int)matched;
                if (insert < 6210)
                {
                    EmitInsertLen(insert, cmdCodes, cmdHisto, ref sink);
                }
                else if (ShouldUseUncompressedMode(metaBlockStart, nextEmit, insert, literalRatio))
                {
                    sink = new BitSink(EmitUncompressedMetaBlock(data.Slice(metaBlockStart, baseMatch - metaBlockStart), mlenStorageIx - 3, sink.Finish(), storage), storage);
                    result = baseMatch;
                    goto Done;
                }
                else
                {
                    EmitLongInsertLen(insert, cmdCodes, cmdHisto, ref sink);
                }

                EmitLiterals(data.Slice(nextEmit, (int)insert), litCodes, ref sink);
                if (distance == lastDistance)
                {
                    WriteCode(ref sink, cmdCodes[64]);
                    ++cmdHisto[64];
                }
                else
                {
                    EmitDistance((nuint)distance, cmdCodes, cmdHisto, ref sink);
                    lastDistance = distance;
                }

                EmitCopyLenLastDistance(matched, cmdCodes, cmdHisto, ref sink);
                nextEmit = ip;
                if (ip >= ipLimit)
                {
                    goto Done;
                }

                // The hashes of some positions within the copy, before going on at ip.
                ulong inputBytes = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(ip - 3, 8));
                uint prevHash = HashBytesAtOffset(inputBytes, 0, shift);
                uint curHash = HashBytesAtOffset(inputBytes, 3, shift);
                table[(int)prevHash] = ip - 3;
                prevHash = HashBytesAtOffset(inputBytes, 1, shift);
                table[(int)prevHash] = ip - 2;
                prevHash = HashBytesAtOffset(inputBytes, 2, shift);
                table[(int)prevHash] = ip - 1;
                candidate = table[(int)curHash];
                table[(int)curHash] = ip;
            }

            while (IsMatch(data, ip, candidate))
            {
                // A match at ip, with no literal before it.
                int baseMatch = ip;
                nuint matched = 5 + FindMatchLength.WithLimit(data, (nuint)(candidate + 5), (nuint)(ip + 5), (nuint)(ipEnd - ip) - 5);
                if (ip - candidate > MaxDistance)
                {
                    break;
                }

                ip += (int)matched;
                lastDistance = baseMatch - candidate;
                EmitCopyLen(matched, cmdCodes, cmdHisto, ref sink);
                EmitDistance((nuint)lastDistance, cmdCodes, cmdHisto, ref sink);
                nextEmit = ip;
                if (ip >= ipLimit)
                {
                    goto Done;
                }

                ulong inputBytes = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(ip - 3, 8));
                uint prevHash = HashBytesAtOffset(inputBytes, 0, shift);
                uint curHash = HashBytesAtOffset(inputBytes, 3, shift);
                table[(int)prevHash] = ip - 3;
                prevHash = HashBytesAtOffset(inputBytes, 1, shift);
                table[(int)prevHash] = ip - 2;
                prevHash = HashBytesAtOffset(inputBytes, 2, shift);
                table[(int)prevHash] = ip - 1;
                candidate = table[(int)curHash];
                table[(int)curHash] = ip;
            }

            nextHash = Hash(data, ++ip, shift);
        }

    Done:
        storageIxRef = sink.Finish();
        nextEmitRef = nextEmit;
        return result;
    }

    /// <summary>FindMatch's answer when it reaches <c>ipLimit</c>.</summary>
    private const ulong NoMatch = ulong.MaxValue;

    /// <summary>
    /// The trawl of brotli's <c>BrotliCompressFragmentFastImpl</c>: the next position from <paramref name="ip"/>
    /// on whose 5 bytes match those at the last distance or at the table's position, looking further
    /// apart after every 32 bytes without one; <see cref="NoMatch"/> past <paramref name="ipLimit"/>. A method
    /// of its own, so that its few variables stay in registers; it returns the position and the candidate,
    /// the candidate's in the high half, so that the caller's stay in registers too. The next hash it
    /// computes is not returned: the caller computes it again.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong FindMatch(ReadOnlySpan<byte> data, int ip, int ipLimit, uint nextHash, Span<int> table, int shift, int lastDistance)
    {
        uint skip = 32;
        int nextIp = ip;
        int candidate;
        do
        {
            do
            {
                uint hash = nextHash;
                uint bytesBetweenHashLookups = skip++ >> 5;
                ip = nextIp;
                nextIp = ip + (int)bytesBetweenHashLookups;
                if (nextIp > ipLimit)
                {
                    return NoMatch;
                }

                nextHash = Hash(data, nextIp, shift);
                candidate = ip - lastDistance;
                if (IsMatch(data, ip, candidate) && candidate < ip)
                {
                    table[(int)hash] = ip;
                    break;
                }

                candidate = table[(int)hash];
                table[(int)hash] = ip;
            }
            while (!IsMatch(data, ip, candidate));
        }

        // A candidate too far is no match: search on.
        while (ip - candidate > MaxDistance);
        return (ulong)(uint)ip | ((ulong)(uint)candidate << 32);
    }
}
