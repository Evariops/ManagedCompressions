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
public sealed unsafe partial class BrotliDecompressor
{
    /// <summary>The size of a block type code's table: brotli's <c>BROTLI_HUFFMAN_MAX_SIZE_258</c>.</summary>
    private const int BlockTypeTableSize = 632;

    /// <summary>The size of a block length code's table: brotli's <c>BROTLI_HUFFMAN_MAX_SIZE_26</c>.</summary>
    private const int BlockLengthTableSize = 396;

    /// <summary>The size of a context map code's table: brotli's <c>BROTLI_HUFFMAN_MAX_SIZE_272</c>.</summary>
    private const int ContextMapTableSize = 646;

    /// <summary>The largest distance alphabet outside the large window: 16 + 120 + (24 &lt;&lt; 4).</summary>
    private const int MaxDistanceAlphabet = 16 + 120 + (Prefix.MaxDistanceBits << 4);

    private uint* _blockTypeTrees;
    private uint* _blockLengthTrees;
    private uint* _contextMapTree;
    private byte* _contextModes;
    private byte* _contextMap;
    private byte* _distContextMap;
    private byte* _distExtraBits;
    private int* _distOffset;

    /// <summary>The scratch of a code's reading: the lists of symbols by length, heads 16 entries before.</summary>
    private ushort* _symbolLists;

    private byte[] _headerBytes = [];
    private uint[] _headerTables = [];
    private int[] _distOffsetArray = [];
    private ushort[] _symbolListsArray = [];


    private int _numLiteralTrees;
    private int _numDistanceTrees;
    private int _distancePostfixBits;
    private int _numDirectDistanceCodes;
    private int _distanceAlphabetSize;

    private readonly TreeGroup _literalGroup = new(Prefix.NumLiteralSymbols);
    private readonly TreeGroup _commandGroup = new(Prefix.NumCommandSymbols);
    private readonly TreeGroup _distanceGroup = new(MaxDistanceAlphabet);

    /// <summary>A group of prefix codes over one alphabet, in a table that grows as streams need, never shrinks.</summary>
    private sealed class TreeGroup
    {
        private uint[] _codes = [];
        private nint[] _trees = [];

        public TreeGroup(int alphabetSize) => AlphabetSize = alphabetSize;

        public int AlphabetSize { get; }

        public uint* Codes;

        /// <summary>The root of each code: a pointer into <see cref="Codes"/>.</summary>
        public nint* Trees;

        public void Reserve(int count, int maxTableSize)
        {
            int size = checked(count * maxTableSize);
            if (_codes.Length < size)
            {
                _codes = Pinned<uint>(size);
                Codes = Pointer(_codes);
            }

            if (_trees.Length < count)
            {
                _trees = Pinned<nint>(Math.Max(count, 8));
                Trees = Pointer(_trees);
            }
        }
    }

    private void AllocateHeaderTables()
    {
        _headerTables = Pinned<uint>((3 * BlockTypeTableSize) + (3 * BlockLengthTableSize) + ContextMapTableSize + 32);
        uint* tables = Pointer(_headerTables);
        _blockTypeTrees = tables;
        _blockLengthTrees = tables + (3 * BlockTypeTableSize);
        _contextMapTree = _blockLengthTrees + (3 * BlockLengthTableSize);

        const int ContextMapSize = Prefix.MaxBlockTypes << Prefix.LiteralContextBits;
        const int DistContextMapSize = Prefix.MaxBlockTypes << Prefix.DistanceContextBits;
        _headerBytes = Pinned<byte>(Prefix.MaxBlockTypes + ContextMapSize + DistContextMapSize + MaxDistanceAlphabet);
        byte* bytes = Pointer(_headerBytes);
        _contextModes = bytes;
        _contextMap = bytes + Prefix.MaxBlockTypes;
        _distContextMap = _contextMap + ContextMapSize;
        _distExtraBits = _distContextMap + DistContextMapSize;

        _distOffsetArray = Pinned<int>(MaxDistanceAlphabet);
        _distOffset = Pointer(_distOffsetArray);

        _symbolListsArray = Pinned<ushort>(Huffman.MaxCodeLength + 1 + Prefix.NumCommandSymbols);
        _symbolLists = Pointer(_symbolListsArray) + Huffman.MaxCodeLength + 1;
    }

