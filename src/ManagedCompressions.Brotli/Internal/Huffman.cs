using System;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// Builds the decoding tables of brotli's prefix codes (RFC 7932, section 3), with the layout of
/// brotli's decoder (<c>c/dec/huffman.c</c>): a root table indexed by the next
/// <see cref="RootBits"/> bits of the stream, and second-level tables for the longer codes.
/// </summary>
/// <remarks>
/// <para>
/// An entry is a <see cref="uint"/>: the code's length in its low byte, the symbol in its high half.
/// A root entry whose length exceeds <see cref="RootBits"/> points at a second-level table instead:
/// its low byte is <see cref="RootBits"/> plus that table's width, its high half the table's offset
/// from the root. The tables reproduce brotli's to the entry, so they decode the same streams.
/// </para>
/// <para>
/// The bits are read least significant first, and a code's first bit is its most significant: a table
/// is indexed by the bit-reversed code. Codes of length 0 (a code of a single symbol) read nothing.
/// </para>
/// </remarks>
internal static unsafe class Huffman
{
    /// <summary>The width of every root table, brotli's <c>HUFFMAN_TABLE_BITS</c>.</summary>
    public const int RootBits = 8;

    /// <summary>The longest code of the format.</summary>
    public const int MaxCodeLength = 15;

    /// <summary>The longest code of the code length code.</summary>
    public const int MaxCodeLengthCodeLength = 5;

    /// <summary>
    /// The largest table a code of <paramref name="alphabetSize"/> symbols can need: brotli's bound,
    /// the root table plus the second-level tables of the worst case (376 entries for every alphabet).
    /// </summary>
    public static int MaxTableSize(int alphabetSize) => alphabetSize + 376;

    /// <summary>Entries of the table of a simple code of up to 4 symbols, or of the code length code.</summary>
    public const int SimpleTableSize = 1 << RootBits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Entry(int bits, int value) => ((uint)value << 16) | (uint)bits;

    /// <summary>
    /// The bit reversal of the 8 low bits: brotli keeps the next key in reversed order so that it
    /// can be incremented as a plain integer.
    /// </summary>
    private static ReadOnlySpan<byte> ReverseBits =>
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

    /// <summary>The lowest bit of the reversed key, brotli's <c>BROTLI_REVERSE_BITS_LOWEST</c>.</summary>
    private const uint ReverseBitsLowest = 1u << 7;

    /// <summary>Writes <paramref name="code"/> at <c>table[0]</c>, <c>table[step]</c>, ..., up to <paramref name="end"/>, a multiple of <paramref name="step"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Replicate(uint* table, int step, int end, uint code)
    {
        do
        {
            end -= step;
            table[end] = code;
        }
        while (end > 0);
    }

    /// <summary>The width of the next second-level table, from the counts of the lengths left.</summary>
    private static int NextTableBitSize(ushort* count, int length, int rootBits)
    {
        int left = 1 << (length - rootBits);
        while (length < MaxCodeLength)
        {
            left -= count[length];
            if (left <= 0)
            {
                break;
            }

            ++length;
            left <<= 1;
        }

        return length - rootBits;
    }

