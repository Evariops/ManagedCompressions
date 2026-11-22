using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// The low level of brotli's format, as brotli writes it (<c>c/enc/brotli_bit_stream.c</c>): prefix
/// codes, meta-block headers, uncompressed meta-blocks.
/// </summary>
internal static unsafe partial class BitStream
{
    /// <summary>The order of the code length code's lengths in the stream.</summary>
    private static ReadOnlySpan<byte> StorageOrder => [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    /// <summary>The static code of the code length code's lengths: the codes of lengths 0 to 5.</summary>
    private static ReadOnlySpan<byte> HuffmanBitLengthHuffmanCodeSymbols => [0, 7, 3, 2, 1, 15];

    /// <summary>The static code of the code length code's lengths: the lengths of those codes.</summary>
    private static ReadOnlySpan<byte> HuffmanBitLengthHuffmanCodeBitLengths => [2, 4, 3, 2, 2, 4];

    /// <summary>brotli's <c>BrotliEncodeMlen</c>: MLEN - 1 in its nibbles, for 1 to 16 Mi bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EncodeMlen(int length, out ulong bits, out int numBits, out ulong nibblesBits)
    {
        int lg = length == 1 ? 1 : FastLog.Log2FloorNonZero((nuint)(uint)(length - 1)) + 1;
        int mnibbles = (lg < 16 ? 16 : lg + 3) / 4;
        nibblesBits = (ulong)(mnibbles - 4);
        numBits = mnibbles * 4;
        bits = (ulong)(length - 1);
    }

    /// <summary>brotli's <c>BrotliStoreUncompressedMetaBlockHeader</c>: never the last meta-block.</summary>
    public static void StoreUncompressedMetaBlockHeader(int length, ref int storageIx, byte* storage)
    {
        BitWriter.Write(1, 0, ref storageIx, storage);
        EncodeMlen(length, out ulong lenBits, out int numLenBits, out ulong nibblesBits);
        BitWriter.Write(2, nibblesBits, ref storageIx, storage);
        BitWriter.Write(numLenBits, lenBits, ref storageIx, storage);
        BitWriter.Write(1, 1, ref storageIx, storage);
    }

    /// <summary>
    /// brotli's <c>BrotliStoreUncompressedMetaBlock</c>: <paramref name="length"/> bytes of
    /// <paramref name="input"/> from <paramref name="position"/>, the ring buffer's masked; then the empty
    /// last meta-block if it is the final block.
    /// </summary>
    public static void StoreUncompressedMetaBlock(bool isFinalBlock, byte* input, nuint position, nuint mask, int length, ref int storageIx, byte* storage)
    {
        nuint maskedPos = position & mask;
        StoreUncompressedMetaBlockHeader(length, ref storageIx, storage);
        BitWriter.JumpToByteBoundary(ref storageIx, storage);
        if (maskedPos + (nuint)length > mask + 1)
        {
            int length1 = (int)(mask + 1 - maskedPos);
            Buffer.MemoryCopy(input + maskedPos, storage + (storageIx >> 3), length1, length1);
            storageIx += length1 << 3;
            length -= length1;
            maskedPos = 0;
        }

        Buffer.MemoryCopy(input + maskedPos, storage + (storageIx >> 3), length, length);
        storageIx += length << 3;
        BitWriter.PrepareStorage(storageIx, storage);
        if (isFinalBlock)
        {
            BitWriter.Write(1, 1, ref storageIx, storage);
            BitWriter.Write(1, 1, ref storageIx, storage);
            BitWriter.JumpToByteBoundary(ref storageIx, storage);
        }
    }

    /// <summary>
    /// brotli's <c>MakeUncompressedStream</c>, the fallback of <c>BrotliEncoderCompress</c>: the input in
    /// uncompressed meta-blocks of up to 16 MiB, in a window of 1 KiB. <paramref name="output"/> holds
    /// <see cref="MaxCompressedSize"/> bytes.
    /// </summary>
    public static int MakeUncompressedStream(byte* input, int inputSize, byte* output)
    {
        int size = inputSize;
        int result = 0;
        int offset = 0;
        if (inputSize == 0)
        {
            output[0] = 6;
            return 1;
        }

        // Window bits 10, not last; an empty metadata block, padding.
        output[result++] = 0x21;
        output[result++] = 0x03;
        while (size > 0)
        {
            uint nibbles = 0;
            uint chunkSize = size > (1 << 24) ? 1u << 24 : (uint)size;
            if (chunkSize > (1u << 16))
            {
                nibbles = chunkSize > (1u << 20) ? 2u : 1u;
            }

            uint bits = (nibbles << 1) | ((chunkSize - 1) << 3) | (1u << (int)(19 + (4 * nibbles)));
            output[result++] = (byte)bits;
            output[result++] = (byte)(bits >> 8);
            output[result++] = (byte)(bits >> 16);
            if (nibbles == 2)
            {
                output[result++] = (byte)(bits >> 24);
            }

            Buffer.MemoryCopy(input + offset, output + result, chunkSize, chunkSize);
            result += (int)chunkSize;
            offset += (int)chunkSize;
            size -= (int)chunkSize;
        }

        output[result++] = 3;
        return result;
    }

    /// <summary>brotli's <c>BrotliEncoderMaxCompressedSize</c>: 0 when it overflows.</summary>
    public static long MaxCompressedSize(long inputSize)
    {
        // The window bits or an empty metadata block, an uncompressed meta-block per 16 KiB, the last empty one.
        long numLargeBlocks = inputSize >> 14;
        long overhead = 2 + (4 * numLargeBlocks) + 3 + 1;
        long result = inputSize + overhead;
        if (inputSize == 0)
        {
            return 2;
        }

        return result < inputSize ? 0 : result;
    }

    private static void StoreHuffmanTreeOfHuffmanTreeToBitMask(int numCodes, byte* codeLengthBitDepth, ref int storageIx, byte* storage)
    {
        ReadOnlySpan<byte> order = StorageOrder;
        int skipSome = 0;
        int codesToStore = Prefix.NumCodeLengthCodes;
        if (numCodes > 1)
        {
            for (; codesToStore > 0; --codesToStore)
            {
                if (codeLengthBitDepth[order[codesToStore - 1]] != 0)
                {
                    break;
                }
            }
        }

        if (codeLengthBitDepth[order[0]] == 0 && codeLengthBitDepth[order[1]] == 0)
        {
            skipSome = 2;
            if (codeLengthBitDepth[order[2]] == 0)
            {
                skipSome = 3;
            }
        }

        BitWriter.Write(2, (ulong)skipSome, ref storageIx, storage);
        ReadOnlySpan<byte> lengths = HuffmanBitLengthHuffmanCodeBitLengths;
        ReadOnlySpan<byte> symbols = HuffmanBitLengthHuffmanCodeSymbols;
        for (int i = skipSome; i < codesToStore; ++i)
        {
            int l = codeLengthBitDepth[order[i]];
            BitWriter.Write(lengths[l], symbols[l], ref storageIx, storage);
        }
    }

    private static void StoreHuffmanTreeToBitMask(int huffmanTreeSize, byte* huffmanTree, byte* huffmanTreeExtraBits, byte* codeLengthBitDepth, ushort* codeLengthBitDepthSymbols, ref int storageIx, byte* storage)
    {
        for (int i = 0; i < huffmanTreeSize; ++i)
        {
            int ix = huffmanTree[i];
            BitWriter.Write(codeLengthBitDepth[ix], codeLengthBitDepthSymbols[ix], ref storageIx, storage);
            if (ix == Prefix.RepeatPreviousCodeLength)
            {
                BitWriter.Write(2, huffmanTreeExtraBits[i], ref storageIx, storage);
            }
            else if (ix == Prefix.RepeatZeroCodeLength)
            {
                BitWriter.Write(3, huffmanTreeExtraBits[i], ref storageIx, storage);
            }
        }
    }

    /// <summary>brotli's <c>StoreSimpleHuffmanTree</c>: 2 to 4 symbols, sorted by depth.</summary>
    public static void StoreSimpleHuffmanTree(byte* depths, nuint* symbols, int numSymbols, int maxBits, ref int storageIx, byte* storage)
    {
        BitWriter.Write(2, 1, ref storageIx, storage);
        BitWriter.Write(2, (ulong)(numSymbols - 1), ref storageIx, storage);
        for (int i = 0; i < numSymbols; i++)
        {
            for (int j = i + 1; j < numSymbols; j++)
            {
                if (depths[symbols[j]] < depths[symbols[i]])
                {
                    (symbols[j], symbols[i]) = (symbols[i], symbols[j]);
                }
            }
        }

        BitWriter.Write(maxBits, symbols[0], ref storageIx, storage);
        BitWriter.Write(maxBits, symbols[1], ref storageIx, storage);
        if (numSymbols >= 3)
        {
            BitWriter.Write(maxBits, symbols[2], ref storageIx, storage);
        }

        if (numSymbols == 4)
        {
            BitWriter.Write(maxBits, symbols[3], ref storageIx, storage);
            BitWriter.Write(1, depths[symbols[0]] == 1 ? 1u : 0u, ref storageIx, storage);
        }
    }

    /// <summary>
    /// brotli's <c>BrotliStoreHuffmanTree</c>: a complex prefix code, its depths RLE-coded with a code
    /// length code built for them. <paramref name="tree"/> holds 2 * 18 + 1 nodes.
    /// </summary>
    public static void StoreHuffmanTree(byte* depths, int num, HuffmanTree* tree, ref int storageIx, byte* storage)
    {
        byte* huffmanTree = stackalloc byte[Prefix.NumCommandSymbols];
        byte* huffmanTreeExtraBits = stackalloc byte[Prefix.NumCommandSymbols];
        int huffmanTreeSize = 0;
        byte* codeLengthBitDepth = stackalloc byte[Prefix.NumCodeLengthCodes];
        ushort* codeLengthBitDepthSymbols = stackalloc ushort[Prefix.NumCodeLengthCodes];
        uint* huffmanTreeHistogram = stackalloc uint[Prefix.NumCodeLengthCodes];
        new Span<byte>(codeLengthBitDepth, Prefix.NumCodeLengthCodes).Clear();
        new Span<uint>(huffmanTreeHistogram, Prefix.NumCodeLengthCodes).Clear();
        int numCodes = 0;
        int code = 0;

        EntropyEncode.WriteHuffmanTree(depths, num, ref huffmanTreeSize, huffmanTree, huffmanTreeExtraBits);
        for (int i = 0; i < huffmanTreeSize; ++i)
        {
            ++huffmanTreeHistogram[huffmanTree[i]];
        }

        for (int i = 0; i < Prefix.NumCodeLengthCodes; ++i)
        {
            if (huffmanTreeHistogram[i] != 0)
            {
                if (numCodes == 0)
                {
                    code = i;
                    numCodes = 1;
                }
                else if (numCodes == 1)
                {
                    numCodes = 2;
                    break;
                }
            }
        }

        EntropyEncode.CreateHuffmanTree(huffmanTreeHistogram, Prefix.NumCodeLengthCodes, 5, tree, codeLengthBitDepth);
        EntropyEncode.ConvertBitDepthsToSymbols(codeLengthBitDepth, Prefix.NumCodeLengthCodes, codeLengthBitDepthSymbols);
        StoreHuffmanTreeOfHuffmanTreeToBitMask(numCodes, codeLengthBitDepth, ref storageIx, storage);
        if (numCodes == 1)
        {
            codeLengthBitDepth[code] = 0;
        }

        StoreHuffmanTreeToBitMask(huffmanTreeSize, huffmanTree, huffmanTreeExtraBits, codeLengthBitDepth, codeLengthBitDepthSymbols, ref storageIx, storage);
    }

    /// <summary>
    /// brotli's <c>BrotliBuildAndStoreHuffmanTreeFast</c>: a code of at most 14 bits over a histogram of
    /// <paramref name="histogramTotal"/>, stored as a simple code or with the static code length code.
    /// <paramref name="tree"/> holds 2 * 256 + 1 nodes.
    /// </summary>
    public static void BuildAndStoreHuffmanTreeFast(HuffmanTree* tree, uint* histogram, nuint histogramTotal, int maxBits, byte* depth, ushort* bits, ref int storageIx, byte* storage)
    {
        // The symbols, the first four kept. Without a branch on the counts, which the processor mispredicts:
        // every symbol is written to the slot of the next one present, which keeps it only if it is present.
        int count = 0;
        nuint* symbols = stackalloc nuint[5];
        symbols[0] = symbols[1] = symbols[2] = symbols[3] = 0;
        int length = 0;
        nuint total = histogramTotal;
        while (total != 0)
        {
            uint value = histogram[length];
            symbols[Math.Min(count, 4)] = (nuint)length;
            count += value != 0 ? 1 : 0;
            total -= value;
            ++length;
        }

        if (count <= 1)
        {
            BitWriter.Write(4, 1, ref storageIx, storage);
            BitWriter.Write(maxBits, symbols[0], ref storageIx, storage);
            depth[symbols[0]] = 0;
            bits[symbols[0]] = 0;
            return;
        }

        new Span<byte>(depth, length).Clear();
        for (uint countLimit = 1; ; countLimit *= 2)
        {
            // A leaf for each symbol present, without a branch: each written, kept if present.
            HuffmanTree* node = tree;
            for (int l = length; l != 0;)
            {
                --l;
                uint value = histogram[l];
                *node = new HuffmanTree(Math.Max(value, countLimit), -1, (short)l);
                node += value != 0 ? 1 : 0;
            }

            int n = (int)(node - tree);
            var sentinel = new HuffmanTree(uint.MaxValue, -1, -1);
            int leaf = 0;
            int parent = n + 1;
            EntropyEncode.Sort<EntropyEncode.ByCount>(tree, n);
            *node++ = sentinel;
            *node++ = sentinel;
            for (int k = n - 1; k > 0; --k)
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

                node[-1].TotalCount = tree[left].TotalCount + tree[right].TotalCount;
                node[-1].IndexLeft = (short)left;
                node[-1].IndexRightOrValue = (short)right;
                *node++ = sentinel;
            }

            if (EntropyEncode.SetDepth((2 * n) - 1, tree, depth, 14))
            {
                break;
            }
        }

        EntropyEncode.ConvertBitDepthsToSymbols(depth, length, bits);
        if (count <= 4)
        {
            // A simple code: its symbols, sorted by depth.
            BitWriter.Write(2, 1, ref storageIx, storage);
            BitWriter.Write(2, (ulong)(count - 1), ref storageIx, storage);
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (depth[symbols[j]] < depth[symbols[i]])
                    {
                        (symbols[j], symbols[i]) = (symbols[i], symbols[j]);
                    }
                }
            }