    /// <summary>The header of a compressed meta-block, up to its first command.</summary>
    private BrotliError DecodeCompressedHeader()
    {
        for (int i = 0; i < 3; i++)
        {
            _s.BlockLength[i] = Prefix.BlockSizeCap;
            _s.BlockTypeRb[(i * 2) + 0] = 1;
            _s.BlockTypeRb[(i * 2) + 1] = 0;
            int types = DecodeVarLenUint8() + 1;
            _s.NumBlockTypes[i] = types;
            if (types < 2)
            {
                continue;
            }

            BrotliError error = ReadHuffmanCode(types + 2, types + 2, _blockTypeTrees + (i * BlockTypeTableSize), out _);
            if (error != BrotliError.None)
            {
                return error;
            }

            error = ReadHuffmanCode(Prefix.NumBlockLengthSymbols, Prefix.NumBlockLengthSymbols, _blockLengthTrees + (i * BlockLengthTableSize), out _);
            if (error != BrotliError.None)
            {
                return error;
            }

            _s.BlockLength[i] = ReadBlockLength(_blockLengthTrees + (i * BlockLengthTableSize));
        }

        uint bits = _br.Read(6);
        _distancePostfixBits = (int)(bits & 3);
        _numDirectDistanceCodes = (int)(bits >> 2) << _distancePostfixBits;

        for (int i = 0; i < _s.NumBlockTypes[0]; i++)
        {
            _contextModes[i] = (byte)_br.Read(2);
        }

        BrotliError result = DecodeContextMap(_s.NumBlockTypes[0] << Prefix.LiteralContextBits, out _numLiteralTrees, _contextMap);
        if (result != BrotliError.None)
        {
            return result;
        }

        DetectTrivialLiteralBlockTypes();

        _distanceAlphabetSize = Prefix.DistanceAlphabetSize(_distancePostfixBits, _numDirectDistanceCodes, Prefix.MaxDistanceBits);
        result = DecodeContextMap(_s.NumBlockTypes[2] << Prefix.DistanceContextBits, out _numDistanceTrees, _distContextMap);
        if (result != BrotliError.None)
        {
            return result;
        }

        result = DecodeTreeGroup(_literalGroup, Prefix.NumLiteralSymbols, Prefix.NumLiteralSymbols, _numLiteralTrees);
        if (result != BrotliError.None)
        {
            return result;
        }

        result = DecodeTreeGroup(_commandGroup, Prefix.NumCommandSymbols, Prefix.NumCommandSymbols, _s.NumBlockTypes[1]);
        if (result != BrotliError.None)
        {
            return result;
        }

        result = DecodeTreeGroup(_distanceGroup, _distanceAlphabetSize, _distanceAlphabetSize, _numDistanceTrees);
        if (result != BrotliError.None)
        {
            return result;
        }

        CalculateDistanceLut();
        return BrotliError.None;
    }

    /// <summary>A number of 0 to 255 in 1 to 11 bits (RFC 7932, section 9.2).</summary>
    private int DecodeVarLenUint8()
    {
        if (_br.Read(1) == 0)
        {
            return 0;
        }

        int n = (int)_br.Read(3);
        if (n == 0)
        {
            return 1;
        }

        return (1 << n) + (int)_br.Read(n);
    }

    private int ReadBlockLength(uint* table)
    {
        int code = _br.ReadSymbol(table);
        return Prefix.BlockLengthOffset[code] + (int)_br.Read(Prefix.BlockLengthBits[code]);
    }

    private BrotliError DecodeTreeGroup(TreeGroup group, int alphabetSizeMax, int alphabetSizeLimit, int count)
    {
        int maxTableSize = Huffman.MaxTableSize(alphabetSizeLimit);
        group.Reserve(count, maxTableSize);
        uint* next = group.Codes;
        for (int i = 0; i < count; i++)
        {
            BrotliError error = ReadHuffmanCode(alphabetSizeMax, alphabetSizeLimit, next, out int tableSize);
            if (error != BrotliError.None)
            {
                return error;
            }

            group.Trees[i] = (nint)next;
            next += tableSize;
        }

        return BrotliError.None;
    }

