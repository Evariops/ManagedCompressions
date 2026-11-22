using System;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// The compressed meta-blocks of qualities 2 to 9 (<c>c/enc/brotli_bit_stream.c</c>): with static codes
/// and fast codes at quality 2, a code a category at quality 3, and block splits and context maps from
/// quality 4.
/// </summary>
internal static unsafe partial class BitStream
{
    /// <summary>brotli's <c>MAX_HUFFMAN_TREE_SIZE</c>: the nodes of a tree over the commands.</summary>
    public const int MaxHuffmanTreeSize = (2 * Prefix.NumCommandSymbols) + 1;

    /// <summary>brotli's <c>MAX_SIMPLE_DISTANCE_ALPHABET_SIZE</c>, the distances of NPOSTFIX 0 and NDIRECT 0 in the large window.</summary>
    public const int MaxSimpleDistanceAlphabetSize = 140;

    /// <summary>brotli's <c>BROTLI_MAX_BLOCK_TYPE_SYMBOLS</c>.</summary>
    private const int MaxBlockTypeSymbols = Prefix.MaxBlockTypes + 2;

    /// <summary>brotli's <c>BROTLI_MAX_CONTEXT_MAP_SYMBOLS</c>.</summary>
    private const int MaxContextMapSymbols = Prefix.MaxBlockTypes + 16;

    private const int SymbolBits = 9;

    /// <summary>brotli's <c>StoreVarLenUint8</c>: 0 to 255.</summary>
    private static void StoreVarLenUint8(nuint n, ref int storageIx, byte* storage)
    {
        if (n == 0)
        {
            BitWriter.Write(1, 0, ref storageIx, storage);
        }
        else
        {
            int nbits = FastLog.Log2FloorNonZero(n);
            BitWriter.Write(1, 1, ref storageIx, storage);
            BitWriter.Write(3, (ulong)nbits, ref storageIx, storage);
            BitWriter.Write(nbits, n - ((nuint)1 << nbits), ref storageIx, storage);
        }
    }

    /// <summary>brotli's <c>StoreCompressedMetaBlockHeader</c>.</summary>
    public static void StoreCompressedMetaBlockHeader(bool isFinalBlock, int length, ref int storageIx, byte* storage)
    {
        BitWriter.Write(1, isFinalBlock ? 1u : 0u, ref storageIx, storage);
        if (isFinalBlock)
        {
            BitWriter.Write(1, 0, ref storageIx, storage);
        }

        EncodeMlen(length, out ulong lenBits, out int numLenBits, out ulong nibblesBits);
        BitWriter.Write(2, nibblesBits, ref storageIx, storage);
        BitWriter.Write(numLenBits, lenBits, ref storageIx, storage);
        if (!isFinalBlock)
        {
            BitWriter.Write(1, 0, ref storageIx, storage);
        }
    }

    /// <summary>brotli's <c>BlockLengthPrefixCode</c>.</summary>
    private static int BlockLengthPrefixCode(uint len)
    {
        int code = len >= 177 ? (len >= 753 ? 20 : 14) : (len >= 41 ? 7 : 0);
        ReadOnlySpan<int> offsets = Prefix.BlockLengthOffset;
        while (code < Prefix.NumBlockLengthSymbols - 1 && len >= (uint)offsets[code + 1])
        {
            ++code;
        }

        return code;
    }

    /// <summary>brotli's <c>StoreCommandExtra</c>: the extra bits of the insert and copy lengths.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreCommandExtra(Command* cmd, ref int storageIx, byte* storage)
    {
        uint copyLenCode = Commands.CopyLenCode(cmd);
        uint insCode = Commands.GetInsertLengthCode(cmd->InsertLen);
        uint copyCode = Commands.GetCopyLengthCode(copyLenCode);
        uint insNumExtra = Commands.GetInsertExtra(insCode);
        ulong insExtraVal = cmd->InsertLen - Commands.GetInsertBase(insCode);
        ulong copyExtraVal = copyLenCode - Commands.GetCopyBase(copyCode);
        ulong bits = (copyExtraVal << (int)insNumExtra) | insExtraVal;
        BitWriter.Write((int)(insNumExtra + Commands.GetCopyExtra(copyCode)), bits, ref storageIx, storage);
    }

