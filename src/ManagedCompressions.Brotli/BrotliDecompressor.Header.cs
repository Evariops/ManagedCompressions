using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedCompressions.Brotli.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// A compressed meta-block's header (RFC 7932, sections 3, 6, 7 and 9.2): the block types and their
/// codes, the distance parameters, the context modes and maps, and the three groups of prefix codes.
/// The validation is brotli's (<c>c/dec/decode.c</c>), check for check.
/// </summary>
public sealed partial class BrotliDecompressor
{
    /// <summary>The size of a block type code's table: brotli's <c>BROTLI_HUFFMAN_MAX_SIZE_258</c>.</summary>
    private const int BlockTypeTableSize = 632;

    /// <summary>The size of a block length code's table: brotli's <c>BROTLI_HUFFMAN_MAX_SIZE_26</c>.</summary>
    private const int BlockLengthTableSize = 396;

    /// <summary>The size of a context map code's table: brotli's <c>BROTLI_HUFFMAN_MAX_SIZE_272</c>.</summary>
    private const int ContextMapTableSize = 646;

    /// <summary>The largest distance alphabet outside the large window: 16 + 120 + (24 &lt;&lt; 4).</summary>
    private const int MaxDistanceAlphabet = 16 + 120 + (Prefix.MaxDistanceBits << 4);

    /// <summary>The offsets of the header's codes in <see cref="_headerTables"/>.</summary>
    private const int BlockTypeTrees = 0;
    private const int BlockLengthTrees = BlockTypeTrees + (3 * BlockTypeTableSize);
    private const int ContextMapTree = BlockLengthTrees + (3 * BlockLengthTableSize);
    private const int CodeLengthTable = ContextMapTree + ContextMapTableSize;

    /// <summary>The codes of the block types, of the block lengths, of the context maps, and the code length code.</summary>
    private readonly uint[] _headerTables = new uint[CodeLengthTable + (1 << Huffman.MaxCodeLengthCodeLength)];

    private readonly byte[] _contextModes = new byte[Prefix.MaxBlockTypes];
    private readonly byte[] _contextMap = new byte[Prefix.MaxBlockTypes << Prefix.LiteralContextBits];
    private readonly byte[] _distContextMap = new byte[Prefix.MaxBlockTypes << Prefix.DistanceContextBits];
    private readonly byte[] _distExtraBits = new byte[MaxDistanceAlphabet];
    private readonly int[] _distOffset = new int[MaxDistanceAlphabet];

    /// <summary>The scratch of a code's reading: the lists of symbols by length, heads first (<see cref="Huffman.ListHeads"/>).</summary>
    private readonly ushort[] _symbolLists = new ushort[Huffman.ListHeads + Prefix.NumCommandSymbols];

    private int _numLiteralTrees;
    private int _numDistanceTrees;
    private int _distancePostfixBits;
    private int _numDirectDistanceCodes;
    private int _distanceAlphabetSize;

    private readonly TreeGroup _literalGroup = new(WideLiteralRootBits);
    private readonly TreeGroup _commandGroup = new(CommandRootBits);
    private readonly TreeGroup _distanceGroup = new(DistanceRootBits);

    /// <summary>
    /// The width of the literal codes' root tables in a meta-block with at least
    /// <see cref="WideRootContentPerCode"/> bytes of content for each literal code. Wider than brotli's 8
    /// bits: a literal of 9 or 10 bits then takes one lookup instead of two. A meta-block with less
    /// content for its codes keeps 8 bits, as filling 1024 entries for each code would cost it more
    /// than it saves: at quality 11, JSON records of 800 bytes decode 2 % faster so, while at quality 1,
    /// where a single code decodes most of a record, they decode 4 % faster with 10 bits.
    /// </summary>
    internal const int WideLiteralRootBits = 10;

    /// <summary>The content for each literal code from which their root tables are 10 bits wide.</summary>
    internal const int WideRootContentPerCode = 1024;

    /// <summary>The width of the insert-and-copy codes' root tables.</summary>
    internal const int CommandRootBits = 8;

    /// <summary>The width of the distance codes' root tables.</summary>
    internal const int DistanceRootBits = 8;

    /// <summary>
    /// A group of prefix codes over one alphabet, built one after the other into a table that grows as
    /// streams need, never shrinks.
    /// </summary>
    private sealed class TreeGroup
    {
        public TreeGroup(int rootBits) => RootBits = rootBits;