    /// <summary>
    /// The table of the code length code: 18 symbols of at most 5 bits, read with 5 bits of lookahead
    /// (32 entries). <paramref name="count"/> is the histogram of <paramref name="codeLengths"/>.
    /// </summary>
    public static void BuildCodeLengthsTable(uint* table, byte* codeLengths, ushort* count)
    {
        const int Codes = 18;
        int* sorted = stackalloc int[Codes];
        int* offset = stackalloc int[MaxCodeLengthCodeLength + 1];

        int symbol = -1;
        for (int bits = 1; bits <= MaxCodeLengthCodeLength; bits++)
        {
            symbol += count[bits];
            offset[bits] = symbol;
        }

        // The symbols of length 0 go after the others.
        offset[0] = Codes - 1;

        // Sorted by length, and within a length by symbol.
        symbol = Codes;
        do
        {
            symbol--;
            sorted[offset[codeLengths[symbol]]--] = symbol;
        }
        while (symbol != 0);

        const int TableSize = 1 << MaxCodeLengthCodeLength;

        // All lengths but one are zero: that symbol reads no bit.
        if (offset[0] == 0)
        {
            uint only = Entry(0, sorted[0]);
            for (int key = 0; key < TableSize; ++key)
            {
                table[key] = only;
            }

            return;
        }

        ref byte reverse = ref Unsafe.AsRef(in ReverseBits[0]);
        uint k = 0;
        uint keyStep = ReverseBitsLowest;
        symbol = 0;
        int step = 2;
        for (int bits = 1; bits <= MaxCodeLengthCodeLength; bits++)
        {
            for (int n = count[bits]; n != 0; --n)
            {
                uint code = Entry(bits, sorted[symbol++]);
                Replicate(table + Unsafe.Add(ref reverse, (int)k), step, TableSize, code);
                k += keyStep;
            }

            step <<= 1;
            keyStep >>= 1;
        }
    }

    /// <summary>
    /// The space of the reversed keys of <see cref="BuildTable{TKeys}"/>: brotli's 8 bits, for root tables
    /// of at most 8 bits, or 16, for wider ones.
    /// </summary>
    public interface IKeys
    {
        /// <summary>The lowest bit of the reversed key, brotli's <c>BROTLI_REVERSE_BITS_LOWEST</c>.</summary>
        static abstract uint Lowest { get; }

        /// <summary>The key, bit-reversed over the width of the space.</summary>
        static abstract uint Reverse(uint key);
    }

    /// <summary>Brotli's keys of 8 bits, for root tables of at most 8 bits.</summary>
    public struct Keys8 : IKeys
    {
        public static uint Lowest => ReverseBitsLowest;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Reverse(uint key) => Unsafe.Add(ref Unsafe.AsRef(in ReverseBits[0]), (int)key);
    }

    /// <summary>Keys of 16 bits, for root tables of up to 15 bits.</summary>
    public struct Keys16 : IKeys
    {
        public static uint Lowest => 1u << 15;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Reverse(uint key)
        {
            ref byte reverse = ref Unsafe.AsRef(in ReverseBits[0]);
            return ((uint)Unsafe.Add(ref reverse, (int)(key & 0xFF)) << 8) | Unsafe.Add(ref reverse, (int)(key >> 8));
        }
    }

    /// <summary>
    /// The table of a code given by its lengths: <paramref name="symbolLists"/> holds, for each length,
    /// a list of the symbols of that length in increasing order (heads at <c>length - 16</c>, links at
    /// the symbols, empty heads 0xFFFF), and <paramref name="count"/> the number of symbols of each
    /// length, which this clears. Returns the number of entries written. Brotli's algorithm, over keys
    /// of <typeparamref name="TKeys"/>'s width.
    /// </summary>
    public static int BuildTable<TKeys>(uint* rootTable, int rootBits, ushort* symbolLists, ushort* count)
        where TKeys : struct, IKeys
    {
        int maxLength = -1;
        while (symbolLists[maxLength] == 0xFFFF)
        {
            maxLength--;
        }

        maxLength += MaxCodeLength + 1;

        uint* table = rootTable;
        int tableBits = rootBits;
        int tableSize = 1 << tableBits;
        int totalSize = tableSize;

        // The root table, at the width of the longest code if that is narrower, then copied over.
        if (tableBits > maxLength)
        {
            tableBits = maxLength;
            tableSize = 1 << tableBits;
        }

        uint key = 0;
        uint keyStep = TKeys.Lowest;
        int symbol;
        int step = 2;
        int bits = 1;
        do
        {
            symbol = bits - (MaxCodeLength + 1);
            for (int n = count[bits]; n != 0; --n)
            {
                symbol = symbolLists[symbol];
                uint code = Entry(bits, symbol);
                Replicate(table + TKeys.Reverse(key), step, tableSize, code);
                key += keyStep;
            }

            step <<= 1;
            keyStep >>= 1;
        }
        while (++bits <= tableBits);

        while (totalSize != tableSize)
        {
            Buffer.MemoryCopy(table, table + tableSize, (long)tableSize * sizeof(uint), (long)tableSize * sizeof(uint));
            tableSize <<= 1;
        }

        // The second-level tables, each pointed at by a root entry.
        keyStep = TKeys.Lowest >> (rootBits - 1);
        uint subKey = TKeys.Lowest << 1;
        uint subKeyStep = TKeys.Lowest;
        step = 2;
        for (int length = rootBits + 1; length <= maxLength; ++length)
        {
            symbol = length - (MaxCodeLength + 1);
            for (; count[length] != 0; --count[length])
            {
                if (subKey == TKeys.Lowest << 1)
                {
                    table += tableSize;
                    tableBits = NextTableBitSize(count, length, rootBits);
                    tableSize = 1 << tableBits;
                    totalSize += tableSize;
                    subKey = TKeys.Reverse(key);
                    key += keyStep;
                    rootTable[subKey] = Entry(tableBits + rootBits, (int)(table - rootTable));
                    subKey = 0;
                }

                symbol = symbolLists[symbol];
                uint code = Entry(length - rootBits, symbol);
                Replicate(table + TKeys.Reverse(subKey), step, tableSize, code);
                subKey += subKeyStep;
            }

            step <<= 1;
            subKeyStep >>= 1;
        }

        return totalSize;
    }