    /// <summary>
    /// brotli's <c>BuildAndStoreHuffmanTree</c>: a code of at most 15 bits, stored as a simple code for
    /// up to four symbols. <paramref name="tree"/> holds <see cref="MaxHuffmanTreeSize"/> nodes.
    /// </summary>
    public static void BuildAndStoreHuffmanTree(uint* histogram, int histogramLength, int alphabetSize, HuffmanTree* tree, byte* depth, ushort* bits, ref int storageIx, byte* storage)
    {
        int count = 0;
        nuint* s4 = stackalloc nuint[4];
        s4[0] = s4[1] = s4[2] = s4[3] = 0;
        for (int i = 0; i < histogramLength; i++)
        {
            if (histogram[i] != 0)
            {
                if (count < 4)
                {
                    s4[count] = (nuint)i;
                }
                else if (count > 4)
                {
                    break;
                }

                count++;
            }
        }

        int maxBits = 0;
        for (int counter = alphabetSize - 1; counter != 0; counter >>= 1)
        {
            ++maxBits;
        }

        if (count <= 1)
        {
            BitWriter.Write(4, 1, ref storageIx, storage);
            BitWriter.Write(maxBits, s4[0], ref storageIx, storage);
            depth[s4[0]] = 0;
            bits[s4[0]] = 0;
            return;
        }

        new Span<byte>(depth, histogramLength).Clear();
        EntropyEncode.CreateHuffmanTree(histogram, histogramLength, 15, tree, depth);
        EntropyEncode.ConvertBitDepthsToSymbols(depth, histogramLength, bits);
        if (count <= 4)
        {
            StoreSimpleHuffmanTree(depth, s4, count, maxBits, ref storageIx, storage);
        }
        else
        {
            StoreHuffmanTree(depth, histogramLength, tree, ref storageIx, storage);
        }
    }

    /// <summary>brotli's <c>MoveToFrontTransform</c>.</summary>
    private static void MoveToFrontTransform(uint* vIn, nuint vSize, uint* vOut)
    {
        if (vSize == 0)
        {
            return;
        }

        uint maxValue = vIn[0];
        for (nuint i = 1; i < vSize; ++i)
        {
            if (vIn[i] > maxValue)
            {
                maxValue = vIn[i];
            }
        }

        byte* mtf = stackalloc byte[256];
        for (uint i = 0; i <= maxValue; ++i)
        {
            mtf[i] = (byte)i;
        }

        nuint mtfSize = maxValue + 1;
        for (nuint i = 0; i < vSize; ++i)
        {
            byte value = (byte)vIn[i];
            nuint index = 0;
            while (index < mtfSize && mtf[index] != value)
            {
                ++index;
            }

            vOut[i] = (uint)index;
            for (nuint k = index; k != 0; --k)
            {
                mtf[k] = mtf[k - 1];
            }

            mtf[0] = value;
        }
    }

    /// <summary>
    /// brotli's <c>RunLengthCodeZeros</c>: the runs of zeros of <paramref name="v"/> as a prefix code of
    /// their length, its extra bits above bit 9, and the other values shifted past the run codes.
    /// </summary>
    private static void RunLengthCodeZeros(nuint inSize, uint* v, out nuint outSize, ref uint maxRunLengthPrefix)
    {
        uint maxReps = 0;
        for (nuint i = 0; i < inSize;)
        {
            uint reps = 0;
            for (; i < inSize && v[i] != 0; ++i)
            {
            }

            for (; i < inSize && v[i] == 0; ++i)
            {
                ++reps;
            }

            maxReps = Math.Max(reps, maxReps);
        }

        uint maxPrefix = maxReps > 0 ? (uint)FastLog.Log2FloorNonZero(maxReps) : 0;
        maxPrefix = Math.Min(maxPrefix, maxRunLengthPrefix);
        maxRunLengthPrefix = maxPrefix;
        outSize = 0;
        for (nuint i = 0; i < inSize;)
        {
            if (v[i] != 0)
            {
                v[outSize] = v[i] + maxRunLengthPrefix;
                ++i;
                ++outSize;
            }
            else
            {
                uint reps = 1;
                for (nuint k = i + 1; k < inSize && v[k] == 0; ++k)
                {
                    ++reps;
                }

                i += reps;
                while (reps != 0)
                {
                    if (reps < (2u << (int)maxPrefix))
                    {
                        uint runLengthPrefix = (uint)FastLog.Log2FloorNonZero(reps);
                        uint extraBits = reps - (1u << (int)runLengthPrefix);
                        v[outSize] = runLengthPrefix + (extraBits << 9);
                        ++outSize;
                        break;
                    }
                    else
                    {
                        uint extraBits = (1u << (int)maxPrefix) - 1u;
                        v[outSize] = maxPrefix + (extraBits << 9);
                        reps -= (2u << (int)maxPrefix) - 1u;
                        ++outSize;
                    }
                }
            }
        }
    }

