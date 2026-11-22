using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>The scratch of quality 1, brotli's <c>BrotliTwoPassArena</c>, and its command and literal buffers.</summary>
internal sealed class TwoPassArena
{
    /// <summary>The size of the blocks quality 1 cuts its fragments into, brotli's <c>kCompressFragmentTwoPassBlockSize</c>.</summary>
    public const int BlockSize = 1 << 17;

    public readonly uint[] LitHisto = new uint[256];
    public readonly byte[] LitDepth = new byte[256];
    public readonly ushort[] LitBits = new ushort[256];
    public readonly uint[] CmdHisto = new uint[128];
    public readonly byte[] CmdDepth = new byte[128];
    public readonly ushort[] CmdBits = new ushort[128];
    public readonly HuffmanTree[] TmpTree = new HuffmanTree[(2 * 256) + 1];
    public readonly byte[] TmpDepth = new byte[Prefix.NumCommandSymbols];
    public readonly ushort[] TmpBits = new ushort[64 + 8];
    public readonly uint[] Commands = new uint[BlockSize];
    public readonly byte[] Literals = new byte[BlockSize];
}


/// <summary>
/// Quality 1, brotli's <c>BrotliCompressFragmentTwoPass</c> (<c>c/enc/compress_fragment_two_pass.c</c>):
/// a first pass gathers the commands and literals of a block, a second stores them with codes built
/// from their histograms.
/// </summary>
internal static class CompressFragmentTwoPass
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
    private static uint Hash(ReadOnlySpan<byte> data, int p, int shift, int length) =>
        (uint)(((BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p, 8)) << ((8 - length) * 8)) * HashMul32) >> shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashBytesAtOffset(ulong v, int offset, int shift, int length) =>
        (uint)((((v >> (8 * offset)) << ((8 - length) * 8)) * HashMul32) >> shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsMatch(ReadOnlySpan<byte> data, int p1, int p2, int length)
    {
        ReadOnlySpan<byte> a = data.Slice(p1, length);
        ReadOnlySpan<byte> b = data.Slice(p2, length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(a) == BinaryPrimitives.ReadUInt32LittleEndian(b))
        {
            return length == 4 || (a[4] == b[4] && a[5] == b[5]);
        }

        return false;
    }

    /// <summary>brotli's <c>BuildAndStoreCommandPrefixCode</c> of quality 1.</summary>
    private static void BuildAndStoreCommandPrefixCode(TwoPassArena s, ref int storageIx, Span<byte> storage)
    {
        Span<byte> depth = s.CmdDepth;
        Span<ushort> bits = s.CmdBits;
        Span<byte> tmpDepth = s.TmpDepth;
        Span<ushort> tmpBits = s.TmpBits;
        tmpDepth.Clear();
        EntropyEncode.CreateHuffmanTree(s.CmdHisto.AsSpan(0, 64), 15, s.TmpTree, depth);
        EntropyEncode.CreateHuffmanTree(s.CmdHisto.AsSpan(64, 64), 14, s.TmpTree, depth.Slice(64));

        // The command codes' bits, through the full alphabet's order.
        depth.Slice(24, 24).CopyTo(tmpDepth);
        depth.Slice(0, 8).CopyTo(tmpDepth.Slice(24));
        depth.Slice(48, 8).CopyTo(tmpDepth.Slice(32));
        depth.Slice(8, 8).CopyTo(tmpDepth.Slice(40));
        depth.Slice(56, 8).CopyTo(tmpDepth.Slice(48));
        depth.Slice(16, 8).CopyTo(tmpDepth.Slice(56));
        EntropyEncode.ConvertBitDepthsToSymbols(tmpDepth.Slice(0, 64), tmpBits);
        tmpBits.Slice(24, 8).CopyTo(bits);
        tmpBits.Slice(40, 8).CopyTo(bits.Slice(8));
        tmpBits.Slice(56, 8).CopyTo(bits.Slice(16));
        tmpBits.Slice(0, 24).CopyTo(bits.Slice(24));
        tmpBits.Slice(32, 8).CopyTo(bits.Slice(48));
        tmpBits.Slice(48, 8).CopyTo(bits.Slice(56));
        EntropyEncode.ConvertBitDepthsToSymbols(depth.Slice(64, 64), bits.Slice(64));

        // The depths over the full command alphabet.
        tmpDepth.Slice(0, 64).Clear();
        depth.Slice(24, 8).CopyTo(tmpDepth);
        depth.Slice(32, 8).CopyTo(tmpDepth.Slice(64));
        depth.Slice(40, 8).CopyTo(tmpDepth.Slice(128));
        depth.Slice(48, 8).CopyTo(tmpDepth.Slice(192));
        depth.Slice(56, 8).CopyTo(tmpDepth.Slice(384));
        for (int i = 0; i < 8; ++i)
        {
            tmpDepth[128 + (8 * i)] = depth[i];
            tmpDepth[256 + (8 * i)] = depth[8 + i];
            tmpDepth[448 + (8 * i)] = depth[16 + i];
        }

        BitStream.StoreHuffmanTree(tmpDepth.Slice(0, Prefix.NumCommandSymbols), s.TmpTree, ref storageIx, storage);
        BitStream.StoreHuffmanTree(depth.Slice(64, 64), s.TmpTree, ref storageIx, storage);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitInsertLen(uint insertLen, Span<uint> commands, ref int n)
    {
        if (insertLen < 6)
        {
            commands[n] = insertLen;
        }
        else if (insertLen < 130)
        {
            uint tail = insertLen - 2;
            int nbits = BitOperations.Log2(tail) - 1;
            uint prefix = tail >> nbits;
            uint code = ((uint)nbits << 1) + prefix + 2;
            uint extra = tail - (prefix << nbits);
            commands[n] = code | (extra << 8);
        }
        else if (insertLen < 2114)
        {
            uint tail = insertLen - 66;
            int nbits = BitOperations.Log2(tail);
            uint code = (uint)nbits + 10;
            uint extra = tail - (1u << nbits);
            commands[n] = code | (extra << 8);
        }
        else if (insertLen < 6210)
        {
            commands[n] = 21 | ((insertLen - 2114) << 8);
        }
        else if (insertLen < 22594)
        {
            commands[n] = 22 | ((insertLen - 6210) << 8);
        }
        else
        {
            commands[n] = 23 | ((insertLen - 22594) << 8);
        }

        ++n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitCopyLen(nuint copyLen, Span<uint> commands, ref int n)
    {
        if (copyLen < 10)
        {
            commands[n] = (uint)(copyLen + 38);
        }
        else if (copyLen < 134)
        {
            nuint tail = copyLen - 6;
            int nbits = FastLog.Log2FloorNonZero(tail) - 1;
            nuint prefix = tail >> nbits;
            nuint code = ((nuint)nbits << 1) + prefix + 44;
            nuint extra = tail - (prefix << nbits);
            commands[n] = (uint)(code | (extra << 8));
        }
        else if (copyLen < 2118)
        {
            nuint tail = copyLen - 70;
            int nbits = FastLog.Log2FloorNonZero(tail);
            nuint code = (nuint)nbits + 52;
            nuint extra = tail - ((nuint)1 << nbits);
            commands[n] = (uint)(code | (extra << 8));
        }
        else
        {
            commands[n] = (uint)(63 | ((copyLen - 2118) << 8));
        }

        ++n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitCopyLenLastDistance(nuint copyLen, Span<uint> commands, ref int n)
    {
        if (copyLen < 12)
        {
            commands[n++] = (uint)(copyLen + 20);
        }
        else if (copyLen < 72)
        {
            nuint tail = copyLen - 8;
            int nbits = FastLog.Log2FloorNonZero(tail) - 1;
            nuint prefix = tail >> nbits;
            nuint code = ((nuint)nbits << 1) + prefix + 28;
            nuint extra = tail - (prefix << nbits);
            commands[n++] = (uint)(code | (extra << 8));
        }
        else if (copyLen < 136)
        {
            nuint tail = copyLen - 8;
            nuint code = (tail >> 5) + 54;
            nuint extra = tail & 31;
            commands[n++] = (uint)(code | (extra << 8));
            commands[n++] = 64;
        }
        else if (copyLen < 2120)
        {
            nuint tail = copyLen - 72;
            int nbits = FastLog.Log2FloorNonZero(tail);
            nuint code = (nuint)nbits + 52;
            nuint extra = tail - ((nuint)1 << nbits);
            commands[n++] = (uint)(code | (extra << 8));
            commands[n++] = 64;
        }
        else
        {
            commands[n++] = (uint)(63 | ((copyLen - 2120) << 8));
            commands[n++] = 64;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitDistance(uint distance, Span<uint> commands, ref int n)
    {
        uint d = distance + 3;
        int nbits = BitOperations.Log2(d) - 1;
        uint prefix = (d >> nbits) & 1;
        uint offset = (2 + prefix) << nbits;
        uint distCode = (2 * ((uint)nbits - 1)) + prefix + 80;
        uint extra = d - offset;
        commands[n++] = distCode | (extra << 8);
    }

    /// <summary>The table's hashes of the positions within the copy that ends at <paramref name="ip"/>, then its candidate.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashCopyTail(ReadOnlySpan<byte> data, int ip, Span<int> table, int shift, int minMatch, int lastOffset)
    {
        ulong inputBytes;
        uint curHash;
        uint prevHash;
        if (minMatch == 4)
        {
            inputBytes = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(ip - 3, 8));
            curHash = HashBytesAtOffset(inputBytes, 3, shift, minMatch);
            prevHash = HashBytesAtOffset(inputBytes, 0, shift, minMatch);
            table[(int)prevHash] = ip - 3;
            prevHash = HashBytesAtOffset(inputBytes, 1, shift, minMatch);
            table[(int)prevHash] = ip - 2;

            // brotli hashes ip - 1 at offset 0 again, as if it were ip - 3, after a match with literals
            // (lastOffset 0), and at offset 2 after a match without (lastOffset 2).
            prevHash = HashBytesAtOffset(inputBytes, lastOffset, shift, minMatch);
            table[(int)prevHash] = ip - 1;
        }
        else
        {
            inputBytes = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(ip - 5, 8));
            prevHash = HashBytesAtOffset(inputBytes, 0, shift, minMatch);
            table[(int)prevHash] = ip - 5;
            prevHash = HashBytesAtOffset(inputBytes, 1, shift, minMatch);
            table[(int)prevHash] = ip - 4;
            prevHash = HashBytesAtOffset(inputBytes, 2, shift, minMatch);
            table[(int)prevHash] = ip - 3;
            inputBytes = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(ip - 2, 8));
            curHash = HashBytesAtOffset(inputBytes, 2, shift, minMatch);
            prevHash = HashBytesAtOffset(inputBytes, 0, shift, minMatch);
            table[(int)prevHash] = ip - 2;
            prevHash = HashBytesAtOffset(inputBytes, 1, shift, minMatch);
            table[(int)prevHash] = ip - 1;
        }

        int candidate = table[(int)curHash];
        table[(int)curHash] = ip;
        return candidate;
    }

    /// <summary>
    /// brotli's <c>CreateCommands</c>: the commands and literals of the block at <paramref name="input"/>,
    /// the first pass, appended to <paramref name="literals"/> and <paramref name="commands"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateCommands<TMinMatch>(ReadOnlySpan<byte> data, int input, int blockSize, int inputSize, Span<int> table, int tableBits, Span<byte> literals, ref int numLiteralsRef, Span<uint> commands, ref int numCommandsRef)
        where TMinMatch : struct, IMinMatch
    {
        // The output positions in locals, which the JIT keeps in registers; the minimal match a constant.
        int minMatch = TMinMatch.Value;
        int numLiterals = numLiteralsRef;
        int numCommands = numCommandsRef;
        int ip = input;
        int shift = 64 - tableBits;
        int ipEnd = input + blockSize;
        int nextEmit = input;
        int lastDistance = -1;
        if (blockSize >= InputMarginBytes)
        {
            // The last block keeps a margin of 16 bytes, so that every distance stays within the window
            // less 16; the others the minimal match, so that no copy goes past the block.
            int lenLimit = Math.Min(blockSize - minMatch, inputSize - InputMarginBytes);
            int ipLimit = input + lenLimit;
            uint nextHash = Hash(data, ++ip, shift, minMatch);
            while (true)
            {
                ip = FindMatch<TMinMatch>(data, ip, ipLimit, ref nextHash, table, shift, lastDistance, out int candidate);
                if (ip < 0)
                {
                    goto EmitRemainder;
                }

                {
                    int baseMatch = ip;
                    nuint matched = (nuint)minMatch + FindMatchLength.WithLimit(data, (nuint)(candidate + minMatch), (nuint)(ip + minMatch), (nuint)(ipEnd - ip) - (nuint)minMatch);
                    int distance = baseMatch - candidate;
                    int insert = baseMatch - nextEmit;
                    ip += (int)matched;
                    EmitInsertLen((uint)insert, commands, ref numCommands);
                    data.Slice(nextEmit, insert).CopyTo(literals.Slice(numLiterals));
                    numLiterals += insert;
                    if (distance == lastDistance)
                    {
                        commands[numCommands++] = 64;
                    }
                    else
                    {
                        EmitDistance((uint)distance, commands, ref numCommands);
                        lastDistance = distance;
                    }

                    EmitCopyLenLastDistance(matched, commands, ref numCommands);
                    nextEmit = ip;
                    if (ip >= ipLimit)
                    {
                        goto EmitRemainder;
                    }

                    candidate = HashCopyTail(data, ip, table, shift, minMatch, 0);
                }

                while (ip - candidate <= MaxDistance && IsMatch(data, ip, candidate, minMatch))
                {
                    int baseMatch = ip;
                    nuint matched = (nuint)minMatch + FindMatchLength.WithLimit(data, (nuint)(candidate + minMatch), (nuint)(ip + minMatch), (nuint)(ipEnd - ip) - (nuint)minMatch);
                    ip += (int)matched;
                    lastDistance = baseMatch - candidate;
                    EmitCopyLen(matched, commands, ref numCommands);
                    EmitDistance((uint)lastDistance, commands, ref numCommands);
                    nextEmit = ip;
                    if (ip >= ipLimit)
                    {
                        goto EmitRemainder;
                    }

                    candidate = HashCopyTail(data, ip, table, shift, minMatch, 2);
                }

                nextHash = Hash(data, ++ip, shift, minMatch);
            }
        }

    EmitRemainder:
        if (nextEmit < ipEnd)
        {
            int insert = ipEnd - nextEmit;
            EmitInsertLen((uint)insert, commands, ref numCommands);
            data.Slice(nextEmit, insert).CopyTo(literals.Slice(numLiterals));
            numLiterals += insert;
        }

        numLiteralsRef = numLiterals;
        numCommandsRef = numCommands;
    }

    /// <summary>brotli's <c>StoreCommands</c>: the codes from the histograms, then the commands and literals.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StoreCommands(TwoPassArena s, ReadOnlySpan<byte> literals, ReadOnlySpan<uint> commands, ref int storageIx, Span<byte> storage)
    {
        s.CmdDepth.AsSpan().Clear();
        s.CmdBits.AsSpan().Clear();
        s.CmdHisto.AsSpan().Clear();

        // The literals counted in four histograms interleaved, so that repeated bytes do not wait on one
        // another's increments, then summed.
        Span<uint> parts = stackalloc uint[4 * 256];
        int at = 0;
        for (; at + 4 <= literals.Length; at += 4)
        {
            ReadOnlySpan<byte> four = literals.Slice(at, 4);
            ++parts[four[0]];
            ++parts[256 + four[1]];
            ++parts[512 + four[2]];
            ++parts[768 + four[3]];
        }

        for (; at < literals.Length; ++at)
        {
            ++parts[literals[at]];
        }

        Span<uint> litHisto = s.LitHisto.AsSpan(0, 256);
        for (int i = 0; i < 256; ++i)
        {
            litHisto[i] = parts[i] + parts[256 + i] + parts[512 + i] + parts[768 + i];
        }

        BitStream.BuildAndStoreHuffmanTreeFast(s.TmpTree, litHisto, (nuint)literals.Length, 8, s.LitDepth, s.LitBits, ref storageIx, storage);
        Span<uint> cmdHisto = s.CmdHisto.AsSpan(0, 128);
        for (int i = 0; i < commands.Length; ++i)
        {
            ++cmdHisto[(int)(commands[i] & 0xFF)];
        }

        cmdHisto[1] += 1;
        cmdHisto[2] += 1;
        cmdHisto[64] += 1;
        cmdHisto[84] += 1;
        BuildAndStoreCommandPrefixCode(s, ref storageIx, storage);

        // The codes combined: a literal's bits with its depth from bit 16; a command's bits, its depth from
        // bit 16 and its extra bits' count from bit 24. A symbol is then one load, a command one write with
        // its extra bits, and four literals, of 14 bits at most each, one write.
        ReadOnlySpan<byte> numExtraBits = NumExtraBits;
        ReadOnlySpan<uint> insertOffset = InsertOffset;
        Span<uint> litCodes = stackalloc uint[256];
        Span<uint> cmdCodes = stackalloc uint[128];
        BitWriter.CombineCodes(s.LitDepth, s.LitBits, litCodes, 256);
        BitWriter.CombineCodes(s.CmdDepth, s.CmdBits, cmdCodes, 128);
        for (int i = 0; i < 128; ++i)
        {
            cmdCodes[i] |= (uint)numExtraBits[i] << 24;
        }

        var sink = new BitSink(storageIx, storage);
        int literal = 0;
        for (int i = 0; i < commands.Length; ++i)
        {
            uint cmd = commands[i];
            uint code = cmd & 0xFF;
            uint extra = cmd >> 8;
            uint combined = cmdCodes[(int)code];
            int depth = (int)((combined >> 16) & 0xFF);
            sink.Write(depth + (int)(combined >> 24), (combined & 0xFFFF) | ((ulong)extra << depth));
            if (code < 24)
            {
                int end = literal + (int)(insertOffset[(int)code] + extra);
                for (; literal + 4 <= end; literal += 4)
                {
                    ReadOnlySpan<byte> four = literals.Slice(literal, 4);
                    uint lit = litCodes[four[0]];
                    ulong bits = lit & 0xFFFF;
                    int count = (int)(lit >> 16);
                    lit = litCodes[four[1]];
                    bits |= (ulong)(lit & 0xFFFF) << count;
                    count += (int)(lit >> 16);
                    lit = litCodes[four[2]];
                    bits |= (ulong)(lit & 0xFFFF) << count;
                    count += (int)(lit >> 16);
                    lit = litCodes[four[3]];
                    bits |= (ulong)(lit & 0xFFFF) << count;
                    count += (int)(lit >> 16);
                    sink.Write(count, bits);
                }

                for (; literal < end; literal++)
                {
                    uint lit = litCodes[literals[literal]];
                    sink.Write((int)(lit >> 16), lit & 0xFFFF);
                }
            }
        }

        storageIx = sink.Finish();
    }

    private static bool ShouldCompress(TwoPassArena s, ReadOnlySpan<byte> input, int numLiterals)
    {
        double corpusSize = input.Length;
        if (numLiterals < MinRatio * corpusSize)
        {
            return true;
        }

        double maxTotalBitCost = corpusSize * 8 * MinRatio / SampleRate;
        Span<uint> histo = s.LitHisto.AsSpan(0, 256);
        histo.Clear();
        for (int i = 0; i < input.Length; i += SampleRate)
        {
            ++histo[input[i]];
        }

        return FastLog.BitsEntropy(histo) < maxTotalBitCost;
    }

    private static void EmitUncompressedMetaBlock(ReadOnlySpan<byte> input, ref int storageIx, Span<byte> storage)
    {
        CompressFragment.StoreMetaBlockHeader(input.Length, true, ref storageIx, storage);
        storageIx = (storageIx + 7) & ~7;
        input.CopyTo(storage.Slice(storageIx >> 3));
        storageIx += input.Length << 3;
        storage[storageIx >> 3] = 0;
    }

    /// <summary>
    /// brotli's <c>BrotliCompressFragmentTwoPass</c>: <paramref name="input"/> in blocks of
    /// <see cref="TwoPassArena.BlockSize"/>, then, if it is the last fragment, the empty last meta-block.
    /// <paramref name="table"/> holds <paramref name="tableSize"/> zeros, a power of two from 2^8 to 2^17.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Compress(TwoPassArena s, ReadOnlySpan<byte> input, bool isLast, Span<int> table, int tableSize, ref int storageIx, Span<byte> storage)
    {
        int initialStorageIx = storageIx;
        int tableBits = BitOperations.Log2((uint)tableSize);
        int minMatch = tableBits <= 15 ? 4 : 6;
        table = table.Slice(0, tableSize);
        int block = 0;
        int remaining = input.Length;
        while (remaining > 0)
        {
            int blockSize = Math.Min(remaining, TwoPassArena.BlockSize);
            int numLiterals = 0;
            int numCommands = 0;
            if (minMatch == 4)
            {
                CreateCommands<MinMatch4>(input, block, blockSize, remaining, table, tableBits, s.Literals, ref numLiterals, s.Commands, ref numCommands);
            }
            else
            {
                CreateCommands<MinMatch6>(input, block, blockSize, remaining, table, tableBits, s.Literals, ref numLiterals, s.Commands, ref numCommands);
            }

            if (ShouldCompress(s, input.Slice(block, blockSize), numLiterals))
            {
                CompressFragment.StoreMetaBlockHeader(blockSize, false, ref storageIx, storage);

                // No block splits, no contexts.
                BitWriter.Write(13, 0, ref storageIx, storage);
                StoreCommands(s, s.Literals.AsSpan(0, numLiterals), s.Commands.AsSpan(0, numCommands), ref storageIx, storage);
            }
            else
            {
                // Few matches and nearly 8 bits a byte: stored as it is, some 3 times faster.
                EmitUncompressedMetaBlock(input.Slice(block, blockSize), ref storageIx, storage);
            }

            block += blockSize;
            remaining -= blockSize;
        }

        // Larger than the fragment stored as it is: stored as it is.
        if (storageIx - initialStorageIx > 31 + (input.Length << 3))
        {
            BitWriter.Rewind(initialStorageIx, ref storageIx, storage);
            EmitUncompressedMetaBlock(input, ref storageIx, storage);
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
    /// every 32 bytes without one; -1 past <paramref name="ipLimit"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindMatch<TMinMatch>(ReadOnlySpan<byte> data, int ip, int ipLimit, ref uint nextHashRef, Span<int> table, int shift, int lastDistance, out int candidateOut)
        where TMinMatch : struct, IMinMatch
    {
        int minMatch = TMinMatch.Value;
        uint nextHash = nextHashRef;
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
                    nextHashRef = nextHash;
                    candidateOut = -1;
                    return -1;
                }

                nextHash = Hash(data, nextIp, shift, minMatch);
                candidate = ip - lastDistance;
                if (IsMatch(data, ip, candidate, minMatch) && candidate < ip)
                {
                    table[(int)hash] = ip;
                    break;
                }

                candidate = table[(int)hash];
                table[(int)hash] = ip;
            }
            while (!IsMatch(data, ip, candidate, minMatch));
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