        /// <summary>The width of each code's root table.</summary>
        public int RootBits { get; set; }

        /// <summary>The codes' tables, one after the other.</summary>
        public uint[] Codes = [];

        /// <summary>The root of each code: its offset in <see cref="Codes"/>.</summary>
        public int[] Trees = [];

        /// <summary>
        /// The largest table of a code of <paramref name="alphabetSize"/> symbols: brotli's bound for its
        /// 8-bit root tables; for wider ones, the root table plus, for each symbol, the half of the widest
        /// second-level table its code could take at most.
        /// </summary>
        public int MaxTableSize(int alphabetSize) =>
            RootBits == Huffman.RootBits
                ? Huffman.MaxTableSize(alphabetSize)
                : (1 << RootBits) + (alphabetSize << (Huffman.MaxCodeLength - 1 - RootBits));

        public void ReserveTrees(int count)
        {
            if (Trees.Length < count)
            {
                Trees = new int[Math.Max(count, 8)];
            }
        }

        /// <summary>Room for <paramref name="size"/> entries past the <paramref name="used"/> first, which are kept.</summary>
        public void Ensure(int used, int size)
        {
            if (used + size <= Codes.Length)
            {
                return;
            }

            uint[] codes = new uint[Math.Max(used + size, Codes.Length * 2)];
            Codes.AsSpan(0, used).CopyTo(codes);
            Codes = codes;
        }
    }

    /// <summary>The header of a compressed meta-block, up to its first command.</summary>
    private BrotliError DecodeCompressedHeader(ReadOnlySpan<byte> src)
    {
        for (int i = 0; i < 3; i++)
        {
            _s.BlockLength[i] = Prefix.BlockSizeCap;
            _s.BlockTypeRb[(i * 2) + 0] = 1;
            _s.BlockTypeRb[(i * 2) + 1] = 0;
            int types = DecodeVarLenUint8(src) + 1;
            _s.NumBlockTypes[i] = types;
            if (types < 2)
            {
                continue;
            }

            BrotliError error = ReadHuffmanCode(src, types + 2, types + 2, _headerTables.AsSpan(BlockTypeTrees + (i * BlockTypeTableSize), BlockTypeTableSize), out _);
            if (error != BrotliError.None)
            {
                return error;
            }

            error = ReadHuffmanCode(src, Prefix.NumBlockLengthSymbols, Prefix.NumBlockLengthSymbols, _headerTables.AsSpan(BlockLengthTrees + (i * BlockLengthTableSize), BlockLengthTableSize), out _);
            if (error != BrotliError.None)
            {
                return error;
            }

            _s.BlockLength[i] = ReadBlockLength(src, BlockLengthTrees + (i * BlockLengthTableSize));
        }

        uint bits = _br.Read(src, 6);
        _distancePostfixBits = (int)(bits & 3);
        _numDirectDistanceCodes = (int)(bits >> 2) << _distancePostfixBits;

        for (int i = 0; i < _s.NumBlockTypes[0]; i++)
        {
            _contextModes[i] = (byte)_br.Read(src, 2);
        }

        BrotliError result = DecodeContextMap(src, _s.NumBlockTypes[0] << Prefix.LiteralContextBits, out _numLiteralTrees, _contextMap);
        if (result != BrotliError.None)
        {
            return result;
        }

        DetectTrivialLiteralBlockTypes();
        _literalGroup.RootBits = _metaBlockRemaining / WideRootContentPerCode >= _numLiteralTrees ? WideLiteralRootBits : Huffman.RootBits;

        _distanceAlphabetSize = Prefix.DistanceAlphabetSize(_distancePostfixBits, _numDirectDistanceCodes, Prefix.MaxDistanceBits);
        result = DecodeContextMap(src, _s.NumBlockTypes[2] << Prefix.DistanceContextBits, out _numDistanceTrees, _distContextMap);
        if (result != BrotliError.None)
        {
            return result;
        }

        result = DecodeTreeGroup(src, _literalGroup, Prefix.NumLiteralSymbols, Prefix.NumLiteralSymbols, _numLiteralTrees);
        if (result != BrotliError.None)
        {
            return result;
        }

        result = DecodeTreeGroup(src, _commandGroup, Prefix.NumCommandSymbols, Prefix.NumCommandSymbols, _s.NumBlockTypes[1]);
        if (result != BrotliError.None)
        {
            return result;
        }

        result = DecodeTreeGroup(src, _distanceGroup, _distanceAlphabetSize, _distanceAlphabetSize, _numDistanceTrees);
        if (result != BrotliError.None)
        {
            return result;
        }

        CalculateDistanceLut();
        return BrotliError.None;
    }