    /// <summary>brotli's <c>EncodeContextMap</c>: move-to-front, runs of zeros, and a prefix code.</summary>
    private static void EncodeContextMap(MetaBlockStorage arena, uint* contextMap, nuint contextMapSize, nuint numClusters, HuffmanTree* tree, ref int storageIx, byte* storage)
    {
        uint maxRunLengthPrefix = 6;
        uint* histogram = arena.ContextMapHistogram;
        byte* depths = arena.ContextMapDepths;
        ushort* bits = arena.ContextMapBits;
        const uint symbolMask = (1u << SymbolBits) - 1u;
        StoreVarLenUint8(numClusters - 1, ref storageIx, storage);
        if (numClusters == 1)
        {
            return;
        }

        uint* rleSymbols = arena.RleSymbols(contextMapSize);
        MoveToFrontTransform(contextMap, contextMapSize, rleSymbols);
        RunLengthCodeZeros(contextMapSize, rleSymbols, out nuint numRleSymbols, ref maxRunLengthPrefix);
        new Span<uint>(histogram, MaxContextMapSymbols).Clear();
        for (nuint i = 0; i < numRleSymbols; ++i)
        {
            ++histogram[rleSymbols[i] & symbolMask];
        }

        bool useRle = maxRunLengthPrefix > 0;
        BitWriter.Write(1, useRle ? 1u : 0u, ref storageIx, storage);
        if (useRle)
        {
            BitWriter.Write(4, maxRunLengthPrefix - 1, ref storageIx, storage);
        }

        int alphabet = (int)(numClusters + maxRunLengthPrefix);
        BuildAndStoreHuffmanTree(histogram, alphabet, alphabet, tree, depths, bits, ref storageIx, storage);
        for (nuint i = 0; i < numRleSymbols; ++i)
        {
            uint rleSymbol = rleSymbols[i] & symbolMask;
            uint extraBitsVal = rleSymbols[i] >> SymbolBits;
            BitWriter.Write(depths[rleSymbol], bits[rleSymbol], ref storageIx, storage);
            if (rleSymbol > 0 && rleSymbol <= maxRunLengthPrefix)
            {
                BitWriter.Write((int)rleSymbol, extraBitsVal, ref storageIx, storage);
            }
        }

        // Inverse move-to-front.
        BitWriter.Write(1, 1, ref storageIx, storage);
    }

    /// <summary>brotli's <c>StoreTrivialContextMap</c>: the histogram of each context is its block type's.</summary>
    private static void StoreTrivialContextMap(MetaBlockStorage arena, nuint numTypes, int contextBits, HuffmanTree* tree, ref int storageIx, byte* storage)
    {
        StoreVarLenUint8(numTypes - 1, ref storageIx, storage);
        if (numTypes > 1)
        {
            int repeatCode = contextBits - 1;
            uint repeatBits = (1u << repeatCode) - 1u;
            int alphabetSize = (int)numTypes + repeatCode;
            uint* histogram = arena.ContextMapHistogram;
            byte* depths = arena.ContextMapDepths;
            ushort* bits = arena.ContextMapBits;
            new Span<uint>(histogram, alphabetSize).Clear();

            // RLEMAX.
            BitWriter.Write(1, 1, ref storageIx, storage);
            BitWriter.Write(4, (ulong)(repeatCode - 1), ref storageIx, storage);
            histogram[repeatCode] = (uint)numTypes;
            histogram[0] = 1;
            for (int i = contextBits; i < alphabetSize; ++i)
            {
                histogram[i] = 1;
            }

            BuildAndStoreHuffmanTree(histogram, alphabetSize, alphabetSize, tree, depths, bits, ref storageIx, storage);
            for (nuint i = 0; i < numTypes; ++i)
            {
                nuint code = i == 0 ? 0 : i + (nuint)contextBits - 1;
                BitWriter.Write(depths[code], bits[code], ref storageIx, storage);
                BitWriter.Write(depths[repeatCode], bits[repeatCode], ref storageIx, storage);
                BitWriter.Write(repeatCode, repeatBits, ref storageIx, storage);
            }

            // Inverse move-to-front.
            BitWriter.Write(1, 1, ref storageIx, storage);
        }
    }

    /// <summary>
    /// brotli's <c>BlockSplitCode</c> and <c>BlockEncoder</c>: the codes of a category's block switches,
    /// and of its symbols, a code a block type.
    /// </summary>
    private struct BlockEncoder
    {
        public nuint HistogramLength;
        public nuint NumBlockTypes;
        public byte* BlockTypes;
        public uint* BlockLengths;
        public nuint NumBlocks;
        public nuint LastType;
        public nuint SecondLastType;
        public byte* TypeDepths;
        public ushort* TypeBits;
        public byte* LengthDepths;
        public ushort* LengthBits;
        public nuint BlockIx;
        public nuint BlockLen;
        public nuint EntropyIx;
        public byte* Depths;
        public ushort* Bits;

        /// <summary>brotli's <c>InitBlockEncoder</c>; the codes live in <paramref name="codes"/>.</summary>
        public void Init(nuint histogramLength, BlockSplit split, byte* codes)
        {
            HistogramLength = histogramLength;
            NumBlockTypes = split.NumTypes;
            BlockTypes = split.Types;
            BlockLengths = split.Lengths;
            NumBlocks = split.NumBlocks;
            LastType = 1;
            SecondLastType = 0;
            TypeBits = (ushort*)codes;
            LengthBits = TypeBits + MaxBlockTypeSymbols;
            TypeDepths = (byte*)(LengthBits + Prefix.NumBlockLengthSymbols);
            LengthDepths = TypeDepths + MaxBlockTypeSymbols;
            BlockIx = 0;
            BlockLen = split.NumBlocks == 0 ? 0 : split.Lengths[0];
            EntropyIx = 0;
            Depths = null;
            Bits = null;
        }