            BitWriter.Write(maxBits, symbols[0], ref storageIx, storage);
            BitWriter.Write(maxBits, symbols[1], ref storageIx, storage);
            if (count >= 3)
            {
                BitWriter.Write(maxBits, symbols[2], ref storageIx, storage);
            }

            if (count == 4)
            {
                BitWriter.Write(maxBits, symbols[3], ref storageIx, storage);
                BitWriter.Write(1, depth[symbols[0]] == 1 ? 1u : 0u, ref storageIx, storage);
            }

            return;
        }

        // A complex code, its depths RLE-coded with the static code length code.
        byte previousValue = 8;
        StoreStaticCodeLengthCode(ref storageIx, storage);
        var sink = new BitSink(storageIx, storage);
        ref byte codeLengthDepth = ref MemoryMarshal.GetReference(EncoderTables.CodeLengthDepth);
        ref uint codeLengthBits = ref MemoryMarshal.GetReference(EncoderTables.CodeLengthBits);
        ref uint zeroRepsDepth = ref MemoryMarshal.GetReference(EncoderTables.ZeroRepsDepth);
        ref ulong zeroRepsBits = ref MemoryMarshal.GetReference(EncoderTables.ZeroRepsBits);
        ref uint nonZeroRepsDepth = ref MemoryMarshal.GetReference(EncoderTables.NonZeroRepsDepth);
        ref ulong nonZeroRepsBits = ref MemoryMarshal.GetReference(EncoderTables.NonZeroRepsBits);
        for (int i = 0; i < length;)
        {
            byte value = depth[i];
            int reps = 1;
            for (int k = i + 1; k < length && depth[k] == value; ++k)
            {
                ++reps;
            }

            i += reps;
            if (value == 0)
            {
                sink.Write((int)Unsafe.Add(ref zeroRepsDepth, reps), Unsafe.Add(ref zeroRepsBits, reps));
                continue;
            }

            if (previousValue != value)
            {
                sink.Write(Unsafe.Add(ref codeLengthDepth, value), Unsafe.Add(ref codeLengthBits, value));
                --reps;
            }

            if (reps < 3)
            {
                while (reps != 0)
                {
                    reps--;
                    sink.Write(Unsafe.Add(ref codeLengthDepth, value), Unsafe.Add(ref codeLengthBits, value));
                }
            }
            else
            {
                reps -= 3;
                sink.Write((int)Unsafe.Add(ref nonZeroRepsDepth, reps), Unsafe.Add(ref nonZeroRepsBits, reps));
            }

            previousValue = value;
        }

        storageIx = sink.Finish(storage);
    }

    /// <summary>brotli's <c>StoreStaticCodeLengthCode</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreStaticCodeLengthCode(ref int storageIx, byte* storage) =>
        BitWriter.Write(40, 0x0000FF55555554UL, ref storageIx, storage);
}