    /// <summary>A number of 0 to 255 in 1 to 11 bits (RFC 7932, section 9.2).</summary>
    private int DecodeVarLenUint8(ReadOnlySpan<byte> src)
    {
        if (_br.Read(src, 1) == 0)
        {
            return 0;
        }

        int n = (int)_br.Read(src, 3);
        if (n == 0)
        {
            return 1;
        }

        return (1 << n) + (int)_br.Read(src, n);
    }

    /// <summary>A block length, through the code whose table is at <paramref name="tree"/> in <see cref="_headerTables"/>.</summary>
    private int ReadBlockLength(ReadOnlySpan<byte> src, int tree)
    {
        int code = _br.ReadSymbol(src, _headerTables, tree);
        return Prefix.BlockLengthOffset[code] + (int)_br.Read(src, Prefix.BlockLengthBits[code]);
    }

    private BrotliError DecodeTreeGroup(ReadOnlySpan<byte> src, TreeGroup group, int alphabetSizeMax, int alphabetSizeLimit, int count)
    {
        int maxTableSize = group.MaxTableSize(alphabetSizeLimit);
        group.ReserveTrees(count);
        int used = 0;
        for (int i = 0; i < count; i++)
        {
            group.Ensure(used, maxTableSize);
            BrotliError error = ReadHuffmanCode(src, alphabetSizeMax, alphabetSizeLimit, group.Codes.AsSpan(used, maxTableSize), out int tableSize, group.RootBits);
            if (error != BrotliError.None)
            {
                return error;
            }

            group.Trees[i] = used;
            used += tableSize;
        }

        return BrotliError.None;
    }