    /// <summary>
    /// A prefix code (RFC 7932, section 3.4 and 3.5), into <paramref name="table"/>: a simple code of 1 to
    /// 4 symbols, or the lengths of every symbol through the code length code.
    /// </summary>
    private BrotliError ReadHuffmanCode(int alphabetSizeMax, int alphabetSizeLimit, uint* table, out int tableSize)
    {
        tableSize = 0;
        int kind = (int)_br.Read(2);
        if (kind == 1)
        {
            return ReadSimpleHuffmanCode(alphabetSizeMax, alphabetSizeLimit, table, out tableSize);
        }

        // The code length code: kind is how many of its first lengths are skipped (0, 2 or 3).
        byte* codeLengthCodeLengths = stackalloc byte[Prefix.NumCodeLengthCodes];
        ushort* histogram = stackalloc ushort[Huffman.MaxCodeLength + 1];
        new Span<byte>(codeLengthCodeLengths, Prefix.NumCodeLengthCodes).Clear();
        new Span<ushort>(histogram, Huffman.MaxCodeLength + 1).Clear();
        uint space = 32;
        int numCodes = 0;
        ref byte order = ref MemoryMarshal.GetReference(Prefix.CodeLengthCodeOrder);
        ref byte prefixLength = ref MemoryMarshal.GetReference(Prefix.CodeLengthPrefixLength);
        ref byte prefixValue = ref MemoryMarshal.GetReference(Prefix.CodeLengthPrefixValue);
        for (int i = kind; i < Prefix.NumCodeLengthCodes; i++)
        {
            if (_br.Count < 4)
            {
                _br.Refill();
            }

            int ix = (int)(_br.Bits & 0xF);
            int v = Unsafe.Add(ref prefixValue, ix);
            int drop = Unsafe.Add(ref prefixLength, ix);
            _br.Bits >>= drop;
            _br.Count -= drop;
            codeLengthCodeLengths[Unsafe.Add(ref order, i)] = (byte)v;
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

        uint* codeLengthTable = _contextMapTree + ContextMapTableSize;
        Huffman.BuildCodeLengthsTable(codeLengthTable, codeLengthCodeLengths, histogram);
        new Span<ushort>(histogram, Huffman.MaxCodeLength + 1).Clear();

        // The lists of symbols of each length, through their heads at length - 16.
        ushort* symbolLists = _symbolLists;
        int* nextSymbol = stackalloc int[Huffman.MaxCodeLength + 1];
        for (int i = 0; i <= Huffman.MaxCodeLength; i++)
        {
            nextSymbol[i] = i - (Huffman.MaxCodeLength + 1);
            symbolLists[nextSymbol[i]] = 0xFFFF;
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
                _br.Refill();
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
                    symbolLists[nextSymbol[codeLength]] = (ushort)symbol;
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
                    symbolLists[nextAt] = (ushort)symbol;
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

        tableSize = Huffman.BuildTable(table, Huffman.RootBits, symbolLists, histogram);
        return BrotliError.None;
    }

    private BrotliError ReadSimpleHuffmanCode(int alphabetSizeMax, int alphabetSizeLimit, uint* table, out int tableSize)
    {
        tableSize = 0;
        int numSymbols = (int)_br.Read(2);
        int maxBits = 32 - BitOperations.LeadingZeroCount((uint)(alphabetSizeMax - 1));
        ushort* symbols = stackalloc ushort[4];
        for (int i = 0; i <= numSymbols; i++)
        {
            uint v = _br.Read(maxBits);
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
            numSymbols += (int)_br.Read(1);
        }

        tableSize = Huffman.BuildSimpleTable(table, Huffman.RootBits, symbols, numSymbols);
        return BrotliError.None;
    }

    /// <summary>A context map (RFC 7932, section 7.3): its number of codes, then its runs and values.</summary>
    private BrotliError DecodeContextMap(int contextMapSize, out int numTrees, byte* contextMap)
    {
        numTrees = DecodeVarLenUint8() + 1;
        if (numTrees <= 1)
        {
            new Span<byte>(contextMap, contextMapSize).Clear();
            return BrotliError.None;
        }

        int maxRunLengthPrefix = 0;
        if (_br.Count < 5)
        {
            _br.Refill();
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
        BrotliError error = ReadHuffmanCode(alphabetSize, alphabetSize, _contextMapTree, out _);
        if (error != BrotliError.None)
        {
            return error;
        }

        int index = 0;
        while (index < contextMapSize)
        {
            int code = _br.ReadSymbol(_contextMapTree);
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

            int repeat = (int)_br.Read(code) + (1 << code);
            if (index + repeat > contextMapSize)
            {
                return Fail(BrotliError.ContextMapRepeat);
            }

            new Span<byte>(contextMap + index, repeat).Clear();
            index += repeat;
        }

        if (_br.Read(1) != 0)
        {
            InverseMoveToFront(contextMap, contextMapSize);
        }

        return BrotliError.None;
    }

    /// <summary>The inverse of the move-to-front transform (RFC 7932, section 7.3).</summary>
    private static void InverseMoveToFront(byte* values, int length)
    {
        byte* mtf = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
        {
            mtf[i] = (byte)i;
        }

        for (int i = 0; i < length; i++)
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
            byte* map = _contextMap + (i << Prefix.LiteralContextBits);
            byte sample = map[0];
            int error = 0;
            for (int j = 0; j < 1 << Prefix.LiteralContextBits; j++)
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