        /// <summary>The bytes of the block switch codes an encoder keeps.</summary>
        public const int CodesSize = (MaxBlockTypeSymbols + Prefix.NumBlockLengthSymbols) * 3;

        /// <summary>brotli's <c>NextBlockTypeCode</c>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nuint NextBlockTypeCode(byte type)
        {
            nuint typeCode = type == LastType + 1 ? 1u : type == SecondLastType ? 0u : type + 2u;
            SecondLastType = LastType;
            LastType = type;
            return typeCode;
        }
    }

    /// <summary>brotli's <c>StoreBlockSwitch</c>.</summary>
    private static void StoreBlockSwitch(ref BlockEncoder code, uint blockLen, byte blockType, bool isFirstBlock, ref int storageIx, byte* storage)
    {
        nuint typeCode = code.NextBlockTypeCode(blockType);
        if (!isFirstBlock)
        {
            BitWriter.Write(code.TypeDepths[typeCode], code.TypeBits[typeCode], ref storageIx, storage);
        }

        int lenCode = BlockLengthPrefixCode(blockLen);
        int lenNumExtra = Prefix.BlockLengthBits[lenCode];
        uint lenExtra = blockLen - (uint)Prefix.BlockLengthOffset[lenCode];
        BitWriter.Write(code.LengthDepths[lenCode], code.LengthBits[lenCode], ref storageIx, storage);
        BitWriter.Write(lenNumExtra, lenExtra, ref storageIx, storage);
    }

    /// <summary>brotli's <c>BuildAndStoreBlockSplitCode</c>: the codes of the block types and lengths, and the first block's length.</summary>
    private static void BuildAndStoreBlockSwitchEntropyCodes(ref BlockEncoder self, HuffmanTree* tree, ref int storageIx, byte* storage)
    {
        uint* typeHisto = stackalloc uint[MaxBlockTypeSymbols];
        uint* lengthHisto = stackalloc uint[Prefix.NumBlockLengthSymbols];
        nuint numTypes = self.NumBlockTypes;
        new Span<uint>(typeHisto, (int)numTypes + 2).Clear();
        new Span<uint>(lengthHisto, Prefix.NumBlockLengthSymbols).Clear();
        nuint lastType = 1;
        nuint secondLastType = 0;
        for (nuint i = 0; i < self.NumBlocks; ++i)
        {
            byte type = self.BlockTypes[i];
            nuint typeCode = type == lastType + 1 ? 1u : type == secondLastType ? 0u : type + 2u;
            secondLastType = lastType;
            lastType = type;
            if (i != 0)
            {
                ++typeHisto[typeCode];
            }

            ++lengthHisto[BlockLengthPrefixCode(self.BlockLengths[i])];
        }

        StoreVarLenUint8(numTypes - 1, ref storageIx, storage);
        if (numTypes > 1)
        {
            BuildAndStoreHuffmanTree(typeHisto, (int)numTypes + 2, (int)numTypes + 2, tree, self.TypeDepths, self.TypeBits, ref storageIx, storage);
            BuildAndStoreHuffmanTree(lengthHisto, Prefix.NumBlockLengthSymbols, Prefix.NumBlockLengthSymbols, tree, self.LengthDepths, self.LengthBits, ref storageIx, storage);
            StoreBlockSwitch(ref self, self.BlockLengths[0], self.BlockTypes[0], true, ref storageIx, storage);
        }
    }

    /// <summary>brotli's <c>BuildAndStoreEntropyCodes</c>: a code for each histogram.</summary>
    private static void BuildAndStoreEntropyCodes<THistogram>(ref BlockEncoder self, MetaBlockStorage arena, int category, THistogram* histograms, nuint histogramsSize, int alphabetSize, HuffmanTree* tree, ref int storageIx, byte* storage)
        where THistogram : unmanaged, IHistogram
    {
        nuint tableSize = histogramsSize * self.HistogramLength;
        self.Depths = arena.Depths(category, tableSize);
        self.Bits = arena.Bits(category, tableSize);
        for (nuint i = 0; i < histogramsSize; ++i)
        {
            nuint ix = i * self.HistogramLength;
            BuildAndStoreHuffmanTree(Histogram.Data(histograms + i), (int)self.HistogramLength, alphabetSize, tree, self.Depths + ix, self.Bits + ix, ref storageIx, storage);
        }
    }

    /// <summary>brotli's <c>StoreSymbol</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreSymbol(ref BlockEncoder self, nuint symbol, ref int storageIx, byte* storage)
    {
        if (self.BlockLen == 0)
        {
            nuint blockIx = ++self.BlockIx;
            uint blockLen = self.BlockLengths[blockIx];
            byte blockType = self.BlockTypes[blockIx];
            self.BlockLen = blockLen;
            self.EntropyIx = blockType * self.HistogramLength;
            StoreBlockSwitch(ref self, blockLen, blockType, false, ref storageIx, storage);
        }

        --self.BlockLen;
        nuint ix = self.EntropyIx + symbol;
        BitWriter.Write(self.Depths[ix], self.Bits[ix], ref storageIx, storage);
    }

