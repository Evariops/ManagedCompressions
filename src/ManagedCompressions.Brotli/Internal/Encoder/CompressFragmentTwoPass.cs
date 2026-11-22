using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>The scratch of quality 1, brotli's <c>BrotliTwoPassArena</c>, and its command and literal buffers.</summary>
internal sealed unsafe class TwoPassArena
{
    /// <summary>The size of the blocks quality 1 cuts its fragments into, brotli's <c>kCompressFragmentTwoPassBlockSize</c>.</summary>
    public const int BlockSize = 1 << 17;

    private const int LitHistoOffset = 0;
    private const int LitDepthOffset = LitHistoOffset + (256 * 4);
    private const int LitBitsOffset = LitDepthOffset + 256;
    private const int CmdHistoOffset = LitBitsOffset + (256 * 2);
    private const int CmdDepthOffset = CmdHistoOffset + (128 * 4);
    private const int CmdBitsOffset = CmdDepthOffset + 128;
    private const int TmpTreeOffset = CmdBitsOffset + (128 * 2);
    private const int TmpDepthOffset = TmpTreeOffset + (((2 * 256) + 1) * 8);
    private const int TmpBitsOffset = TmpDepthOffset + Prefix.NumCommandSymbols;
    private const int Size = TmpBitsOffset + (64 * 2) + 16;

    public readonly byte[] Buffer = GC.AllocateArray<byte>(Size, pinned: true);
    public readonly uint[] CommandArray = GC.AllocateUninitializedArray<uint>(BlockSize, pinned: true);
    public readonly byte[] LiteralArray = GC.AllocateUninitializedArray<byte>(BlockSize, pinned: true);

    public TwoPassArena()
    {
        byte* b = (byte*)Unsafe.AsPointer(ref Buffer[0]);
        LitHisto = (uint*)(b + LitHistoOffset);
        LitDepth = b + LitDepthOffset;
        LitBits = (ushort*)(b + LitBitsOffset);
        CmdHisto = (uint*)(b + CmdHistoOffset);
        CmdDepth = b + CmdDepthOffset;
        CmdBits = (ushort*)(b + CmdBitsOffset);
        TmpTree = (HuffmanTree*)(b + TmpTreeOffset);
        TmpDepth = b + TmpDepthOffset;
        TmpBits = (ushort*)(b + TmpBitsOffset);
        Commands = (uint*)Unsafe.AsPointer(ref CommandArray[0]);
        Literals = (byte*)Unsafe.AsPointer(ref LiteralArray[0]);
    }

    public readonly uint* LitHisto;
    public readonly byte* LitDepth;
    public readonly ushort* LitBits;
    public readonly uint* CmdHisto;
    public readonly byte* CmdDepth;
    public readonly ushort* CmdBits;
    public readonly HuffmanTree* TmpTree;
    public readonly byte* TmpDepth;
    public readonly ushort* TmpBits;
    public readonly uint* Commands;
    public readonly byte* Literals;
}

/// <summary>
/// Quality 1, brotli's <c>BrotliCompressFragmentTwoPass</c> (<c>c/enc/compress_fragment_two_pass.c</c>):
/// a first pass gathers the commands and literals of a block, a second stores them with codes built
/// from their histograms.
/// </summary>
internal static unsafe class CompressFragmentTwoPass
{
    private const long MaxDistance = (1L << 18) - Prefix.WindowGap;
    private const uint HashMul32 = 0x1E35A7BD;
    private const int InputMarginBytes = Prefix.WindowGap;

    /// <summary>Acceptable loss for an uncompressible block's speedup: 2 %.</summary>
    private const double MinRatio = 0.98;
    private const int SampleRate = 43;

    private static ReadOnlySpan<byte> NumExtraBits =>
    [
        0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5,
        6, 7, 8, 9, 10, 12, 14, 24, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 2, 2, 3, 3, 4, 4, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 24,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8,
        9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16,
        17, 17, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22, 23, 23, 24, 24,
    ];