    /// <summary>
    /// A prefix code (RFC 7932, section 3.4 and 3.5), into <paramref name="table"/>: a simple code of 1 to
    /// 4 symbols, or the lengths of every symbol through the code length code.
    /// </summary>
    private BrotliError ReadHuffmanCode(ReadOnlySpan<byte> src, int alphabetSizeMax, int alphabetSizeLimit, Span<uint> table, out int tableSize, int rootBits = Huffman.RootBits)
    {
        tableSize = 0;
        int kind = (int)_br.Read(src, 2);
        if (kind == 1)
        {
            return ReadSimpleHuffmanCode(src, alphabetSizeMax, alphabetSizeLimit, table, out tableSize, rootBits);
        }

        // The code length code: kind is how many of its first lengths are skipped (0, 2 or 3).
        Span<byte> codeLengthCodeLengths = stackalloc byte[Prefix.NumCodeLengthCodes];
        Span<ushort> histogram = stackalloc ushort[Huffman.MaxCodeLength + 1];
        codeLengthCodeLengths.Clear();
        histogram.Clear();
        uint space = 32;
        int numCodes = 0;
        ReadOnlySpan<byte> order = Prefix.CodeLengthCodeOrder;
        ReadOnlySpan<byte> prefixLength = Prefix.CodeLengthPrefixLength;
        ReadOnlySpan<byte> prefixValue = Prefix.CodeLengthPrefixValue;
        for (int i = kind; i < Prefix.NumCodeLengthCodes; i++)
        {
            if (_br.Count < 4)
            {
                _br.Refill(src);
            }

            int ix = (int)(_br.Bits & 0xF);
            int v = prefixValue[ix];
            int drop = prefixLength[ix];
            _br.Bits >>= drop;
            _br.Count -= drop;
            codeLengthCodeLengths[order[i]] = (byte)v;
            if (v != 0)
            {
                space -= 32u >> v;
                numCodes++;
                histogram[v]++;
                if (space - 1u >= 32u)
                {
                    // Space is 0, or has wrapped around.
                    break;
                }
            }
        }

        if (!(numCodes == 1 || space == 0))
        {
            return Fail(BrotliError.CodeLengthSpace);
        }

        Span<uint> codeLengthTable = _headerTables.AsSpan(CodeLengthTable, 1 << Huffman.MaxCodeLengthCodeLength);
        Huffman.BuildCodeLengthsTable(codeLengthTable, codeLengthCodeLengths, histogram);
        histogram.Clear();

        // The lists of symbols of each length, through their heads, which come first.
        Span<ushort> symbolLists = _symbolLists;
        Span<int> nextSymbol = stackalloc int[Huffman.MaxCodeLength + 1];
        for (int i = 0; i <= Huffman.MaxCodeLength; i++)
        {
            nextSymbol[i] = i - Huffman.ListHeads;
            symbolLists[i] = 0xFFFF;
        }

        int symbol = 0;
        int prevCodeLength = Prefix.InitialRepeatedCodeLength;
        uint repeat = 0;
        int repeatCodeLength = 0;
        space = 32768;
        while (symbol < alphabetSizeLimit && space > 0)
        {
            if (_br.Count < 8)
            {
                _br.Refill(src);
            }

            uint entry = codeLengthTable[(int)(_br.Bits & 31)];
            int entryBits = (int)(entry & 0xFF);
            _br.Bits >>= entryBits;
            _br.Count -= entryBits;
            int codeLength = (int)(entry >> 16);
            if (codeLength < Prefix.RepeatPreviousCodeLength)
            {
                // A length of 0 to 15.
                repeat = 0;
                if (codeLength != 0)
                {
                    symbolLists[Huffman.ListHeads + nextSymbol[codeLength]] = (ushort)symbol;
                    nextSymbol[codeLength] = symbol;
                    prevCodeLength = codeLength;
                    space -= 32768u >> codeLength;
                    histogram[codeLength]++;
                }

                symbol++;
                continue;
            }

            // A repetition of the previous length (16, 2 extra bits) or of zeros (17, 3 extra bits).
            int extraBits = codeLength == Prefix.RepeatPreviousCodeLength ? 2 : 3;
            uint repeatDelta = (uint)(_br.Bits & ((1UL << extraBits) - 1));
            _br.Bits >>= extraBits;
            _br.Count -= extraBits;
            int newLength = codeLength == Prefix.RepeatPreviousCodeLength ? prevCodeLength : 0;
            if (repeatCodeLength != newLength)
            {
                repeat = 0;
                repeatCodeLength = newLength;
            }

            uint oldRepeat = repeat;
            if (repeat > 0)
            {
                repeat -= 2;
                repeat <<= extraBits;
            }

            repeat += repeatDelta + 3u;
            repeatDelta = repeat - oldRepeat;
            if (symbol + repeatDelta > (uint)alphabetSizeLimit)
            {
                symbol = alphabetSizeLimit;
                space = 0xFFFFF;
                break;
            }

            if (repeatCodeLength != 0)
            {
                int lastSymbol = symbol + (int)repeatDelta;
                int nextAt = nextSymbol[repeatCodeLength];
                do
                {
                    symbolLists[Huffman.ListHeads + nextAt] = (ushort)symbol;
                    nextAt = symbol;
                }
                while (++symbol != lastSymbol);

                nextSymbol[repeatCodeLength] = nextAt;
                space -= repeatDelta << (15 - repeatCodeLength);
                histogram[repeatCodeLength] = (ushort)(histogram[repeatCodeLength] + repeatDelta);
            }
            else
            {
                symbol += (int)repeatDelta;
            }
        }

        if (space != 0)
        {
            return Fail(BrotliError.HuffmanSpace);
        }

        tableSize = Huffman.BuildTable(table, rootBits, symbolLists, histogram);
        return BrotliError.None;
    }

    private BrotliError ReadSimpleHuffmanCode(ReadOnlySpan<byte> src, int alphabetSizeMax, int alphabetSizeLimit, Span<uint> table, out int tableSize, int rootBits)
    {
        tableSize = 0;
        int numSymbols = (int)_br.Read(src, 2);
        int maxBits = 32 - BitOperations.LeadingZeroCount((uint)(alphabetSizeMax - 1));
        Span<ushort> symbols = stackalloc ushort[4];
        for (int i = 0; i <= numSymbols; i++)
        {
            uint v = _br.Read(src, maxBits);
            if (v >= (uint)alphabetSizeLimit)
            {
                return Fail(BrotliError.SimpleHuffmanAlphabet);
            }

            symbols[i] = (ushort)v;
        }

        for (int i = 0; i < numSymbols; i++)
        {
            for (int k = i + 1; k <= numSymbols; k++)
            {
                if (symbols[i] == symbols[k])
                {
                    return Fail(BrotliError.SimpleHuffmanSame);
                }
            }
        }

        if (numSymbols == 3)
        {
            numSymbols += (int)_br.Read(src, 1);
        }

        tableSize = Huffman.BuildSimpleTable(table, rootBits, symbols, numSymbols);
        return BrotliError.None;
    }