    /// <summary>brotli's <c>StoreSymbolWithContext</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreSymbolWithContext(ref BlockEncoder self, nuint symbol, nuint context, uint* contextMap, ref int storageIx, byte* storage, int contextBits)
    {
        if (self.BlockLen == 0)
        {
            nuint blockIx = ++self.BlockIx;
            uint blockLen = self.BlockLengths[blockIx];
            byte blockType = self.BlockTypes[blockIx];
            self.BlockLen = blockLen;
            self.EntropyIx = (nuint)blockType << contextBits;
            StoreBlockSwitch(ref self, blockLen, blockType, false, ref storageIx, storage);
        }

        --self.BlockLen;
        nuint histoIx = contextMap[self.EntropyIx + context];
        nuint ix = (histoIx * self.HistogramLength) + symbol;
        BitWriter.Write(self.Depths[ix], self.Bits[ix], ref storageIx, storage);
    }

    /// <summary>brotli's <c>BrotliStoreMetaBlock</c>: a meta-block with its block splits and context maps.</summary>
    public static void StoreMetaBlock(MetaBlockStorage arena, byte* input, nuint startPos, int length, nuint mask, byte prevByte, byte prevByte2, bool isLast, in DistanceParams dist, int literalContextMode, Command* commands, nuint numCommands, MetaBlockSplit mb, ref int storageIx, byte* storage)
    {
        nuint pos = startPos;
        int numDistanceSymbols = (int)dist.AlphabetSizeMax;
        nuint numEffectiveDistanceSymbols = dist.AlphabetSizeLimit;
        byte* literalContextLut = ContextLut(literalContextMode);
        StoreCompressedMetaBlockHeader(isLast, length, ref storageIx, storage);
        HuffmanTree* tree = arena.Tree;
        BlockEncoder literalEnc = default;
        BlockEncoder commandEnc = default;
        BlockEncoder distanceEnc = default;
        literalEnc.Init(Prefix.NumLiteralSymbols, mb.LiteralSplit, arena.Codes(0));
        commandEnc.Init(Prefix.NumCommandSymbols, mb.CommandSplit, arena.Codes(1));
        distanceEnc.Init(numEffectiveDistanceSymbols, mb.DistanceSplit, arena.Codes(2));
        BuildAndStoreBlockSwitchEntropyCodes(ref literalEnc, tree, ref storageIx, storage);
        BuildAndStoreBlockSwitchEntropyCodes(ref commandEnc, tree, ref storageIx, storage);
        BuildAndStoreBlockSwitchEntropyCodes(ref distanceEnc, tree, ref storageIx, storage);
        BitWriter.Write(2, dist.PostfixBits, ref storageIx, storage);
        BitWriter.Write(4, dist.NumDirectCodes >> (int)dist.PostfixBits, ref storageIx, storage);
        for (nuint i = 0; i < mb.LiteralSplit.NumTypes; ++i)
        {
            BitWriter.Write(2, (ulong)literalContextMode, ref storageIx, storage);
        }

        if (mb.LiteralContextMapSize == 0)
        {
            StoreTrivialContextMap(arena, mb.LiteralHistogramsSize, Prefix.LiteralContextBits, tree, ref storageIx, storage);
        }
        else
        {
            EncodeContextMap(arena, mb.LiteralContextMap, mb.LiteralContextMapSize, mb.LiteralHistogramsSize, tree, ref storageIx, storage);
        }

        if (mb.DistanceContextMapSize == 0)
        {
            StoreTrivialContextMap(arena, mb.DistanceHistogramsSize, Prefix.DistanceContextBits, tree, ref storageIx, storage);
        }
        else
        {
            EncodeContextMap(arena, mb.DistanceContextMap, mb.DistanceContextMapSize, mb.DistanceHistogramsSize, tree, ref storageIx, storage);
        }

        BuildAndStoreEntropyCodes(ref literalEnc, arena, 0, mb.LiteralHistograms, mb.LiteralHistogramsSize, Prefix.NumLiteralSymbols, tree, ref storageIx, storage);
        BuildAndStoreEntropyCodes(ref commandEnc, arena, 1, mb.CommandHistograms, mb.CommandHistogramsSize, Prefix.NumCommandSymbols, tree, ref storageIx, storage);
        BuildAndStoreEntropyCodes(ref distanceEnc, arena, 2, mb.DistanceHistograms, mb.DistanceHistogramsSize, numDistanceSymbols, tree, ref storageIx, storage);

        for (nuint i = 0; i < numCommands; ++i)
        {
            Command* cmd = commands + i;
            StoreSymbol(ref commandEnc, cmd->CmdPrefix, ref storageIx, storage);
            StoreCommandExtra(cmd, ref storageIx, storage);
            if (mb.LiteralContextMapSize == 0)
            {
                for (nuint j = cmd->InsertLen; j != 0; --j)
                {
                    StoreSymbol(ref literalEnc, input[pos & mask], ref storageIx, storage);
                    ++pos;
                }
            }
            else
            {
                for (nuint j = cmd->InsertLen; j != 0; --j)
                {
                    nuint context = (nuint)(literalContextLut[prevByte] | literalContextLut[256 + prevByte2]);
                    byte literal = input[pos & mask];
                    StoreSymbolWithContext(ref literalEnc, literal, context, mb.LiteralContextMap, ref storageIx, storage, Prefix.LiteralContextBits);
                    prevByte2 = prevByte;
                    prevByte = literal;
                    ++pos;
                }
            }

            uint copyLen = Commands.CopyLen(cmd);
            pos += copyLen;
            if (copyLen != 0)
            {
                prevByte2 = input[(pos - 2) & mask];
                prevByte = input[(pos - 1) & mask];
                if (cmd->CmdPrefix >= 128)
                {
                    nuint distCode = (nuint)(cmd->DistPrefix & 0x3FF);
                    int distNumExtra = cmd->DistPrefix >> 10;
                    ulong distExtra = cmd->DistExtra;
                    if (mb.DistanceContextMapSize == 0)
                    {
                        StoreSymbol(ref distanceEnc, distCode, ref storageIx, storage);
                    }
                    else
                    {
                        nuint context = Commands.DistanceContext(cmd);
                        StoreSymbolWithContext(ref distanceEnc, distCode, context, mb.DistanceContextMap, ref storageIx, storage, Prefix.DistanceContextBits);
                    }

                    BitWriter.Write(distNumExtra, distExtra, ref storageIx, storage);
                }
            }
        }

        if (isLast)
        {
            BitWriter.JumpToByteBoundary(ref storageIx, storage);
        }
    }

