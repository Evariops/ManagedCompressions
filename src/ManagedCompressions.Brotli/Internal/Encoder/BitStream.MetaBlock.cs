using System;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// The compressed meta-blocks of qualities 2 to 9 (<c>c/enc/brotli_bit_stream.c</c>): with static codes
/// and fast codes at quality 2, a code a category at quality 3, and block splits and context maps from
/// quality 4.
/// </summary>
internal static partial class BitStream
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
    private static void StoreVarLenUint8(nuint n, ref int storageIx, Span<byte> storage)
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
    public static void StoreCompressedMetaBlockHeader(bool isFinalBlock, int length, ref int storageIx, Span<byte> storage)
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
    private static void StoreCommandExtra(in Command cmd, ref BitSink sink)
    {
        uint copyLenCode = Commands.CopyLenCode(in cmd);
        uint insCode = Commands.GetInsertLengthCode(cmd.InsertLen);
        uint copyCode = Commands.GetCopyLengthCode(copyLenCode);
        uint insNumExtra = Commands.GetInsertExtra(insCode);
        ulong insExtraVal = cmd.InsertLen - Commands.GetInsertBase(insCode);
        ulong copyExtraVal = copyLenCode - Commands.GetCopyBase(copyCode);
        ulong bits = (copyExtraVal << (int)insNumExtra) | insExtraVal;
        sink.Write((int)(insNumExtra + Commands.GetCopyExtra(copyCode)), bits);
    }

    /// <summary>
    /// brotli's <c>BuildAndStoreHuffmanTree</c>: a code of at most 15 bits over <paramref name="histogram"/>,
    /// stored as a simple code for up to four symbols. <paramref name="tree"/> holds
    /// <see cref="MaxHuffmanTreeSize"/> nodes.
    /// </summary>
    public static void BuildAndStoreHuffmanTree(ReadOnlySpan<uint> histogram, int alphabetSize, Span<HuffmanTree> tree, Span<byte> depth, Span<ushort> bits, ref int storageIx, Span<byte> storage)
    {
        int histogramLength = histogram.Length;
        int count = 0;
        Span<nuint> s4 = stackalloc nuint[4];
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
            depth[(int)s4[0]] = 0;
            bits[(int)s4[0]] = 0;
            return;
        }

        depth = depth.Slice(0, histogramLength);
        depth.Clear();
        EntropyEncode.CreateHuffmanTree(histogram, 15, tree, depth);
        EntropyEncode.ConvertBitDepthsToSymbols(depth, bits);
        if (count <= 4)
        {
            StoreSimpleHuffmanTree(depth, s4, count, maxBits, ref storageIx, storage);
        }
        else
        {
            StoreHuffmanTree(depth, tree, ref storageIx, storage);
        }
    }

    /// <summary>brotli's <c>MoveToFrontTransform</c>.</summary>
    private static void MoveToFrontTransform(ReadOnlySpan<uint> vIn, uint[] vOut)
    {
        if (vIn.IsEmpty)
        {
            return;
        }

        uint maxValue = vIn[0];
        for (int i = 1; i < vIn.Length; ++i)
        {
            if (vIn[i] > maxValue)
            {
                maxValue = vIn[i];
            }
        }

        Span<byte> mtf = stackalloc byte[256];
        for (int i = 0; i <= (int)maxValue; ++i)
        {
            mtf[i] = (byte)i;
        }

        int mtfSize = (int)maxValue + 1;
        for (int i = 0; i < vIn.Length; ++i)
        {
            byte value = (byte)vIn[i];
            int index = 0;
            while (index < mtfSize && mtf[index] != value)
            {
                ++index;
            }

            vOut[i] = (uint)index;
            for (int k = index; k != 0; --k)
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
    private static void RunLengthCodeZeros(nuint inSize, uint[] v, out nuint outSize, ref uint maxRunLengthPrefix)
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
    private static void EncodeContextMap(MetaBlockStorage arena, ReadOnlySpan<uint> contextMap, nuint numClusters, Span<HuffmanTree> tree, ref int storageIx, Span<byte> storage)
    {
        nuint contextMapSize = (nuint)contextMap.Length;
        uint maxRunLengthPrefix = 6;
        uint[] histogram = arena.ContextMapHistogram;
        byte[] depths = arena.ContextMapDepths;
        ushort[] bits = arena.ContextMapBits;
        const uint symbolMask = (1u << SymbolBits) - 1u;
        StoreVarLenUint8(numClusters - 1, ref storageIx, storage);
        if (numClusters == 1)
        {
            return;
        }

        uint[] rleSymbols = arena.RleSymbols(contextMapSize);
        MoveToFrontTransform(contextMap, rleSymbols);
        RunLengthCodeZeros(contextMapSize, rleSymbols, out nuint numRleSymbols, ref maxRunLengthPrefix);
        histogram.AsSpan(0, MaxContextMapSymbols).Clear();
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
        BuildAndStoreHuffmanTree(histogram.AsSpan(0, alphabet), alphabet, tree, depths, bits, ref storageIx, storage);
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
    private static void StoreTrivialContextMap(MetaBlockStorage arena, nuint numTypes, int contextBits, Span<HuffmanTree> tree, ref int storageIx, Span<byte> storage)
    {
        StoreVarLenUint8(numTypes - 1, ref storageIx, storage);
        if (numTypes > 1)
        {
            int repeatCode = contextBits - 1;
            uint repeatBits = (1u << repeatCode) - 1u;
            int alphabetSize = (int)numTypes + repeatCode;
            uint[] histogram = arena.ContextMapHistogram;
            byte[] depths = arena.ContextMapDepths;
            ushort[] bits = arena.ContextMapBits;
            histogram.AsSpan(0, alphabetSize).Clear();

            // RLEMAX.
            BitWriter.Write(1, 1, ref storageIx, storage);
            BitWriter.Write(4, (ulong)(repeatCode - 1), ref storageIx, storage);
            histogram[repeatCode] = (uint)numTypes;
            histogram[0] = 1;
            for (int i = contextBits; i < alphabetSize; ++i)
            {
                histogram[i] = 1;
            }

            BuildAndStoreHuffmanTree(histogram.AsSpan(0, alphabetSize), alphabetSize, tree, depths, bits, ref storageIx, storage);
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
        public byte[] BlockTypes;
        public uint[] BlockLengths;
        public nuint NumBlocks;
        public nuint LastType;
        public nuint SecondLastType;
        public byte[] TypeDepths;
        public ushort[] TypeBits;
        public byte[] LengthDepths;
        public ushort[] LengthBits;
        public nuint BlockIx;
        public nuint BlockLen;
        public nuint EntropyIx;
        public byte[] Depths;
        public ushort[] Bits;

        /// <summary>brotli's <c>InitBlockEncoder</c>; the codes live in <paramref name="codes"/>.</summary>
        public void Init(nuint histogramLength, BlockSplit split, BlockSwitchCodes codes)
        {
            HistogramLength = histogramLength;
            NumBlockTypes = split.NumTypes;
            BlockTypes = split.Types;
            BlockLengths = split.Lengths;
            NumBlocks = split.NumBlocks;
            LastType = 1;
            SecondLastType = 0;
            TypeBits = codes.TypeBits;
            LengthBits = codes.LengthBits;
            TypeDepths = codes.TypeDepths;
            LengthDepths = codes.LengthDepths;
            BlockIx = 0;
            BlockLen = split.NumBlocks == 0 ? 0 : split.Lengths[0];
            EntropyIx = 0;
            Depths = [];
            Bits = [];
        }

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

    /// <summary>The codes of a category's block switches, which a <see cref="BlockEncoder"/> keeps.</summary>
    internal sealed class BlockSwitchCodes
    {
        public readonly byte[] TypeDepths = new byte[MaxBlockTypeSymbols];
        public readonly ushort[] TypeBits = new ushort[MaxBlockTypeSymbols];
        public readonly byte[] LengthDepths = new byte[Prefix.NumBlockLengthSymbols];
        public readonly ushort[] LengthBits = new ushort[Prefix.NumBlockLengthSymbols];
    }

    /// <summary>brotli's <c>StoreBlockSwitch</c>.</summary>
    private static void StoreBlockSwitch(ref BlockEncoder code, uint blockLen, byte blockType, bool isFirstBlock, ref BitSink sink)
    {
        nuint typeCode = code.NextBlockTypeCode(blockType);
        if (!isFirstBlock)
        {
            sink.Write(code.TypeDepths[typeCode], code.TypeBits[typeCode]);
        }

        int lenCode = BlockLengthPrefixCode(blockLen);
        int lenNumExtra = Prefix.BlockLengthBits[lenCode];
        uint lenExtra = blockLen - (uint)Prefix.BlockLengthOffset[lenCode];
        sink.Write(code.LengthDepths[lenCode], code.LengthBits[lenCode]);
        sink.Write(lenNumExtra, lenExtra);
    }

    /// <summary>brotli's <c>BuildAndStoreBlockSplitCode</c>: the codes of the block types and lengths, and the first block's length.</summary>
    private static void BuildAndStoreBlockSwitchEntropyCodes(ref BlockEncoder self, Span<HuffmanTree> tree, ref int storageIx, Span<byte> storage)
    {
        Span<uint> typeHisto = stackalloc uint[MaxBlockTypeSymbols];
        Span<uint> lengthHisto = stackalloc uint[Prefix.NumBlockLengthSymbols];
        nuint numTypes = self.NumBlockTypes;
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
                ++typeHisto[(int)typeCode];
            }

            ++lengthHisto[BlockLengthPrefixCode(self.BlockLengths[i])];
        }

        StoreVarLenUint8(numTypes - 1, ref storageIx, storage);
        if (numTypes > 1)
        {
            BuildAndStoreHuffmanTree(typeHisto.Slice(0, (int)numTypes + 2), (int)numTypes + 2, tree, self.TypeDepths, self.TypeBits, ref storageIx, storage);
            BuildAndStoreHuffmanTree(lengthHisto, Prefix.NumBlockLengthSymbols, tree, self.LengthDepths, self.LengthBits, ref storageIx, storage);
            var sink = new BitSink(storageIx, storage);
            StoreBlockSwitch(ref self, self.BlockLengths[0], self.BlockTypes[0], true, ref sink);
            storageIx = sink.Finish();
        }
    }

    /// <summary>brotli's <c>BuildAndStoreEntropyCodes</c>: a code for each histogram.</summary>
    private static void BuildAndStoreEntropyCodes<THistogram>(ref BlockEncoder self, MetaBlockStorage arena, int category, Span<THistogram> histograms, int alphabetSize, Span<HuffmanTree> tree, ref int storageIx, Span<byte> storage)
        where THistogram : struct, IHistogram
    {
        int histogramLength = (int)self.HistogramLength;
        nuint tableSize = (nuint)histograms.Length * self.HistogramLength;
        self.Depths = arena.Depths(category, tableSize);
        self.Bits = arena.Bits(category, tableSize);
        for (int i = 0; i < histograms.Length; ++i)
        {
            int ix = i * histogramLength;
            BuildAndStoreHuffmanTree(histograms[i].Data.Slice(0, histogramLength), alphabetSize, tree, self.Depths.AsSpan(ix), self.Bits.AsSpan(ix), ref storageIx, storage);
        }
    }

    /// <summary>brotli's <c>StoreSymbol</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreSymbol(ref BlockEncoder self, nuint symbol, ref BitSink sink)
    {
        if (self.BlockLen == 0)
        {
            nuint blockIx = ++self.BlockIx;
            uint blockLen = self.BlockLengths[blockIx];
            byte blockType = self.BlockTypes[blockIx];
            self.BlockLen = blockLen;
            self.EntropyIx = blockType * self.HistogramLength;
            StoreBlockSwitch(ref self, blockLen, blockType, false, ref sink);
        }

        --self.BlockLen;
        nuint ix = self.EntropyIx + symbol;
        sink.Write(self.Depths[ix], self.Bits[ix]);
    }

    /// <summary>brotli's <c>StoreSymbolWithContext</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreSymbolWithContext(ref BlockEncoder self, nuint symbol, nuint context, uint[] contextMap, ref BitSink sink, int contextBits)
    {
        if (self.BlockLen == 0)
        {
            nuint blockIx = ++self.BlockIx;
            uint blockLen = self.BlockLengths[blockIx];
            byte blockType = self.BlockTypes[blockIx];
            self.BlockLen = blockLen;
            self.EntropyIx = (nuint)blockType << contextBits;
            StoreBlockSwitch(ref self, blockLen, blockType, false, ref sink);
        }

        --self.BlockLen;
        nuint histoIx = contextMap[self.EntropyIx + context];
        nuint ix = (histoIx * self.HistogramLength) + symbol;
        sink.Write(self.Depths[ix], self.Bits[ix]);
    }

    /// <summary>brotli's <c>BrotliStoreMetaBlock</c>: a meta-block with its block splits and context maps.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void StoreMetaBlock(MetaBlockStorage arena, ReadOnlySpan<byte> input, nuint startPos, int length, nuint mask, byte prevByte, byte prevByte2, bool isLast, in DistanceParams dist, int literalContextMode, ReadOnlySpan<Command> commands, MetaBlockSplit mb, ref int storageIx, Span<byte> storage)
    {
        nuint pos = startPos;
        int numDistanceSymbols = (int)dist.AlphabetSizeMax;
        nuint numEffectiveDistanceSymbols = dist.AlphabetSizeLimit;
        ReadOnlySpan<byte> literalContextLut = ContextLut(literalContextMode);
        StoreCompressedMetaBlockHeader(isLast, length, ref storageIx, storage);
        HuffmanTree[] tree = arena.Tree;
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
            EncodeContextMap(arena, mb.LiteralContextMap.AsSpan(0, (int)mb.LiteralContextMapSize), mb.LiteralHistogramsSize, tree, ref storageIx, storage);
        }

        if (mb.DistanceContextMapSize == 0)
        {
            StoreTrivialContextMap(arena, mb.DistanceHistogramsSize, Prefix.DistanceContextBits, tree, ref storageIx, storage);
        }
        else
        {
            EncodeContextMap(arena, mb.DistanceContextMap.AsSpan(0, (int)mb.DistanceContextMapSize), mb.DistanceHistogramsSize, tree, ref storageIx, storage);
        }

        BuildAndStoreEntropyCodes(ref literalEnc, arena, 0, mb.LiteralHistograms.AsSpan(0, (int)mb.LiteralHistogramsSize), Prefix.NumLiteralSymbols, tree, ref storageIx, storage);
        BuildAndStoreEntropyCodes(ref commandEnc, arena, 1, mb.CommandHistograms.AsSpan(0, (int)mb.CommandHistogramsSize), Prefix.NumCommandSymbols, tree, ref storageIx, storage);
        BuildAndStoreEntropyCodes(ref distanceEnc, arena, 2, mb.DistanceHistograms.AsSpan(0, (int)mb.DistanceHistogramsSize), numDistanceSymbols, tree, ref storageIx, storage);
        var sink = new BitSink(storageIx, storage);
        for (int i = 0; i < commands.Length; ++i)
        {
            ref readonly Command cmd = ref commands[i];
            StoreSymbol(ref commandEnc, cmd.CmdPrefix, ref sink);
            StoreCommandExtra(in cmd, ref sink);
            if (mb.LiteralContextMapSize == 0)
            {
                for (nuint j = cmd.InsertLen; j != 0; --j)
                {
                    StoreSymbol(ref literalEnc, input[(int)(pos & mask)], ref sink);
                    ++pos;
                }
            }
            else
            {
                for (nuint j = cmd.InsertLen; j != 0; --j)
                {
                    nuint context = (nuint)(literalContextLut[prevByte] | literalContextLut[256 + prevByte2]);
                    byte literal = input[(int)(pos & mask)];
                    StoreSymbolWithContext(ref literalEnc, literal, context, mb.LiteralContextMap, ref sink, Prefix.LiteralContextBits);
                    prevByte2 = prevByte;
                    prevByte = literal;
                    ++pos;
                }
            }

            uint copyLen = Commands.CopyLen(in cmd);
            pos += copyLen;
            if (copyLen != 0)
            {
                prevByte2 = input[(int)((pos - 2) & mask)];
                prevByte = input[(int)((pos - 1) & mask)];
                if (cmd.CmdPrefix >= 128)
                {
                    nuint distCode = (nuint)(cmd.DistPrefix & 0x3FF);
                    int distNumExtra = cmd.DistPrefix >> 10;
                    ulong distExtra = cmd.DistExtra;
                    if (mb.DistanceContextMapSize == 0)
                    {
                        StoreSymbol(ref distanceEnc, distCode, ref sink);
                    }
                    else
                    {
                        nuint context = Commands.DistanceContext(in cmd);
                        StoreSymbolWithContext(ref distanceEnc, distCode, context, mb.DistanceContextMap, ref sink, Prefix.DistanceContextBits);
                    }

                    sink.Write(distNumExtra, distExtra);
                }
            }
        }

        storageIx = sink.Finish();
        if (isLast)
        {
            BitWriter.JumpToByteBoundary(ref storageIx, storage);
        }
    }

    /// <summary>brotli's <c>BuildHistograms</c>.</summary>
    private static void BuildHistograms(ReadOnlySpan<byte> input, nuint startPos, nuint mask, ReadOnlySpan<Command> commands, ref HistogramLiteral litHisto, ref HistogramCommand cmdHisto, ref HistogramDistance distHisto)
    {
        nuint pos = startPos;
        for (int i = 0; i < commands.Length; ++i)
        {
            ref readonly Command cmd = ref commands[i];
            Histogram.Add(ref cmdHisto, cmd.CmdPrefix);
            for (nuint j = cmd.InsertLen; j != 0; --j)
            {
                Histogram.Add(ref litHisto, input[(int)(pos & mask)]);
                ++pos;
            }

            uint copyLen = Commands.CopyLen(in cmd);
            pos += copyLen;
            if (copyLen != 0 && cmd.CmdPrefix >= 128)
            {
                Histogram.Add(ref distHisto, (nuint)(cmd.DistPrefix & 0x3FF));
            }
        }
    }

    /// <summary>brotli's <c>StoreDataWithHuffmanCodes</c>: the commands, literals and distances, a code each.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StoreDataWithHuffmanCodes(ReadOnlySpan<byte> input, nuint startPos, nuint mask, ReadOnlySpan<Command> commands, ReadOnlySpan<byte> litDepth, ReadOnlySpan<ushort> litBits, ReadOnlySpan<byte> cmdDepth, ReadOnlySpan<ushort> cmdBits, ReadOnlySpan<byte> distDepth, ReadOnlySpan<ushort> distBits, ref int storageIx, Span<byte> storage)
    {
        nuint pos = startPos;

        // The literal codes combined, bits and depth from bit 16: one load a literal, and three literals, of 15
        // bits at most each, one write.
        Span<uint> litCodes = stackalloc uint[256];
        BitWriter.CombineCodes(litDepth, litBits, litCodes, 256);
        var sink = new BitSink(storageIx, storage);
        for (int i = 0; i < commands.Length; ++i)
        {
            ref readonly Command cmd = ref commands[i];
            int cmdCode = cmd.CmdPrefix;
            sink.Write(cmdDepth[cmdCode], cmdBits[cmdCode]);
            StoreCommandExtra(in cmd, ref sink);
            nuint j = cmd.InsertLen;
            for (; j >= 3; j -= 3)
            {
                uint code = litCodes[input[(int)(pos & mask)]];
                ulong bits = code & 0xFFFF;
                int count = (int)(code >> 16);
                code = litCodes[input[(int)((pos + 1) & mask)]];
                bits |= (ulong)(code & 0xFFFF) << count;
                count += (int)(code >> 16);
                code = litCodes[input[(int)((pos + 2) & mask)]];
                bits |= (ulong)(code & 0xFFFF) << count;
                count += (int)(code >> 16);
                sink.Write(count, bits);
                pos += 3;
            }

            for (; j != 0; --j)
            {
                uint code = litCodes[input[(int)(pos & mask)]];
                sink.Write((int)(code >> 16), code & 0xFFFF);
                ++pos;
            }

            uint copyLen = Commands.CopyLen(in cmd);
            pos += copyLen;
            if (copyLen != 0 && cmd.CmdPrefix >= 128)
            {
                int distCode = cmd.DistPrefix & 0x3FF;
                int distNumExtra = cmd.DistPrefix >> 10;
                uint distExtra = cmd.DistExtra;
                int depth = distDepth[distCode];
                sink.Write(depth + distNumExtra, distBits[distCode] | ((ulong)distExtra << depth));
            }
        }

        storageIx = sink.Finish();
    }

    /// <summary>brotli's <c>BrotliStoreMetaBlockTrivial</c>: quality 3, a code a category.</summary>
    public static void StoreMetaBlockTrivial(MetaBlockStorage arena, ReadOnlySpan<byte> input, nuint startPos, int length, nuint mask, bool isLast, in DistanceParams dist, ReadOnlySpan<Command> commands, ref int storageIx, Span<byte> storage)
    {
        int numDistanceSymbols = (int)dist.AlphabetSizeMax;
        StoreCompressedMetaBlockHeader(isLast, length, ref storageIx, storage);
        Histogram.Clear(ref arena.LiteralHistogram);
        Histogram.Clear(ref arena.CommandHistogram);
        Histogram.Clear(ref arena.DistanceHistogram);
        BuildHistograms(input, startPos, mask, commands, ref arena.LiteralHistogram, ref arena.CommandHistogram, ref arena.DistanceHistogram);
        BitWriter.Write(13, 0, ref storageIx, storage);
        HuffmanTree[] tree = arena.Tree;
        BuildAndStoreHuffmanTree(arena.LiteralHistogram.Data, Prefix.NumLiteralSymbols, tree, arena.LiteralDepth, arena.LiteralBits, ref storageIx, storage);
        BuildAndStoreHuffmanTree(arena.CommandHistogram.Data, Prefix.NumCommandSymbols, tree, arena.CommandDepth, arena.CommandBits, ref storageIx, storage);
        BuildAndStoreHuffmanTree(arena.DistanceHistogram.Data.Slice(0, MaxSimpleDistanceAlphabetSize), numDistanceSymbols, tree, arena.DistanceDepth, arena.DistanceBits, ref storageIx, storage);
        StoreDataWithHuffmanCodes(input, startPos, mask, commands, arena.LiteralDepth, arena.LiteralBits, arena.CommandDepth, arena.CommandBits, arena.DistanceDepth, arena.DistanceBits, ref storageIx, storage);
        if (isLast)
        {
            BitWriter.JumpToByteBoundary(ref storageIx, storage);
        }
    }

    /// <summary>
    /// brotli's <c>BrotliStoreMetaBlockFast</c>: quality 2, fast codes, and the static codes of commands
    /// and distances for 128 commands or fewer.
    /// </summary>
    public static void StoreMetaBlockFast(MetaBlockStorage arena, ReadOnlySpan<byte> input, nuint startPos, int length, nuint mask, bool isLast, in DistanceParams dist, ReadOnlySpan<Command> commands, ref int storageIx, Span<byte> storage)
    {
        uint numDistanceSymbols = dist.AlphabetSizeMax;
        int distanceAlphabetBits = FastLog.Log2FloorNonZero(numDistanceSymbols - 1) + 1;
        StoreCompressedMetaBlockHeader(isLast, length, ref storageIx, storage);
        BitWriter.Write(13, 0, ref storageIx, storage);
        HuffmanTree[] tree = arena.Tree;
        if (commands.Length <= 128)
        {
            Span<uint> histogram = arena.LiteralHistogram.Data;
            histogram.Clear();
            nuint pos = startPos;
            nuint numLiterals = 0;
            for (int i = 0; i < commands.Length; ++i)
            {
                ref readonly Command cmd = ref commands[i];
                for (nuint j = cmd.InsertLen; j != 0; --j)
                {
                    ++histogram[input[(int)(pos & mask)]];
                    ++pos;
                }

                numLiterals += cmd.InsertLen;
                pos += Commands.CopyLen(in cmd);
            }

            BuildAndStoreHuffmanTreeFast(tree, histogram, numLiterals, 8, arena.LiteralDepth, arena.LiteralBits, ref storageIx, storage);

            // brotli's StoreStaticCommandHuffmanTree and StoreStaticDistanceHuffmanTree.
            BitWriter.Write(56, 0x0092624416307003, ref storageIx, storage);
            BitWriter.Write(3, 0, ref storageIx, storage);
            BitWriter.Write(28, 0x0369DC03, ref storageIx, storage);
            StoreDataWithHuffmanCodes(input, startPos, mask, commands, arena.LiteralDepth, arena.LiteralBits, EncoderTables.StaticCommandCodeDepth, EncoderTables.StaticCommandCodeBits, EncoderTables.StaticDistanceCodeDepth, EncoderTables.StaticDistanceCodeBits, ref storageIx, storage);
        }
        else
        {
            Histogram.Clear(ref arena.LiteralHistogram);
            Histogram.Clear(ref arena.CommandHistogram);
            Histogram.Clear(ref arena.DistanceHistogram);
            BuildHistograms(input, startPos, mask, commands, ref arena.LiteralHistogram, ref arena.CommandHistogram, ref arena.DistanceHistogram);
            BuildAndStoreHuffmanTreeFast(tree, arena.LiteralHistogram.Data, arena.LiteralHistogram.TotalCount, 8, arena.LiteralDepth, arena.LiteralBits, ref storageIx, storage);
            BuildAndStoreHuffmanTreeFast(tree, arena.CommandHistogram.Data, arena.CommandHistogram.TotalCount, 10, arena.CommandDepth, arena.CommandBits, ref storageIx, storage);
            BuildAndStoreHuffmanTreeFast(tree, arena.DistanceHistogram.Data, arena.DistanceHistogram.TotalCount, distanceAlphabetBits, arena.DistanceDepth, arena.DistanceBits, ref storageIx, storage);
            StoreDataWithHuffmanCodes(input, startPos, mask, commands, arena.LiteralDepth, arena.LiteralBits, arena.CommandDepth, arena.CommandBits, arena.DistanceDepth, arena.DistanceBits, ref storageIx, storage);
        }

        if (isLast)
        {
            BitWriter.JumpToByteBoundary(ref storageIx, storage);
        }
    }

    /// <summary>brotli's <c>BROTLI_CONTEXT_LUT</c>: the context lookup table of a literal context mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> ContextLut(int mode) => Context.Lookup.Slice(mode << 9, 512);
}

/// <summary>The memory of the meta-block storage functions: brotli's arenas, kept.</summary>
internal sealed class MetaBlockStorage
{
    private const int ContextMapSymbols = Prefix.MaxBlockTypes + 16;

    private readonly GrowableBuffer<uint> _rleSymbols = new();
    private readonly GrowableBuffer<byte>[] _depths = [new(), new(), new()];
    private readonly GrowableBuffer<ushort>[] _bits = [new(), new(), new()];
    private readonly BitStream.BlockSwitchCodes[] _codes = [new(), new(), new()];

    public readonly HuffmanTree[] Tree = new HuffmanTree[BitStream.MaxHuffmanTreeSize];

    public HistogramLiteral LiteralHistogram;

    public HistogramCommand CommandHistogram;

    public HistogramDistance DistanceHistogram;

    public readonly uint[] ContextMapHistogram = new uint[ContextMapSymbols];
    public readonly byte[] ContextMapDepths = new byte[ContextMapSymbols + 16];
    public readonly ushort[] ContextMapBits = new ushort[ContextMapSymbols];
    public readonly byte[] LiteralDepth = new byte[Prefix.NumLiteralSymbols];
    public readonly ushort[] LiteralBits = new ushort[Prefix.NumLiteralSymbols];
    public readonly byte[] CommandDepth = new byte[Prefix.NumCommandSymbols];
    public readonly ushort[] CommandBits = new ushort[Prefix.NumCommandSymbols];
    public readonly byte[] DistanceDepth = new byte[HistogramDistance.Symbols];
    public readonly ushort[] DistanceBits = new ushort[HistogramDistance.Symbols];

    /// <summary>The block switch codes of the category <paramref name="category"/>.</summary>
    public BitStream.BlockSwitchCodes Codes(int category) => _codes[category];

    public uint[] RleSymbols(nuint size) => _rleSymbols.Ensure(size);

    public byte[] Depths(int category, nuint size) => _depths[category].Ensure(size);

    public ushort[] Bits(int category, nuint size) => _bits[category].Ensure(size);
}