    /// <summary>A context map (RFC 7932, section 7.3): its number of codes, then its runs and values.</summary>
    private BrotliError DecodeContextMap(ReadOnlySpan<byte> src, int contextMapSize, out int numTrees, Span<byte> contextMap)
    {
        contextMap = contextMap.Slice(0, contextMapSize);
        numTrees = DecodeVarLenUint8(src) + 1;
        if (numTrees <= 1)
        {
            contextMap.Clear();
            return BrotliError.None;
        }

        int maxRunLengthPrefix = 0;
        if (_br.Count < 5)
        {
            _br.Refill(src);
        }

        uint bits = (uint)(_br.Bits & 31);
        if ((bits & 1) != 0)
        {
            // Runs of zeros.
            maxRunLengthPrefix = (int)(bits >> 1) + 1;
            _br.Bits >>= 5;
            _br.Count -= 5;
        }
        else
        {
            _br.Bits >>= 1;
            _br.Count -= 1;
        }

        int alphabetSize = numTrees + maxRunLengthPrefix;
        BrotliError error = ReadHuffmanCode(src, alphabetSize, alphabetSize, _headerTables.AsSpan(ContextMapTree, ContextMapTableSize), out _);
        if (error != BrotliError.None)
        {
            return error;
        }

        int index = 0;
        while (index < contextMapSize)
        {
            int code = _br.ReadSymbol(src, _headerTables, ContextMapTree);
            if (code == 0)
            {
                contextMap[index++] = 0;
                continue;
            }

            if (code > maxRunLengthPrefix)
            {
                contextMap[index++] = (byte)(code - maxRunLengthPrefix);
                continue;
            }

            int repeat = (int)_br.Read(src, code) + (1 << code);
            if (index + repeat > contextMapSize)
            {
                return Fail(BrotliError.ContextMapRepeat);
            }

            contextMap.Slice(index, repeat).Clear();
            index += repeat;
        }

        if (_br.Read(src, 1) != 0)
        {
            InverseMoveToFront(contextMap);
        }

        return BrotliError.None;
    }

    /// <summary>The inverse of the move-to-front transform (RFC 7932, section 7.3).</summary>
    private static void InverseMoveToFront(Span<byte> values)
    {
        Span<byte> mtf = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
        {
            mtf[i] = (byte)i;
        }

        for (int i = 0; i < values.Length; i++)
        {
            int index = values[i];
            byte value = mtf[index];
            values[i] = value;
            for (; index > 0; index--)
            {
                mtf[index] = mtf[index - 1];
            }

            mtf[0] = value;
        }
    }

    /// <summary>The literal block types whose 64 contexts all use the same code, which then needs no context.</summary>
    private void DetectTrivialLiteralBlockTypes()
    {
        for (int i = 0; i < 8; i++)
        {
            _s.TrivialLiteralContexts[i] = 0;
        }

        for (int i = 0; i < _s.NumBlockTypes[0]; i++)
        {
            ReadOnlySpan<byte> map = _contextMap.AsSpan(i << Prefix.LiteralContextBits, 1 << Prefix.LiteralContextBits);
            byte sample = map[0];
            int error = 0;
            for (int j = 0; j < map.Length; j++)
            {
                error |= map[j] ^ sample;
            }

            if (error == 0)
            {
                _s.TrivialLiteralContexts[i >> 5] |= 1u << (i & 31);
            }
        }
    }

    /// <summary>The extra bits and offset of each distance code, for this meta-block's NPOSTFIX and NDIRECT.</summary>
    private void CalculateDistanceLut()
    {
        int npostfix = _distancePostfixBits;
        int ndirect = _numDirectDistanceCodes;
        int postfix = 1 << npostfix;
        int bits = 1;
        int half = 0;
        int i = Prefix.NumDistanceShortCodes;
        for (int j = 0; j < ndirect; ++j)
        {
            _distExtraBits[i] = 0;
            _distOffset[i] = j + 1;
            ++i;
        }

        while (i < _distanceAlphabetSize)
        {
            int baseDistance = ndirect + ((((2 + half) << bits) - 4) << npostfix) + 1;
            for (int j = 0; j < postfix; ++j)
            {
                _distExtraBits[i] = (byte)bits;
                _distOffset[i] = baseDistance + j;
                ++i;
            }

            bits += half;
            half ^= 1;
        }
    }
}