    /// <summary>brotli's <c>BuildHistograms</c>.</summary>
    private static void BuildHistograms(byte* input, nuint startPos, nuint mask, Command* commands, nuint numCommands, HistogramLiteral* litHisto, HistogramCommand* cmdHisto, HistogramDistance* distHisto)
    {
        nuint pos = startPos;
        for (nuint i = 0; i < numCommands; ++i)
        {
            Command* cmd = commands + i;
            Histogram.Add(cmdHisto, cmd->CmdPrefix);
            for (nuint j = cmd->InsertLen; j != 0; --j)
            {
                Histogram.Add(litHisto, input[pos & mask]);
                ++pos;
            }

            uint copyLen = Commands.CopyLen(cmd);
            pos += copyLen;
            if (copyLen != 0 && cmd->CmdPrefix >= 128)
            {
                Histogram.Add(distHisto, (nuint)(cmd->DistPrefix & 0x3FF));
            }
        }
    }

    /// <summary>brotli's <c>StoreDataWithHuffmanCodes</c>: the commands, literals and distances, a code each.</summary>
    private static void StoreDataWithHuffmanCodes(byte* input, nuint startPos, nuint mask, Command* commands, nuint numCommands, byte* litDepth, ushort* litBits, byte* cmdDepth, ushort* cmdBits, byte* distDepth, ushort* distBits, ref int storageIx, byte* storage)
    {
        nuint pos = startPos;
        for (nuint i = 0; i < numCommands; ++i)
        {
            Command* cmd = commands + i;
            nuint cmdCode = cmd->CmdPrefix;
            BitWriter.Write(cmdDepth[cmdCode], cmdBits[cmdCode], ref storageIx, storage);
            StoreCommandExtra(cmd, ref storageIx, storage);
            for (nuint j = cmd->InsertLen; j != 0; --j)
            {
                byte literal = input[pos & mask];
                BitWriter.Write(litDepth[literal], litBits[literal], ref storageIx, storage);
                ++pos;
            }

            uint copyLen = Commands.CopyLen(cmd);
            pos += copyLen;
            if (copyLen != 0 && cmd->CmdPrefix >= 128)
            {
                nuint distCode = (nuint)(cmd->DistPrefix & 0x3FF);
                int distNumExtra = cmd->DistPrefix >> 10;
                uint distExtra = cmd->DistExtra;
                BitWriter.Write(distDepth[distCode], distBits[distCode], ref storageIx, storage);
                BitWriter.Write(distNumExtra, distExtra, ref storageIx, storage);
            }
        }
    }