    /// <summary>
    /// The table of a simple code (RFC 7932, section 3.4): <paramref name="numSymbols"/> is 0 for 1
    /// symbol, 1 for 2, 2 for 3, 3 for 4 symbols of length 2 and 4 for 4 symbols of lengths 1, 2, 3, 3.
    /// The symbols may be reordered. Returns the number of entries written, the root table's.
    /// </summary>
    public static int BuildSimpleTable(uint* table, int rootBits, ushort* val, int numSymbols)
    {
        int tableSize = 1;
        int goalSize = 1 << rootBits;
        switch (numSymbols)
        {
            case 0:
                table[0] = Entry(0, val[0]);
                break;
            case 1:
                if (val[1] > val[0])
                {
                    table[0] = Entry(1, val[0]);
                    table[1] = Entry(1, val[1]);
                }
                else
                {
                    table[0] = Entry(1, val[1]);
                    table[1] = Entry(1, val[0]);
                }

                tableSize = 2;
                break;
            case 2:
                table[0] = Entry(1, val[0]);
                table[2] = Entry(1, val[0]);
                if (val[2] > val[1])
                {
                    table[1] = Entry(2, val[1]);
                    table[3] = Entry(2, val[2]);
                }
                else
                {
                    table[1] = Entry(2, val[2]);
                    table[3] = Entry(2, val[1]);
                }

                tableSize = 4;
                break;
            case 3:
                for (int i = 0; i < 3; ++i)
                {
                    for (int k = i + 1; k < 4; ++k)
                    {
                        if (val[k] < val[i])
                        {
                            (val[k], val[i]) = (val[i], val[k]);
                        }
                    }
                }

                table[0] = Entry(2, val[0]);
                table[2] = Entry(2, val[1]);
                table[1] = Entry(2, val[2]);
                table[3] = Entry(2, val[3]);
                tableSize = 4;
                break;
            case 4:
                if (val[3] < val[2])
                {
                    (val[3], val[2]) = (val[2], val[3]);
                }

                table[0] = Entry(1, val[0]);
                table[1] = Entry(2, val[1]);
                table[2] = Entry(1, val[0]);
                table[3] = Entry(3, val[2]);
                table[4] = Entry(1, val[0]);
                table[5] = Entry(2, val[1]);
                table[6] = Entry(1, val[0]);
                table[7] = Entry(3, val[3]);
                tableSize = 8;
                break;
        }

        while (tableSize != goalSize)
        {
            Buffer.MemoryCopy(table, table + tableSize, (long)tableSize * sizeof(uint), (long)tableSize * sizeof(uint));
            tableSize <<= 1;
        }

        return goalSize;
    }
}
