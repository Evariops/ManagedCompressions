using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// The low level of brotli's format, as brotli writes it (<c>c/enc/brotli_bit_stream.c</c>): prefix
/// codes, meta-block headers, uncompressed meta-blocks.
/// </summary>
internal static partial class BitStream
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
    public static void StoreUncompressedMetaBlockHeader(int length, ref int storageIx, Span<byte> storage)
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
    public static void StoreUncompressedMetaBlock(bool isFinalBlock, ReadOnlySpan<byte> input, nuint position, nuint mask, int length, ref int storageIx, Span<byte> storage)
    {
        nuint maskedPos = position & mask;
        StoreUncompressedMetaBlockHeader(length, ref storageIx, storage);
        BitWriter.JumpToByteBoundary(ref storageIx, storage);
        if (maskedPos + (nuint)length > mask + 1)
        {
            int length1 = (int)(mask + 1 - maskedPos);
            input.Slice((int)maskedPos, length1).CopyTo(storage.Slice(storageIx >> 3));
            storageIx += length1 << 3;
            length -= length1;
            maskedPos = 0;
        }

        input.Slice((int)maskedPos, length).CopyTo(storage.Slice(storageIx >> 3));
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
    public static int MakeUncompressedStream(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int inputSize = input.Length;
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

            input.Slice(offset, (int)chunkSize).CopyTo(output.Slice(result));
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

    private static void StoreHuffmanTreeOfHuffmanTreeToBitMask(int numCodes, Span<byte> codeLengthBitDepth, ref int storageIx, Span<byte> storage)
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

    private static void StoreHuffmanTreeToBitMask(int huffmanTreeSize, Span<byte> huffmanTree, Span<byte> huffmanTreeExtraBits, Span<byte> codeLengthBitDepth, Span<ushort> codeLengthBitDepthSymbols, ref int storageIx, Span<byte> storage)
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
    public static void StoreSimpleHuffmanTree(ReadOnlySpan<byte> depths, Span<nuint> symbols, int numSymbols, int maxBits, ref int storageIx, Span<byte> storage)
    {
        BitWriter.Write(2, 1, ref storageIx, storage);
        BitWriter.Write(2, (ulong)(numSymbols - 1), ref storageIx, storage);
        for (int i = 0; i < numSymbols; i++)
        {
            for (int j = i + 1; j < numSymbols; j++)
            {
                if (depths[(int)symbols[j]] < depths[(int)symbols[i]])
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
            BitWriter.Write(1, depths[(int)symbols[0]] == 1 ? 1u : 0u, ref storageIx, storage);
        }
    }

    /// <summary>
    /// brotli's <c>BrotliStoreHuffmanTree</c>: a complex prefix code, its depths RLE-coded with a code
    /// length code built for them. <paramref name="tree"/> holds 2 * 18 + 1 nodes.
    /// </summary>
    public static void StoreHuffmanTree(ReadOnlySpan<byte> depths, Span<HuffmanTree> tree, ref int storageIx, Span<byte> storage)
    {
        Span<byte> huffmanTree = stackalloc byte[Prefix.NumCommandSymbols];
        Span<byte> huffmanTreeExtraBits = stackalloc byte[Prefix.NumCommandSymbols];
        int huffmanTreeSize = 0;
        Span<byte> codeLengthBitDepth = stackalloc byte[Prefix.NumCodeLengthCodes];
        Span<ushort> codeLengthBitDepthSymbols = stackalloc ushort[Prefix.NumCodeLengthCodes];
        Span<uint> huffmanTreeHistogram = stackalloc uint[Prefix.NumCodeLengthCodes];
        int numCodes = 0;
        int code = 0;

        EntropyEncode.WriteHuffmanTree(depths, depths.Length, ref huffmanTreeSize, huffmanTree, huffmanTreeExtraBits);
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

        EntropyEncode.CreateHuffmanTree(huffmanTreeHistogram, 5, tree, codeLengthBitDepth);
        EntropyEncode.ConvertBitDepthsToSymbols(codeLengthBitDepth, codeLengthBitDepthSymbols);
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
    public static void BuildAndStoreHuffmanTreeFast(Span<HuffmanTree> tree, ReadOnlySpan<uint> histogram, nuint histogramTotal, int maxBits, Span<byte> depth, Span<ushort> bits, ref int storageIx, Span<byte> storage)
    {
        // The symbols, the first four kept. Without a branch on the counts, which the processor mispredicts:
        // every symbol is written to the slot of the next one present, which keeps it only if it is present.
        int count = 0;
        Span<nuint> symbols = stackalloc nuint[5];
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
            depth[(int)symbols[0]] = 0;
            bits[(int)symbols[0]] = 0;
            return;
        }

        depth.Slice(0, length).Clear();
        for (uint countLimit = 1; ; countLimit *= 2)
        {
            // A leaf for each symbol present, without a branch: each written, kept if present.
            int node = 0;
            for (int l = length; l != 0;)
            {
                --l;
                uint value = histogram[l];
                tree[node] = new HuffmanTree(Math.Max(value, countLimit), -1, (short)l);
                node += value != 0 ? 1 : 0;
            }

            int n = node;
            var sentinel = new HuffmanTree(uint.MaxValue, -1, -1);
            int leaf = 0;
            int parent = n + 1;
            EntropyEncode.Sort<EntropyEncode.ByCount>(tree, n);
            tree[node++] = sentinel;
            tree[node++] = sentinel;
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

                tree[node - 1].TotalCount = tree[left].TotalCount + tree[right].TotalCount;
                tree[node - 1].IndexLeft = (short)left;
                tree[node - 1].IndexRightOrValue = (short)right;
                tree[node++] = sentinel;
            }

            if (EntropyEncode.SetDepth((2 * n) - 1, tree, depth, 14))
            {
                break;
            }
        }

        EntropyEncode.ConvertBitDepthsToSymbols(depth.Slice(0, length), bits);
        if (count <= 4)
        {
            // A simple code: its symbols, sorted by depth.
            BitWriter.Write(2, 1, ref storageIx, storage);
            BitWriter.Write(2, (ulong)(count - 1), ref storageIx, storage);
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (depth[(int)symbols[j]] < depth[(int)symbols[i]])
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
                BitWriter.Write(1, depth[(int)symbols[0]] == 1 ? 1u : 0u, ref storageIx, storage);
            }

            return;
        }

        // A complex code, its depths RLE-coded with the static code length code.
        byte previousValue = 8;
        StoreStaticCodeLengthCode(ref storageIx, storage);
        var sink = new BitSink(storageIx, storage);
        ReadOnlySpan<byte> codeLengthDepth = EncoderTables.CodeLengthDepth;
        ReadOnlySpan<uint> codeLengthBits = EncoderTables.CodeLengthBits;
        ReadOnlySpan<uint> zeroRepsDepth = EncoderTables.ZeroRepsDepth;
        ReadOnlySpan<ulong> zeroRepsBits = EncoderTables.ZeroRepsBits;
        ReadOnlySpan<uint> nonZeroRepsDepth = EncoderTables.NonZeroRepsDepth;
        ReadOnlySpan<ulong> nonZeroRepsBits = EncoderTables.NonZeroRepsBits;
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
                sink.Write((int)zeroRepsDepth[reps], zeroRepsBits[reps]);
                continue;
            }

            if (previousValue != value)
            {
                sink.Write(codeLengthDepth[value], codeLengthBits[value]);
                --reps;
            }

            if (reps < 3)
            {
                while (reps != 0)
                {
                    reps--;
                    sink.Write(codeLengthDepth[value], codeLengthBits[value]);
                }
            }
            else
            {
                reps -= 3;
                sink.Write((int)nonZeroRepsDepth[reps], nonZeroRepsBits[reps]);
            }

            previousValue = value;
        }

        storageIx = sink.Finish();
    }

    /// <summary>brotli's <c>StoreStaticCodeLengthCode</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreStaticCodeLengthCode(ref int storageIx, Span<byte> storage) =>
        BitWriter.Write(40, 0x0000FF55555554UL, ref storageIx, storage);
}