    /// <summary>brotli's <c>BrotliStoreMetaBlockTrivial</c>: quality 3, a code a category.</summary>
    public static void StoreMetaBlockTrivial(MetaBlockStorage arena, byte* input, nuint startPos, int length, nuint mask, bool isLast, in DistanceParams dist, Command* commands, nuint numCommands, ref int storageIx, byte* storage)
    {
        int numDistanceSymbols = (int)dist.AlphabetSizeMax;
        StoreCompressedMetaBlockHeader(isLast, length, ref storageIx, storage);
        HistogramLiteral* litHisto = arena.LiteralHistogram;
        HistogramCommand* cmdHisto = arena.CommandHistogram;
        HistogramDistance* distHisto = arena.DistanceHistogram;
        Histogram.Clear(litHisto);
        Histogram.Clear(cmdHisto);
        Histogram.Clear(distHisto);
        BuildHistograms(input, startPos, mask, commands, numCommands, litHisto, cmdHisto, distHisto);
        BitWriter.Write(13, 0, ref storageIx, storage);
        HuffmanTree* tree = arena.Tree;
        BuildAndStoreHuffmanTree(Histogram.Data(litHisto), Prefix.NumLiteralSymbols, Prefix.NumLiteralSymbols, tree, arena.LiteralDepth, arena.LiteralBits, ref storageIx, storage);
        BuildAndStoreHuffmanTree(Histogram.Data(cmdHisto), Prefix.NumCommandSymbols, Prefix.NumCommandSymbols, tree, arena.CommandDepth, arena.CommandBits, ref storageIx, storage);
        BuildAndStoreHuffmanTree(Histogram.Data(distHisto), MaxSimpleDistanceAlphabetSize, numDistanceSymbols, tree, arena.DistanceDepth, arena.DistanceBits, ref storageIx, storage);
        StoreDataWithHuffmanCodes(input, startPos, mask, commands, numCommands, arena.LiteralDepth, arena.LiteralBits, arena.CommandDepth, arena.CommandBits, arena.DistanceDepth, arena.DistanceBits, ref storageIx, storage);
        if (isLast)
        {
            BitWriter.JumpToByteBoundary(ref storageIx, storage);
        }
    }

    /// <summary>
    /// brotli's <c>BrotliStoreMetaBlockFast</c>: quality 2, fast codes, and the static codes of commands
    /// and distances for 128 commands or fewer.
    /// </summary>
    public static void StoreMetaBlockFast(MetaBlockStorage arena, byte* input, nuint startPos, int length, nuint mask, bool isLast, in DistanceParams dist, Command* commands, nuint numCommands, ref int storageIx, byte* storage)
    {
        uint numDistanceSymbols = dist.AlphabetSizeMax;
        int distanceAlphabetBits = FastLog.Log2FloorNonZero(numDistanceSymbols - 1) + 1;
        StoreCompressedMetaBlockHeader(isLast, length, ref storageIx, storage);
        BitWriter.Write(13, 0, ref storageIx, storage);
        HuffmanTree* tree = arena.Tree;
        if (numCommands <= 128)
        {
            uint* histogram = (uint*)Histogram.Data(arena.LiteralHistogram);
            new Span<uint>(histogram, Prefix.NumLiteralSymbols).Clear();
            nuint pos = startPos;
            nuint numLiterals = 0;
            for (nuint i = 0; i < numCommands; ++i)
            {
                Command* cmd = commands + i;
                for (nuint j = cmd->InsertLen; j != 0; --j)
                {
                    ++histogram[input[pos & mask]];
                    ++pos;
                }

                numLiterals += cmd->InsertLen;
                pos += Commands.CopyLen(cmd);
            }

            BuildAndStoreHuffmanTreeFast(tree, histogram, numLiterals, 8, arena.LiteralDepth, arena.LiteralBits, ref storageIx, storage);

            // brotli's StoreStaticCommandHuffmanTree and StoreStaticDistanceHuffmanTree.
            BitWriter.Write(56, 0x0092624416307003, ref storageIx, storage);
            BitWriter.Write(3, 0, ref storageIx, storage);
            BitWriter.Write(28, 0x0369DC03, ref storageIx, storage);
            fixed (byte* cmdDepth = EncoderTables.StaticCommandCodeDepth)
            fixed (ushort* cmdBits = EncoderTables.StaticCommandCodeBits)
            fixed (byte* distDepth = EncoderTables.StaticDistanceCodeDepth)
            fixed (ushort* distBits = EncoderTables.StaticDistanceCodeBits)
            {
                StoreDataWithHuffmanCodes(input, startPos, mask, commands, numCommands, arena.LiteralDepth, arena.LiteralBits, cmdDepth, cmdBits, distDepth, distBits, ref storageIx, storage);
            }
        }
        else
        {
            HistogramLiteral* litHisto = arena.LiteralHistogram;
            HistogramCommand* cmdHisto = arena.CommandHistogram;
            HistogramDistance* distHisto = arena.DistanceHistogram;
            Histogram.Clear(litHisto);
            Histogram.Clear(cmdHisto);
            Histogram.Clear(distHisto);
            BuildHistograms(input, startPos, mask, commands, numCommands, litHisto, cmdHisto, distHisto);
            BuildAndStoreHuffmanTreeFast(tree, Histogram.Data(litHisto), *Histogram.TotalCount(litHisto), 8, arena.LiteralDepth, arena.LiteralBits, ref storageIx, storage);
            BuildAndStoreHuffmanTreeFast(tree, Histogram.Data(cmdHisto), *Histogram.TotalCount(cmdHisto), 10, arena.CommandDepth, arena.CommandBits, ref storageIx, storage);
            BuildAndStoreHuffmanTreeFast(tree, Histogram.Data(distHisto), *Histogram.TotalCount(distHisto), distanceAlphabetBits, arena.DistanceDepth, arena.DistanceBits, ref storageIx, storage);
            StoreDataWithHuffmanCodes(input, startPos, mask, commands, numCommands, arena.LiteralDepth, arena.LiteralBits, arena.CommandDepth, arena.CommandBits, arena.DistanceDepth, arena.DistanceBits, ref storageIx, storage);
        }

        if (isLast)
        {
            BitWriter.JumpToByteBoundary(ref storageIx, storage);
        }
    }

