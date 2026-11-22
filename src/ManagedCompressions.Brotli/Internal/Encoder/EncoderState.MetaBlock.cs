using System;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>brotli's <c>WriteMetaBlockInternal</c> and the choice of the literals' static context map.</summary>
internal sealed partial class EncoderState
{
    /// <summary>brotli's <c>CONTEXT_UTF8</c>.</summary>
    private const int ContextModeUtf8 = 2;

    /// <summary>brotli's <c>CONTEXT_SIGNED</c>.</summary>
    private const int ContextModeSigned = 3;

    private static ReadOnlySpan<uint> StaticContextMapContinuation =>
    [
        1, 1, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    private static ReadOnlySpan<uint> StaticContextMapSimpleUtf8 =>
    [
        0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    private static ReadOnlySpan<uint> StaticContextMapComplexUtf8 =>
    [
        11, 11, 12, 12, // 0 special
        0, 0, 0, 0, // 4 lf
        1, 1, 9, 9, // 8 space
        2, 2, 2, 2, // !, first after space/lf and after something else.
        1, 1, 1, 1, // "
        8, 3, 3, 3, // %
        1, 1, 1, 1, // ({[
        2, 2, 2, 2, // }])
        8, 4, 4, 4, // :;
        8, 7, 4, 4, // .
        8, 0, 0, 0, // >
        3, 3, 3, 3, // [0..9]
        5, 5, 10, 5, // [A-Z]
        5, 5, 10, 5,
        6, 6, 6, 6, // [a-z]
        6, 6, 6, 6,
    ];

    /// <summary>brotli's <c>ShouldCompress</c>: false for blocks of mostly literals that look random.</summary>
    private static bool ShouldCompress(ReadOnlySpan<byte> data, nuint mask, ulong lastFlushPos, nuint bytes, nuint numLiterals, nuint numCommands)
    {
        if (bytes <= 2)
        {
            return false;
        }

        if (numCommands < (bytes >> 8) + 2 && (double)numLiterals > 0.99 * bytes)
        {
            Span<uint> literalHisto = stackalloc uint[256];
            const uint sampleRate = 13;
            const double minEntropy = 7.92;
            double bitCostThreshold = (double)bytes * minEntropy / sampleRate;
            nuint t = (bytes + sampleRate - 1) / sampleRate;
            uint pos = (uint)lastFlushPos;
            for (nuint i = 0; i < t; i++)
            {
                ++literalHisto[data[(int)(pos & mask)]];
                pos += sampleRate;
            }

            if (FastLog.BitsEntropy(literalHisto) > bitCostThreshold)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>brotli's <c>ChooseContextMap</c>: 1, 2 or 3 contexts, from the bigrams of the bytes' top two bits.</summary>
    private static void ChooseContextMap(int quality, ReadOnlySpan<uint> bigramHisto, out nuint numLiteralContexts, out ReadOnlySpan<uint> literalContextMap)
    {
        Span<uint> monogramHisto = stackalloc uint[3];
        Span<uint> twoPrefixHisto = stackalloc uint[6];
        Span<double> entropy = stackalloc double[4];
        for (int i = 0; i < 9; ++i)
        {
            monogramHisto[i % 3] += bigramHisto[i];
            twoPrefixHisto[i % 6] += bigramHisto[i];
        }

        entropy[1] = FastLog.ShannonEntropy(monogramHisto, out _);
        entropy[2] = FastLog.ShannonEntropy(twoPrefixHisto.Slice(0, 3), out _) + FastLog.ShannonEntropy(twoPrefixHisto.Slice(3, 3), out _);
        entropy[3] = 0;
        for (int i = 0; i < 3; ++i)
        {
            entropy[3] += FastLog.ShannonEntropy(bigramHisto.Slice(3 * i, 3), out _);
        }

        nuint total = (nuint)monogramHisto[0] + monogramHisto[1] + monogramHisto[2];
        entropy[0] = 1.0 / (double)total;
        entropy[1] *= entropy[0];
        entropy[2] *= entropy[0];
        entropy[3] *= entropy[0];
        if (quality < 7)
        {
            // Three contexts are a little slower: not below quality 7.
            entropy[3] = entropy[1] * 10;
        }

        // Context modeling only when it saves 0.2 bit a literal.
        if (entropy[1] - entropy[2] < 0.2 && entropy[1] - entropy[3] < 0.2)
        {
            numLiteralContexts = 1;
            literalContextMap = default;
        }
        else if (entropy[2] - entropy[3] < 0.02)
        {
            numLiteralContexts = 2;
            literalContextMap = StaticContextMapSimpleUtf8;
        }
        else
        {
            numLiteralContexts = 3;
            literalContextMap = StaticContextMapContinuation;
        }
    }

    /// <summary>
    /// brotli's <c>ShouldUseComplexStaticContextMap</c>: 13 contexts for inputs of 1 MiB and more, when
    /// the entropy of the literals' top five bits drops enough under them.
    /// </summary>
    private bool ShouldUseComplexStaticContextMap(ReadOnlySpan<byte> input, nuint startPos, nuint length, nuint mask, Span<uint> arena, ref nuint numLiteralContexts, ref ReadOnlySpan<uint> literalContextMap)
    {
        if (_sizeHint < (1 << 20))
        {
            return false;
        }

        nuint endPos = startPos + length;
        Span<uint> combinedHisto = arena.Slice(0, 32);
        Span<uint> contextHisto = arena.Slice(32, 32 * 13);
        uint total = 0;
        ReadOnlySpan<byte> utf8Lut = BitStream.ContextLut(ContextModeUtf8);
        ReadOnlySpan<uint> map = StaticContextMapComplexUtf8;
        arena.Slice(0, 32 * 14).Clear();
        for (; startPos + 64 <= endPos; startPos += 4096)
        {
            nuint strideEndPos = startPos + 64;
            byte prev2 = input[(int)(startPos & mask)];
            byte prev1 = input[(int)((startPos + 1) & mask)];
            for (nuint pos = startPos + 2; pos < strideEndPos; ++pos)
            {
                byte literal = input[(int)(pos & mask)];
                byte context = (byte)map[utf8Lut[prev1] | utf8Lut[256 + prev2]];
                ++total;
                ++combinedHisto[literal >> 3];
                ++contextHisto[(context << 5) + (literal >> 3)];
                prev2 = prev1;
                prev1 = literal;
            }
        }

        double entropy1 = FastLog.ShannonEntropy(combinedHisto, out _);
        double entropy2 = 0;
        for (int i = 0; i < 13; ++i)
        {
            entropy2 += FastLog.ShannonEntropy(contextHisto.Slice(i << 5, 32), out _);
        }

        double entropy0 = 1.0 / (double)total;
        entropy1 *= entropy0;
        entropy2 *= entropy0;
        if (entropy2 > 3.0 || entropy1 - entropy2 < 0.2)
        {
            return false;
        }

        numLiteralContexts = 13;
        literalContextMap = StaticContextMapComplexUtf8;
        return true;
    }

    /// <summary>brotli's <c>DecideOverLiteralContextModeling</c>, from quality 5.</summary>
    private void DecideOverLiteralContextModeling(ReadOnlySpan<byte> input, nuint startPos, nuint length, nuint mask, ref nuint numLiteralContexts, ref ReadOnlySpan<uint> literalContextMap)
    {
        if (_quality < 5 || length < 64)
        {
            return;
        }

        Span<uint> arena = _contextArena.Ensure(14 * 32);
        if (ShouldUseComplexStaticContextMap(input, startPos, length, mask, arena, ref numLiteralContexts, ref literalContextMap))
        {
            return;
        }

        // The bigrams of the bytes' top two bits, in 64-byte strides every 4 KiB.
        nuint endPos = startPos + length;
        Span<uint> bigramPrefixHisto = arena.Slice(0, 9);
        bigramPrefixHisto.Clear();
        ReadOnlySpan<int> lut = [0, 0, 1, 2];
        for (; startPos + 64 <= endPos; startPos += 4096)
        {
            nuint strideEndPos = startPos + 64;
            int prev = lut[input[(int)(startPos & mask)] >> 6] * 3;
            for (nuint pos = startPos + 1; pos < strideEndPos; ++pos)
            {
                byte literal = input[(int)(pos & mask)];
                ++bigramPrefixHisto[prev + lut[literal >> 6]];
                prev = lut[literal >> 6] * 3;
            }
        }

        ChooseContextMap(_quality, bigramPrefixHisto, out numLiteralContexts, out literalContextMap);
    }

    /// <summary>
    /// brotli's <c>WriteMetaBlockInternal</c>: the meta-block of <paramref name="bytes"/> bytes from
    /// <paramref name="lastFlushPos"/>, compressed, or stored when that is smaller or the data looks random.
    /// </summary>
    private void WriteMetaBlockInternal(ReadOnlySpan<byte> data, nuint mask, ulong lastFlushPos, uint bytes, bool isLast, int literalContextMode, Command[] commandArray, ref int storageIx, Span<byte> storage)
    {
        uint wrappedLastFlushPos = WrapPosition(lastFlushPos);
        nuint numLiterals = _numLiterals;
        nuint numCommands = _numCommands;
        Span<Command> commands = commandArray.AsSpan(0, (int)numCommands);
        if (bytes == 0)
        {
            // ISLAST and ISEMPTY.
            BitWriter.Write(2, 3, ref storageIx, storage);
            storageIx = (storageIx + 7) & ~7;
            return;
        }

        if (!ShouldCompress(data, mask, lastFlushPos, bytes, numLiterals, numCommands))
        {
            // The distance cache as it was, since the commands are dropped.
            _savedDistCache.AsSpan().CopyTo(_distCache);
            BitStream.StoreUncompressedMetaBlock(isLast, data, wrappedLastFlushPos, mask, (int)bytes, ref storageIx, storage);
            return;
        }

        ushort lastBytes = (ushort)((storage[1] << 8) | storage[0]);
        int lastBytesBits = storageIx;
        if (_quality <= 2)
        {
            BitStream.StoreMetaBlockFast(_metaBlockStorage, data, wrappedLastFlushPos, (int)bytes, mask, isLast, _dist, commands, ref storageIx, storage);
        }
        else if (_quality < 4)
        {
            BitStream.StoreMetaBlockTrivial(_metaBlockStorage, data, wrappedLastFlushPos, (int)bytes, mask, isLast, _dist, commands, ref storageIx, storage);
        }
        else
        {
            MetaBlockSplit mb = _mb;
            mb.Init();

            // The meta-block's own distance parameters, which qualities 10 and 11 choose.
            DistanceParams blockDist = _dist;
            if (_quality < 10)
            {
                nuint numLiteralContexts = 1;
                ReadOnlySpan<uint> literalContextMap = default;
                DecideOverLiteralContextModeling(data, wrappedLastFlushPos, bytes, mask, ref numLiteralContexts, ref literalContextMap);
                _greedy.Build(data, wrappedLastFlushPos, mask, _prevByte, _prevByte2, BitStream.ContextLut(literalContextMode), numLiteralContexts, literalContextMap, commands, mb);
            }
            else
            {
                (_hqBuilder ??= new HqMetaBlockBuilder()).Build(data, wrappedLastFlushPos, mask, _quality, ref blockDist, _prevByte, _prevByte2, commands, literalContextMode, mb);
            }

            _greedy.OptimizeHistograms(blockDist.AlphabetSizeLimit, mb);
            BitStream.StoreMetaBlock(_metaBlockStorage, data, wrappedLastFlushPos, (int)bytes, mask, _prevByte, _prevByte2, isLast, blockDist, literalContextMode, commands, mb, ref storageIx, storage);
        }

        if (bytes + 4 < (uint)(storageIx >> 3))
        {
            // Stored is smaller: the distance cache and the last byte as they were.
            _savedDistCache.AsSpan().CopyTo(_distCache);
            storage[0] = (byte)lastBytes;
            storage[1] = (byte)(lastBytes >> 8);
            storageIx = lastBytesBits;
            BitStream.StoreUncompressedMetaBlock(isLast, data, wrappedLastFlushPos, mask, (int)bytes, ref storageIx, storage);
        }
    }
}