    private static ReadOnlySpan<uint> InsertOffset =>
    [
        0, 1, 2, 3, 4, 5, 6, 8, 10, 14, 18, 26, 34, 50, 66, 98, 130, 194, 322, 578, 1090, 2114, 6210, 22594,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(byte* p, int shift, int length) =>
        (uint)(((Unsafe.ReadUnaligned<ulong>(p) << ((8 - length) * 8)) * HashMul32) >> shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashBytesAtOffset(ulong v, int offset, int shift, int length) =>
        (uint)((((v >> (8 * offset)) << ((8 - length) * 8)) * HashMul32) >> shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsMatch(byte* p1, byte* p2, int length)
    {
        if (Unsafe.ReadUnaligned<uint>(p1) == Unsafe.ReadUnaligned<uint>(p2))
        {
            return length == 4 || (p1[4] == p2[4] && p1[5] == p2[5]);
        }

        return false;
    }

    /// <summary>brotli's <c>BuildAndStoreCommandPrefixCode</c> of quality 1.</summary>
    private static void BuildAndStoreCommandPrefixCode(TwoPassArena s, ref int storageIx, byte* storage)
    {
        byte* depth = s.CmdDepth;
        ushort* bits = s.CmdBits;
        byte* tmpDepth = s.TmpDepth;
        ushort* tmpBits = s.TmpBits;
        new Span<byte>(tmpDepth, Prefix.NumCommandSymbols).Clear();
        EntropyEncode.CreateHuffmanTree(s.CmdHisto, 64, 15, s.TmpTree, depth);
        EntropyEncode.CreateHuffmanTree(s.CmdHisto + 64, 64, 14, s.TmpTree, depth + 64);

        // The command codes' bits, through the full alphabet's order.
        Buffer.MemoryCopy(depth + 24, tmpDepth, 24, 24);
        Buffer.MemoryCopy(depth, tmpDepth + 24, 8, 8);
        Buffer.MemoryCopy(depth + 48, tmpDepth + 32, 8, 8);
        Buffer.MemoryCopy(depth + 8, tmpDepth + 40, 8, 8);
        Buffer.MemoryCopy(depth + 56, tmpDepth + 48, 8, 8);
        Buffer.MemoryCopy(depth + 16, tmpDepth + 56, 8, 8);
        EntropyEncode.ConvertBitDepthsToSymbols(tmpDepth, 64, tmpBits);
        Buffer.MemoryCopy(tmpBits + 24, bits, 16, 16);
        Buffer.MemoryCopy(tmpBits + 40, bits + 8, 16, 16);
        Buffer.MemoryCopy(tmpBits + 56, bits + 16, 16, 16);
        Buffer.MemoryCopy(tmpBits, bits + 24, 48, 48);
        Buffer.MemoryCopy(tmpBits + 32, bits + 48, 16, 16);
        Buffer.MemoryCopy(tmpBits + 48, bits + 56, 16, 16);
        EntropyEncode.ConvertBitDepthsToSymbols(depth + 64, 64, bits + 64);

        // The depths over the full command alphabet.
        new Span<byte>(tmpDepth, 64).Clear();
        Buffer.MemoryCopy(depth + 24, tmpDepth, 8, 8);
        Buffer.MemoryCopy(depth + 32, tmpDepth + 64, 8, 8);
        Buffer.MemoryCopy(depth + 40, tmpDepth + 128, 8, 8);
        Buffer.MemoryCopy(depth + 48, tmpDepth + 192, 8, 8);
        Buffer.MemoryCopy(depth + 56, tmpDepth + 384, 8, 8);
        for (int i = 0; i < 8; ++i)
        {
            tmpDepth[128 + (8 * i)] = depth[i];
            tmpDepth[256 + (8 * i)] = depth[8 + i];
            tmpDepth[448 + (8 * i)] = depth[16 + i];
        }

        BitStream.StoreHuffmanTree(tmpDepth, Prefix.NumCommandSymbols, s.TmpTree, ref storageIx, storage);
        BitStream.StoreHuffmanTree(depth + 64, 64, s.TmpTree, ref storageIx, storage);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitInsertLen(uint insertLen, ref uint* commands)
    {
        if (insertLen < 6)
        {
            *commands = insertLen;
        }
        else if (insertLen < 130)
        {
            uint tail = insertLen - 2;
            int nbits = BitOperations.Log2(tail) - 1;
            uint prefix = tail >> nbits;
            uint code = ((uint)nbits << 1) + prefix + 2;
            uint extra = tail - (prefix << nbits);
            *commands = code | (extra << 8);
        }
        else if (insertLen < 2114)
        {
            uint tail = insertLen - 66;
            int nbits = BitOperations.Log2(tail);
            uint code = (uint)nbits + 10;
            uint extra = tail - (1u << nbits);
            *commands = code | (extra << 8);
        }
        else if (insertLen < 6210)
        {
            *commands = 21 | ((insertLen - 2114) << 8);
        }
        else if (insertLen < 22594)
        {
            *commands = 22 | ((insertLen - 6210) << 8);
        }
        else
        {
            *commands = 23 | ((insertLen - 22594) << 8);
        }

        ++commands;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitCopyLen(nuint copyLen, ref uint* commands)
    {
        if (copyLen < 10)
        {
            *commands = (uint)(copyLen + 38);
        }
        else if (copyLen < 134)
        {
            nuint tail = copyLen - 6;
            int nbits = FastLog.Log2FloorNonZero(tail) - 1;
            nuint prefix = tail >> nbits;
            nuint code = ((nuint)nbits << 1) + prefix + 44;
            nuint extra = tail - (prefix << nbits);
            *commands = (uint)(code | (extra << 8));
        }
        else if (copyLen < 2118)
        {
            nuint tail = copyLen - 70;
            int nbits = FastLog.Log2FloorNonZero(tail);
            nuint code = (nuint)nbits + 52;
            nuint extra = tail - ((nuint)1 << nbits);
            *commands = (uint)(code | (extra << 8));
        }
        else
        {
            *commands = (uint)(63 | ((copyLen - 2118) << 8));
        }

        ++commands;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitCopyLenLastDistance(nuint copyLen, ref uint* commands)
    {
        if (copyLen < 12)
        {
            *commands++ = (uint)(copyLen + 20);
        }
        else if (copyLen < 72)
        {
            nuint tail = copyLen - 8;
            int nbits = FastLog.Log2FloorNonZero(tail) - 1;
            nuint prefix = tail >> nbits;
            nuint code = ((nuint)nbits << 1) + prefix + 28;
            nuint extra = tail - (prefix << nbits);
            *commands++ = (uint)(code | (extra << 8));
        }
        else if (copyLen < 136)
        {
            nuint tail = copyLen - 8;
            nuint code = (tail >> 5) + 54;
            nuint extra = tail & 31;
            *commands++ = (uint)(code | (extra << 8));
            *commands++ = 64;
        }
        else if (copyLen < 2120)
        {
            nuint tail = copyLen - 72;
            int nbits = FastLog.Log2FloorNonZero(tail);
            nuint code = (nuint)nbits + 52;
            nuint extra = tail - ((nuint)1 << nbits);
            *commands++ = (uint)(code | (extra << 8));
            *commands++ = 64;
        }
        else
        {
            *commands++ = (uint)(63 | ((copyLen - 2120) << 8));
            *commands++ = 64;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitDistance(uint distance, ref uint* commands)
    {
        uint d = distance + 3;
        int nbits = BitOperations.Log2(d) - 1;
        uint prefix = (d >> nbits) & 1;
        uint offset = (2 + prefix) << nbits;
        uint distCode = (2 * ((uint)nbits - 1)) + prefix + 80;
        uint extra = d - offset;
        *commands++ = distCode | (extra << 8);
    }

    /// <summary>The table's hashes of the positions within the copy that ends at <paramref name="ip"/>, then its candidate.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* HashCopyTail(byte* ip, byte* baseIp, int* table, int shift, int minMatch, int lastOffset)
    {
        ulong inputBytes;
        uint curHash;
        uint prevHash;
        if (minMatch == 4)
        {
            inputBytes = Unsafe.ReadUnaligned<ulong>(ip - 3);
            curHash = HashBytesAtOffset(inputBytes, 3, shift, minMatch);
            prevHash = HashBytesAtOffset(inputBytes, 0, shift, minMatch);
            table[prevHash] = (int)(ip - baseIp - 3);
            prevHash = HashBytesAtOffset(inputBytes, 1, shift, minMatch);
            table[prevHash] = (int)(ip - baseIp - 2);

            // brotli hashes ip - 1 at offset 0 again, as if it were ip - 3, after a match with literals
            // (lastOffset 0), and at offset 2 after a match without (lastOffset 2).
            prevHash = HashBytesAtOffset(inputBytes, lastOffset, shift, minMatch);
            table[prevHash] = (int)(ip - baseIp - 1);
        }
        else
        {
            inputBytes = Unsafe.ReadUnaligned<ulong>(ip - 5);
            prevHash = HashBytesAtOffset(inputBytes, 0, shift, minMatch);
            table[prevHash] = (int)(ip - baseIp - 5);
            prevHash = HashBytesAtOffset(inputBytes, 1, shift, minMatch);
            table[prevHash] = (int)(ip - baseIp - 4);
            prevHash = HashBytesAtOffset(inputBytes, 2, shift, minMatch);
            table[prevHash] = (int)(ip - baseIp - 3);
            inputBytes = Unsafe.ReadUnaligned<ulong>(ip - 2);
            curHash = HashBytesAtOffset(inputBytes, 2, shift, minMatch);
            prevHash = HashBytesAtOffset(inputBytes, 0, shift, minMatch);
            table[prevHash] = (int)(ip - baseIp - 2);
            prevHash = HashBytesAtOffset(inputBytes, 1, shift, minMatch);
            table[prevHash] = (int)(ip - baseIp - 1);
        }

        byte* candidate = baseIp + table[curHash];
        table[curHash] = (int)(ip - baseIp);
        return candidate;
    }

    /// <summary>brotli's <c>CreateCommands</c>: the commands and literals of a block, the first pass.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateCommands<TMinMatch>(byte* input, int blockSize, int inputSize, byte* baseIp, int* table, int tableBits, ref byte* literalsRef, ref uint* commandsRef)
        where TMinMatch : struct, IMinMatch
    {
        // The output pointers in locals, which the JIT keeps in registers; the minimal match a constant.
        int minMatch = TMinMatch.Value;
        byte* literals = literalsRef;
        uint* commands = commandsRef;
        byte* ip = input;
        int shift = 64 - tableBits;
        byte* ipEnd = input + blockSize;
        byte* nextEmit = input;
        int lastDistance = -1;
        if (blockSize >= InputMarginBytes)
        {
            // The last block keeps a margin of 16 bytes, so that every distance stays within the window
            // less 16; the others the minimal match, so that no copy goes past the block.
            int lenLimit = Math.Min(blockSize - minMatch, inputSize - InputMarginBytes);
            byte* ipLimit = input + lenLimit;
            uint nextHash = Hash(++ip, shift, minMatch);
            while (true)
            {
                ip = FindMatch<TMinMatch>(ip, ipLimit, ref nextHash, table, baseIp, shift, lastDistance, out byte* candidate);
                if (ip == null)
                {
                    goto EmitRemainder;
                }

                {
                    byte* baseMatch = ip;
                    nuint matched = (nuint)minMatch + FindMatchLength.WithLimit(candidate + minMatch, ip + minMatch, (nuint)(ipEnd - ip) - (nuint)minMatch);
                    int distance = (int)(baseMatch - candidate);
                    int insert = (int)(baseMatch - nextEmit);
                    ip += matched;
                    EmitInsertLen((uint)insert, ref commands);
                    Buffer.MemoryCopy(nextEmit, literals, insert, insert);
                    literals += insert;
                    if (distance == lastDistance)
                    {
                        *commands++ = 64;
                    }
                    else
                    {
                        EmitDistance((uint)distance, ref commands);
                        lastDistance = distance;
                    }

                    EmitCopyLenLastDistance(matched, ref commands);
                    nextEmit = ip;
                    if (ip >= ipLimit)
                    {
                        goto EmitRemainder;
                    }

                    candidate = HashCopyTail(ip, baseIp, table, shift, minMatch, 0);
                }

                while (ip - candidate <= MaxDistance && IsMatch(ip, candidate, minMatch))
                {
                    byte* baseMatch = ip;
                    nuint matched = (nuint)minMatch + FindMatchLength.WithLimit(candidate + minMatch, ip + minMatch, (nuint)(ipEnd - ip) - (nuint)minMatch);
                    ip += matched;
                    lastDistance = (int)(baseMatch - candidate);
                    EmitCopyLen(matched, ref commands);
                    EmitDistance((uint)lastDistance, ref commands);
                    nextEmit = ip;
                    if (ip >= ipLimit)
                    {
                        goto EmitRemainder;
                    }

                    candidate = HashCopyTail(ip, baseIp, table, shift, minMatch, 2);
                }

                nextHash = Hash(++ip, shift, minMatch);
            }
        }

    EmitRemainder:
        if (nextEmit < ipEnd)
        {
            uint insert = (uint)(ipEnd - nextEmit);
            EmitInsertLen(insert, ref commands);
            Buffer.MemoryCopy(nextEmit, literals, insert, insert);
            literals += insert;
        }

        literalsRef = literals;
        commandsRef = commands;
    }

    /// <summary>brotli's <c>StoreCommands</c>: the codes from the histograms, then the commands and literals.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StoreCommands(TwoPassArena s, byte* literals, int numLiterals, uint* commands, int numCommands, ref int storageIx, byte* storage)
    {
        new Span<uint>(s.LitHisto, 256).Clear();
        new Span<byte>(s.CmdDepth, 128).Clear();
        new Span<ushort>(s.CmdBits, 128).Clear();
        new Span<uint>(s.CmdHisto, 128).Clear();
        for (int i = 0; i < numLiterals; ++i)
        {
            ++s.LitHisto[literals[i]];
        }

        BitStream.BuildAndStoreHuffmanTreeFast(s.TmpTree, s.LitHisto, (nuint)numLiterals, 8, s.LitDepth, s.LitBits, ref storageIx, storage);
        for (int i = 0; i < numCommands; ++i)
        {
            ++s.CmdHisto[commands[i] & 0xFF];
        }

        s.CmdHisto[1] += 1;
        s.CmdHisto[2] += 1;
        s.CmdHisto[64] += 1;
        s.CmdHisto[84] += 1;
        BuildAndStoreCommandPrefixCode(s, ref storageIx, storage);

        // The codes combined: a literal's bits with its depth from bit 16; a command's bits, its depth from
        // bit 16 and its extra bits' count from bit 24. A symbol is then one load, a command one write with
        // its extra bits, and four literals, of 14 bits at most each, one write.
        ReadOnlySpan<byte> numExtraBits = NumExtraBits;
        ReadOnlySpan<uint> insertOffset = InsertOffset;
        uint* litCodes = stackalloc uint[256];
        uint* cmdCodes = stackalloc uint[128];
        for (int i = 0; i < 256; ++i)
        {
            litCodes[i] = s.LitBits[i] | ((uint)s.LitDepth[i] << 16);
        }

        for (int i = 0; i < 128; ++i)
        {
            cmdCodes[i] = s.CmdBits[i] | ((uint)s.CmdDepth[i] << 16) | ((uint)numExtraBits[i] << 24);
        }

        var sink = new BitSink(storageIx, storage);
        for (int i = 0; i < numCommands; ++i)
        {
            uint cmd = commands[i];
            uint code = cmd & 0xFF;
            uint extra = cmd >> 8;
            uint combined = cmdCodes[code];
            int depth = (int)((combined >> 16) & 0xFF);
            sink.Write(depth + (int)(combined >> 24), (combined & 0xFFFF) | ((ulong)extra << depth));
            if (code < 24)
            {
                byte* end = literals + insertOffset[(int)code] + extra;
                for (; literals + 4 <= end; literals += 4)
                {
                    uint lit = litCodes[literals[0]];
                    ulong bits = lit & 0xFFFF;
                    int count = (int)(lit >> 16);
                    lit = litCodes[literals[1]];
                    bits |= (ulong)(lit & 0xFFFF) << count;
                    count += (int)(lit >> 16);
                    lit = litCodes[literals[2]];
                    bits |= (ulong)(lit & 0xFFFF) << count;
                    count += (int)(lit >> 16);
                    lit = litCodes[literals[3]];
                    bits |= (ulong)(lit & 0xFFFF) << count;
                    count += (int)(lit >> 16);
                    sink.Write(count, bits);
                }

                for (; literals < end; literals++)
                {
                    uint lit = litCodes[*literals];
                    sink.Write((int)(lit >> 16), lit & 0xFFFF);
                }
            }
        }

        storageIx = sink.Finish(storage);
    }

    private static bool ShouldCompress(TwoPassArena s, byte* input, int inputSize, int numLiterals)
    {
        double corpusSize = inputSize;
        if (numLiterals < MinRatio * corpusSize)
        {
            return true;
        }

        double maxTotalBitCost = corpusSize * 8 * MinRatio / SampleRate;
        new Span<uint>(s.LitHisto, 256).Clear();
        for (int i = 0; i < inputSize; i += SampleRate)
        {
            ++s.LitHisto[input[i]];
        }

        return FastLog.BitsEntropy(s.LitHisto, 256) < maxTotalBitCost;
    }

    private static void EmitUncompressedMetaBlock(byte* input, int inputSize, ref int storageIx, byte* storage)
    {
        CompressFragment.StoreMetaBlockHeader(inputSize, true, ref storageIx, storage);
        storageIx = (storageIx + 7) & ~7;
        Buffer.MemoryCopy(input, storage + (storageIx >> 3), inputSize, inputSize);
        storageIx += inputSize << 3;
        storage[storageIx >> 3] = 0;
    }

    /// <summary>
    /// brotli's <c>BrotliCompressFragmentTwoPass</c>: <paramref name="inputSize"/> bytes in blocks of
    /// <see cref="TwoPassArena.BlockSize"/>, then, if it is the last fragment, the empty last meta-block.
    /// <paramref name="table"/> holds <paramref name="tableSize"/> zeros, a power of two from 2^8 to 2^17.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Compress(TwoPassArena s, byte* input, int inputSize, bool isLast, int* table, int tableSize, ref int storageIx, byte* storage)
    {
        int initialStorageIx = storageIx;
        int tableBits = BitOperations.Log2((uint)tableSize);
        int minMatch = tableBits <= 15 ? 4 : 6;
        byte* baseIp = input;
        byte* block = input;
        int remaining = inputSize;
        while (remaining > 0)
        {
            int blockSize = Math.Min(remaining, TwoPassArena.BlockSize);
            uint* commands = s.Commands;
            byte* literals = s.Literals;
            if (minMatch == 4)
            {
                CreateCommands<MinMatch4>(block, blockSize, remaining, baseIp, table, tableBits, ref literals, ref commands);
            }
            else
            {
                CreateCommands<MinMatch6>(block, blockSize, remaining, baseIp, table, tableBits, ref literals, ref commands);
            }
            int numLiterals = (int)(literals - s.Literals);
            if (ShouldCompress(s, block, blockSize, numLiterals))
            {
                int numCommands = (int)(commands - s.Commands);
                CompressFragment.StoreMetaBlockHeader(blockSize, false, ref storageIx, storage);

                // No block splits, no contexts.
                BitWriter.Write(13, 0, ref storageIx, storage);
                StoreCommands(s, s.Literals, numLiterals, s.Commands, numCommands, ref storageIx, storage);
            }
            else
            {
                // Few matches and nearly 8 bits a byte: stored as it is, some 3 times faster.
                EmitUncompressedMetaBlock(block, blockSize, ref storageIx, storage);
            }

            block += blockSize;
            remaining -= blockSize;
        }

        // Larger than the fragment stored as it is: stored as it is.
        if (storageIx - initialStorageIx > 31 + (inputSize << 3))
        {
            BitWriter.Rewind(initialStorageIx, ref storageIx, storage);
            EmitUncompressedMetaBlock(input, inputSize, ref storageIx, storage);
        }

        if (isLast)
        {
            BitWriter.Write(1, 1, ref storageIx, storage);
            BitWriter.Write(1, 1, ref storageIx, storage);
            storageIx = (storageIx + 7) & ~7;
        }
    }

    /// <summary>
    /// The trawl of brotli's <c>CreateCommands</c>: the next position from <paramref name="ip"/> on whose
    /// bytes match those at the last distance or at the table's position, looking further apart after
    /// every 32 bytes without one; null past <paramref name="ipLimit"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* FindMatch<TMinMatch>(byte* ip, byte* ipLimit, ref uint nextHashRef, int* table, byte* baseIp, int shift, int lastDistance, out byte* candidateOut)
        where TMinMatch : struct, IMinMatch
    {
        int minMatch = TMinMatch.Value;
        uint nextHash = nextHashRef;
        uint skip = 32;
        byte* nextIp = ip;
        byte* candidate;
        do
        {
            do
            {
                uint hash = nextHash;
                uint bytesBetweenHashLookups = skip++ >> 5;
                ip = nextIp;
                nextIp = ip + bytesBetweenHashLookups;
                if (nextIp > ipLimit)
                {
                    nextHashRef = nextHash;
                    candidateOut = null;
                    return null;
                }

                nextHash = Hash(nextIp, shift, minMatch);
                candidate = ip - lastDistance;
                if (IsMatch(ip, candidate, minMatch) && candidate < ip)
                {
                    table[hash] = (int)(ip - baseIp);
                    break;
                }

                candidate = baseIp + table[hash];
                table[hash] = (int)(ip - baseIp);
            }
            while (!IsMatch(ip, candidate, minMatch));
        }

        // A candidate too far is no match: search on.
        while (ip - candidate > MaxDistance);

        nextHashRef = nextHash;
        candidateOut = candidate;
        return ip;
    }
}

/// <summary>The minimal match of quality 1, which its hash table's size sets: a type, so that it is a constant.</summary>
internal interface IMinMatch
{
    static abstract int Value { get; }
}

/// <summary>Matches of 4 bytes, for tables of 2^15 entries and less.</summary>
internal struct MinMatch4 : IMinMatch
{
    public static int Value => 4;
}

/// <summary>Matches of 6 bytes, for larger tables.</summary>
internal struct MinMatch6 : IMinMatch
{
    public static int Value => 6;
}