    /// <summary>brotli's <c>BROTLI_CONTEXT_LUT</c>: the context lookup table of a literal context mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* ContextLut(int mode) =>
        (byte*)Unsafe.AsPointer(ref Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(Context.Lookup), mode << 9));
}

/// <summary>The memory of the meta-block storage functions: brotli's arenas, pinned and kept.</summary>
internal sealed unsafe class MetaBlockStorage
{
    private readonly PinnedBuffer<HuffmanTree> _tree = new();
    private readonly PinnedBuffer<byte> _fixed = new();
    private readonly PinnedBuffer<uint> _rleSymbols = new();
    private readonly PinnedBuffer<byte>[] _depths = [new(), new(), new()];
    private readonly PinnedBuffer<ushort>[] _bits = [new(), new(), new()];
    private readonly PinnedBuffer<HistogramLiteral> _literalHistogram = new();
    private readonly PinnedBuffer<HistogramCommand> _commandHistogram = new();
    private readonly PinnedBuffer<HistogramDistance> _distanceHistogram = new();
    private readonly byte* _fixedBase;

    private const int ContextMapSymbols = Prefix.MaxBlockTypes + 16;
    private const int CodesOffset = 0;
    private const int CodesSize = ((Prefix.MaxBlockTypes + 2 + Prefix.NumBlockLengthSymbols) * 3) + 8;
    private const int HistogramOffset = CodesOffset + (3 * CodesSize);
    private const int DepthsOffset = HistogramOffset + (sizeof(uint) * ContextMapSymbols);
    private const int BitsOffset = DepthsOffset + ContextMapSymbols + 16;
    private const int LiteralDepthOffset = BitsOffset + (sizeof(ushort) * ContextMapSymbols);
    private const int LiteralBitsOffset = LiteralDepthOffset + Prefix.NumLiteralSymbols;
    private const int CommandDepthOffset = LiteralBitsOffset + (sizeof(ushort) * Prefix.NumLiteralSymbols);
    private const int CommandBitsOffset = CommandDepthOffset + Prefix.NumCommandSymbols;
    private const int DistanceDepthOffset = CommandBitsOffset + (sizeof(ushort) * Prefix.NumCommandSymbols);
    private const int DistanceBitsOffset = DistanceDepthOffset + HistogramDistance.Symbols;
    private const int FixedSize = DistanceBitsOffset + (sizeof(ushort) * HistogramDistance.Symbols);

    public MetaBlockStorage()
    {
        Tree = _tree.Ensure(BitStream.MaxHuffmanTreeSize);
        _fixedBase = _fixed.Ensure(FixedSize);
        LiteralHistogram = _literalHistogram.Ensure(1);
        CommandHistogram = _commandHistogram.Ensure(1);
        DistanceHistogram = _distanceHistogram.Ensure(1);
    }

    public HuffmanTree* Tree { get; }

    public HistogramLiteral* LiteralHistogram { get; }

    public HistogramCommand* CommandHistogram { get; }

    public HistogramDistance* DistanceHistogram { get; }

    public uint* ContextMapHistogram => (uint*)(_fixedBase + HistogramOffset);

    public byte* ContextMapDepths => _fixedBase + DepthsOffset;

    public ushort* ContextMapBits => (ushort*)(_fixedBase + BitsOffset);

    public byte* LiteralDepth => _fixedBase + LiteralDepthOffset;

    public ushort* LiteralBits => (ushort*)(_fixedBase + LiteralBitsOffset);

    public byte* CommandDepth => _fixedBase + CommandDepthOffset;

    public ushort* CommandBits => (ushort*)(_fixedBase + CommandBitsOffset);

    public byte* DistanceDepth => _fixedBase + DistanceDepthOffset;

    public ushort* DistanceBits => (ushort*)(_fixedBase + DistanceBitsOffset);

    /// <summary>The block switch codes of the category <paramref name="category"/>.</summary>
    public byte* Codes(int category) => _fixedBase + CodesOffset + (category * CodesSize);

    public uint* RleSymbols(nuint size) => _rleSymbols.Ensure(size);

    public byte* Depths(int category, nuint size) => _depths[category].Ensure(size);

    public ushort* Bits(int category, nuint size) => _bits[category].Ensure(size);
}
