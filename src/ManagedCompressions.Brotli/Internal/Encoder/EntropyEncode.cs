using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>A node of a Huffman tree, brotli's <c>HuffmanTree</c>.</summary>
internal struct HuffmanTree
{
    public uint TotalCount;
    public short IndexLeft;
    public short IndexRightOrValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HuffmanTree(uint count, short left, short right)
    {
        TotalCount = count;
        IndexLeft = left;
        IndexRightOrValue = right;
    }
}

/// <summary>
/// brotli's Huffman code construction (<c>c/enc/entropy_encode.c</c>), step for step: the same depths
/// from the same counts, and the same RLE of the depths, hence the same bytes.
/// </summary>
internal static unsafe class EntropyEncode
{
    /// <summary>The order of a sort of Huffman tree nodes.</summary>
    public interface IComparer
    {
        static abstract bool Less(in HuffmanTree a, in HuffmanTree b);
    }

    /// <summary>Least popular first, then the larger symbol first: <c>entropy_encode.c</c>'s order.</summary>
    public struct ByCountThenValue : IComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Less(in HuffmanTree a, in HuffmanTree b) =>
            a.TotalCount != b.TotalCount ? a.TotalCount < b.TotalCount : a.IndexRightOrValue > b.IndexRightOrValue;
    }

    /// <summary>Least popular first, ties left as the sort leaves them: <c>brotli_bit_stream.c</c>'s order.</summary>
    public struct ByCount : IComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Less(in HuffmanTree a, in HuffmanTree b) => a.TotalCount < b.TotalCount;
    }

    private static ReadOnlySpan<byte> ShellGaps => [132, 57, 23, 10, 4, 1];

    /// <summary>
    /// brotli's <c>SortHuffmanTreeItems</c>: an insertion sort below 13 items, a Shell sort over its gaps
    /// above. With <see cref="ByCount"/>, ties end where this very algorithm puts them, so it is kept as is.
    /// </summary>
    public static void Sort<TComparer>(HuffmanTree* items, int n)
        where TComparer : struct, IComparer
    {
        if (n < 13)
        {
            for (int i = 1; i < n; ++i)
            {
                HuffmanTree tmp = items[i];
                int k = i;
                int j = i - 1;
                while (TComparer.Less(tmp, items[j]))
                {
                    items[k] = items[j];
                    k = j;
                    if (j-- == 0)
                    {
                        break;
                    }
                }

                items[k] = tmp;
            }

            return;
        }

        for (int g = n < 57 ? 2 : 0; g < 6; ++g)
        {
            int gap = ShellGaps[g];
            for (int i = gap; i < n; ++i)
            {
                int j = i;
                HuffmanTree tmp = items[i];
                for (; j >= gap && TComparer.Less(tmp, items[j - gap]); j -= gap)
                {
                    items[j] = items[j - gap];
                }

                items[j] = tmp;
            }
        }
    }

    /// <summary>The depth of each leaf under <paramref name="p0"/>; false when one exceeds <paramref name="maxDepth"/>.</summary>
    public static bool SetDepth(int p0, HuffmanTree* pool, byte* depth, int maxDepth)
    {
        int* stack = stackalloc int[16];
        int level = 0;
        int p = p0;
        stack[0] = -1;
        while (true)
        {
            if (pool[p].IndexLeft >= 0)
            {
                level++;
                if (level > maxDepth)
                {
                    return false;
                }

                stack[level] = pool[p].IndexRightOrValue;
                p = pool[p].IndexLeft;
                continue;
            }

            depth[pool[p].IndexRightOrValue] = (byte)level;
            while (level >= 0 && stack[level] == -1)
            {
                level--;
            }

            if (level < 0)
            {
                return true;
            }

            p = stack[level];
            stack[level] = -1;
        }
    }

    /// <summary>
    /// brotli's <c>BrotliCreateHuffmanTree</c>: the depths of a Huffman code of at most
    /// <paramref name="treeLimit"/> bits over <paramref name="data"/>'s counts, the smallest counts raised
    /// until it fits. <paramref name="tree"/> holds 2 * <paramref name="length"/> + 1 nodes.
    /// </summary>
    public static void CreateHuffmanTree(uint* data, int length, int treeLimit, HuffmanTree* tree, byte* depth)
    {
        var sentinel = new HuffmanTree(uint.MaxValue, -1, -1);
        for (uint countLimit = 1; ; countLimit *= 2)
        {
            int n = 0;
            for (int i = length; i != 0;)
            {
                --i;
                if (data[i] != 0)
                {
                    uint count = Math.Max(data[i], countLimit);
                    tree[n++] = new HuffmanTree(count, -1, (short)i);
                }
            }

            // As in brotli, an empty histogram would run the merges below from k = -1, out of the tree:
            // every caller returns before, on a count of one symbol or none.
            Debug.Assert(n >= 1, "a symbol to build the tree of");
            if (n == 1)
            {
                // A single symbol.
                depth[tree[0].IndexRightOrValue] = 1;
                break;
            }

            Sort<ByCountThenValue>(tree, n);

            // The sorted leaves, a sentinel, the parents in increasing order, a sentinel.
            tree[n] = sentinel;
            tree[n + 1] = sentinel;
            int leaf = 0;
            int parent = n + 1;
            for (int k = n - 1; k != 0; --k)
            {
                int left;
                int right;
                if (tree[leaf].TotalCount <= tree[parent].TotalCount)
                {
                    left = leaf++;
                }
                else
                {
                    left = parent++;
                }

                if (tree[leaf].TotalCount <= tree[parent].TotalCount)
                {
                    right = leaf++;
                }
                else
                {
                    right = parent++;
                }

                int end = (2 * n) - k;
                tree[end].TotalCount = tree[left].TotalCount + tree[right].TotalCount;
                tree[end].IndexLeft = (short)left;
                tree[end].IndexRightOrValue = (short)right;
                tree[end + 1] = sentinel;
            }

            if (SetDepth((2 * n) - 1, tree, depth, treeLimit))
            {
                break;
            }
        }
    }

    private static void Reverse(byte* v, int start, int end)
    {
        --end;
        while (start < end)
        {
            (v[start], v[end]) = (v[end], v[start]);
            ++start;
            --end;
        }
    }

    private static void WriteHuffmanTreeRepetitions(byte previousValue, byte value, int repetitions, ref int treeSize, byte* tree, byte* extraBitsData)
    {
        if (previousValue != value)
        {
            tree[treeSize] = value;
            extraBitsData[treeSize] = 0;
            ++treeSize;
            --repetitions;
        }

        if (repetitions == 7)
        {
            tree[treeSize] = value;
            extraBitsData[treeSize] = 0;
            ++treeSize;
            --repetitions;
        }

        if (repetitions < 3)
        {
            for (int i = 0; i < repetitions; ++i)
            {
                tree[treeSize] = value;
                extraBitsData[treeSize] = 0;
                ++treeSize;
            }

            return;
        }

        int start = treeSize;
        repetitions -= 3;
        while (true)
        {
            tree[treeSize] = Prefix.RepeatPreviousCodeLength;
            extraBitsData[treeSize] = (byte)(repetitions & 0x3);
            ++treeSize;
            repetitions >>= 2;
            if (repetitions == 0)
            {
                break;
            }

            --repetitions;
        }

        Reverse(tree, start, treeSize);
        Reverse(extraBitsData, start, treeSize);
    }

    private static void WriteHuffmanTreeRepetitionsZeros(int repetitions, ref int treeSize, byte* tree, byte* extraBitsData)
    {
        if (repetitions == 11)
        {
            tree[treeSize] = 0;
            extraBitsData[treeSize] = 0;
            ++treeSize;
            --repetitions;
        }

        if (repetitions < 3)
        {
            for (int i = 0; i < repetitions; ++i)
            {
                tree[treeSize] = 0;
                extraBitsData[treeSize] = 0;
                ++treeSize;
            }

            return;
        }

        int start = treeSize;
        repetitions -= 3;
        while (true)
        {
            tree[treeSize] = Prefix.RepeatZeroCodeLength;
            extraBitsData[treeSize] = (byte)(repetitions & 0x7);
            ++treeSize;
            repetitions >>= 3;
            if (repetitions == 0)
            {
                break;
            }

            --repetitions;
        }

        Reverse(tree, start, treeSize);
        Reverse(extraBitsData, start, treeSize);
    }

    /// <summary>
    /// brotli's <c>BrotliOptimizeHuffmanCountsForRle</c>: counts adjusted so that the code's depths
    /// compress better with the RLE of the code length code. <paramref name="goodForRle"/> is scratch of
    /// <paramref name="length"/> bytes.
    /// </summary>
    public static void OptimizeHuffmanCountsForRle(int length, uint* counts, byte* goodForRle)
    {
        int nonzeroCount = 0;
        const int StreakLimit = 1240;
        for (int i = 0; i < length; i++)
        {
            if (counts[i] != 0)
            {
                ++nonzeroCount;
            }
        }

        if (nonzeroCount < 16)
        {
            return;
        }

        while (length != 0 && counts[length - 1] == 0)
        {
            --length;
        }

        if (length == 0)
        {
            return;
        }

        {
            int nonzeros = 0;
            uint smallestNonzero = 1u << 30;
            for (int i = 0; i < length; ++i)
            {
                if (counts[i] != 0)
                {
                    ++nonzeros;
                    if (smallestNonzero > counts[i])
                    {
                        smallestNonzero = counts[i];
                    }
                }
            }

            if (nonzeros < 5)
            {
                return;
            }

            if (smallestNonzero < 4)
            {
                int zeros = length - nonzeros;
                if (zeros < 6)
                {
                    for (int i = 1; i < length - 1; ++i)
                    {
                        if (counts[i - 1] != 0 && counts[i] == 0 && counts[i + 1] != 0)
                        {
                            counts[i] = 1;
                        }
                    }
                }
            }

            if (nonzeros < 28)
            {
                return;
            }
        }

        // The counts that RLE codes already cover: runs of 5 zeros or more, of 7 other values or more.
        new Span<byte>(goodForRle, length).Clear();
        {
            uint symbol = counts[0];
            int step = 0;
            for (int i = 0; i <= length; ++i)
            {
                if (i == length || counts[i] != symbol)
                {
                    if ((symbol == 0 && step >= 5) || (symbol != 0 && step >= 7))
                    {
                        for (int k = 0; k < step; ++k)
                        {
                            goodForRle[i - k - 1] = 1;
                        }
                    }

                    step = 1;
                    if (i != length)
                    {
                        symbol = counts[i];
                    }
                }
                else
                {
                    ++step;
                }
            }
        }

        // The counts replaced where that makes more RLE codes, in 24.8 fixed point: brotli's arithmetic
        // with its widths: 32 bits for the products of counts, size_t for the rest, all unsigned.
        nuint stride = 0;
        nuint limit = (256u * (counts[0] + counts[1] + counts[2]) / 3) + 420;
        nuint sum = 0;
        for (int i = 0; i <= length; ++i)
        {
            if (i == length || goodForRle[i] != 0 || (i != 0 && goodForRle[i - 1] != 0) ||
                ((nuint)(256u * counts[i]) - limit + StreakLimit) >= 2 * StreakLimit)
            {
                if (stride >= 4 || (stride >= 3 && sum == 0))
                {
                    // The stride ends: what it has, if it has 4 or more, collapsed to their average.
                    nuint count = (sum + (stride / 2)) / stride;
                    if (count == 0)
                    {
                        count = 1;
                    }

                    if (sum == 0)
                    {
                        // An all-zero stride stays zeros.
                        count = 0;
                    }

                    for (nuint k = 0; k < stride; ++k)
                    {
                        counts[i - (int)k - 1] = (uint)count;
                    }
                }

                stride = 0;
                sum = 0;
                if (i < length - 2)
                {
                    limit = (256u * (counts[i] + counts[i + 1] + counts[i + 2]) / 3) + 420;
                }
                else if (i < length)
                {
                    limit = 256u * counts[i];
                }
                else
                {
                    limit = 0;
                }
            }

            ++stride;
            if (i != length)
            {
                sum += counts[i];
                if (stride >= 4)
                {
                    limit = ((256 * sum) + (stride / 2)) / stride;
                }

                if (stride == 4)
                {
                    limit += 120;
                }
            }
        }
    }

    private static void DecideOverRleUse(byte* depth, int length, out bool useRleForNonZero, out bool useRleForZero)
    {
        int totalRepsZero = 0;
        int totalRepsNonZero = 0;
        int countRepsZero = 1;
        int countRepsNonZero = 1;
        for (int i = 0; i < length;)
        {
            byte value = depth[i];
            int reps = 1;
            for (int k = i + 1; k < length && depth[k] == value; ++k)
            {
                ++reps;
            }

            if (reps >= 3 && value == 0)
            {
                totalRepsZero += reps;
                ++countRepsZero;
            }

            if (reps >= 4 && value != 0)
            {
                totalRepsNonZero += reps;
                ++countRepsNonZero;
            }

            i += reps;
        }

        useRleForNonZero = totalRepsNonZero > countRepsNonZero * 2;
        useRleForZero = totalRepsZero > countRepsZero * 2;
    }

    /// <summary>
    /// brotli's <c>BrotliWriteHuffmanTree</c>: the depths as code length code symbols, with the repeat
    /// codes 16 and 17 and their extra bits.
    /// </summary>
    public static void WriteHuffmanTree(byte* depth, int length, ref int treeSize, byte* tree, byte* extraBitsData)
    {
        byte previousValue = Prefix.InitialRepeatedCodeLength;
        bool useRleForNonZero = false;
        bool useRleForZero = false;

        // Without the trailing zeros.
        int newLength = length;
        for (int i = 0; i < length; ++i)
        {
            if (depth[length - i - 1] == 0)
            {
                --newLength;
            }
            else
            {
                break;
            }
        }

        // RLE only pays for the longer codes.
        if (length > 50)
        {
            DecideOverRleUse(depth, newLength, out useRleForNonZero, out useRleForZero);
        }

        for (int i = 0; i < newLength;)
        {
            byte value = depth[i];
            int reps = 1;
            if ((value != 0 && useRleForNonZero) || (value == 0 && useRleForZero))
            {
                for (int k = i + 1; k < newLength && depth[k] == value; ++k)
                {
                    ++reps;
                }
            }

            if (value == 0)
            {
                WriteHuffmanTreeRepetitionsZeros(reps, ref treeSize, tree, extraBitsData);
            }
            else
            {
                WriteHuffmanTreeRepetitions(previousValue, value, reps, ref treeSize, tree, extraBitsData);
                previousValue = value;
            }

            i += reps;
        }
    }

    private static ReadOnlySpan<byte> ReversedBytes =>
    [
        0x00, 0x80, 0x40, 0xC0, 0x20, 0xA0, 0x60, 0xE0, 0x10, 0x90, 0x50, 0xD0, 0x30, 0xB0, 0x70, 0xF0,
        0x08, 0x88, 0x48, 0xC8, 0x28, 0xA8, 0x68, 0xE8, 0x18, 0x98, 0x58, 0xD8, 0x38, 0xB8, 0x78, 0xF8,
        0x04, 0x84, 0x44, 0xC4, 0x24, 0xA4, 0x64, 0xE4, 0x14, 0x94, 0x54, 0xD4, 0x34, 0xB4, 0x74, 0xF4,
        0x0C, 0x8C, 0x4C, 0xCC, 0x2C, 0xAC, 0x6C, 0xEC, 0x1C, 0x9C, 0x5C, 0xDC, 0x3C, 0xBC, 0x7C, 0xFC,
        0x02, 0x82, 0x42, 0xC2, 0x22, 0xA2, 0x62, 0xE2, 0x12, 0x92, 0x52, 0xD2, 0x32, 0xB2, 0x72, 0xF2,
        0x0A, 0x8A, 0x4A, 0xCA, 0x2A, 0xAA, 0x6A, 0xEA, 0x1A, 0x9A, 0x5A, 0xDA, 0x3A, 0xBA, 0x7A, 0xFA,
        0x06, 0x86, 0x46, 0xC6, 0x26, 0xA6, 0x66, 0xE6, 0x16, 0x96, 0x56, 0xD6, 0x36, 0xB6, 0x76, 0xF6,
        0x0E, 0x8E, 0x4E, 0xCE, 0x2E, 0xAE, 0x6E, 0xEE, 0x1E, 0x9E, 0x5E, 0xDE, 0x3E, 0xBE, 0x7E, 0xFE,
        0x01, 0x81, 0x41, 0xC1, 0x21, 0xA1, 0x61, 0xE1, 0x11, 0x91, 0x51, 0xD1, 0x31, 0xB1, 0x71, 0xF1,
        0x09, 0x89, 0x49, 0xC9, 0x29, 0xA9, 0x69, 0xE9, 0x19, 0x99, 0x59, 0xD9, 0x39, 0xB9, 0x79, 0xF9,
        0x05, 0x85, 0x45, 0xC5, 0x25, 0xA5, 0x65, 0xE5, 0x15, 0x95, 0x55, 0xD5, 0x35, 0xB5, 0x75, 0xF5,
        0x0D, 0x8D, 0x4D, 0xCD, 0x2D, 0xAD, 0x6D, 0xED, 0x1D, 0x9D, 0x5D, 0xDD, 0x3D, 0xBD, 0x7D, 0xFD,
        0x03, 0x83, 0x43, 0xC3, 0x23, 0xA3, 0x63, 0xE3, 0x13, 0x93, 0x53, 0xD3, 0x33, 0xB3, 0x73, 0xF3,
        0x0B, 0x8B, 0x4B, 0xCB, 0x2B, 0xAB, 0x6B, 0xEB, 0x1B, 0x9B, 0x5B, 0xDB, 0x3B, 0xBB, 0x7B, 0xFB,
        0x07, 0x87, 0x47, 0xC7, 0x27, 0xA7, 0x67, 0xE7, 0x17, 0x97, 0x57, 0xD7, 0x37, 0xB7, 0x77, 0xF7,
        0x0F, 0x8F, 0x4F, 0xCF, 0x2F, 0xAF, 0x6F, 0xEF, 0x1F, 0x9F, 0x5F, 0xDF, 0x3F, 0xBF, 0x7F, 0xFF,
    ];

    /// <summary>
    /// The <paramref name="numBits"/> low bits of <paramref name="bits"/>, reversed: brotli reverses them a
    /// nibble at a time, this does all 16 bits a byte at a time and drops the low ones.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ReverseBits(int numBits, ushort bits)
    {
        ReadOnlySpan<byte> lut = ReversedBytes;
        uint reversed = ((uint)lut[bits & 0xFF] << 8) | lut[bits >> 8];
        return (ushort)(reversed >> (16 - numBits));
    }

    /// <summary>brotli's <c>BrotliConvertBitDepthsToSymbols</c>: the canonical codes of the depths, bit-reversed.</summary>
    public static void ConvertBitDepthsToSymbols(byte* depth, int length, ushort* bits)
    {
        const int MaxHuffmanBits = 16;
        ushort* blCount = stackalloc ushort[MaxHuffmanBits];
        ushort* nextCode = stackalloc ushort[MaxHuffmanBits];

        // Four counts interleaved: most depths are equal, zeros mostly, and one count would make each
        // increment wait for the one before.
        ushort* counts = stackalloc ushort[4 * MaxHuffmanBits];
        new Span<ushort>(counts, 4 * MaxHuffmanBits).Clear();
        int at = 0;
        for (; at + 4 <= length; at += 4)
        {
            ++counts[depth[at]];
            ++counts[MaxHuffmanBits + depth[at + 1]];
            ++counts[(2 * MaxHuffmanBits) + depth[at + 2]];
            ++counts[(3 * MaxHuffmanBits) + depth[at + 3]];
        }

        for (; at < length; ++at)
        {
            ++counts[depth[at]];
        }

        for (int i = 0; i < MaxHuffmanBits; ++i)
        {
            blCount[i] = (ushort)(counts[i] + counts[MaxHuffmanBits + i] + counts[(2 * MaxHuffmanBits) + i] + counts[(3 * MaxHuffmanBits) + i]);
        }

        blCount[0] = 0;
        nextCode[0] = 0;
        int code = 0;
        for (int i = 1; i < MaxHuffmanBits; ++i)
        {
            code = (code + blCount[i - 1]) << 1;
            nextCode[i] = (ushort)code;
        }

        for (int i = 0; i < length; ++i)
        {
            if (depth[i] != 0)
            {
                bits[i] = ReverseBits(depth[i], nextCode[depth[i]]++);
            }
        }
    }
}
