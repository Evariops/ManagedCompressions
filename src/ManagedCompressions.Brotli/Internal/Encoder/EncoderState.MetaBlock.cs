using System;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>brotli's <c>WriteMetaBlockInternal</c> and the choice of the literals' static context map.</summary>
internal sealed unsafe partial class EncoderState
{
    /// <summary>brotli's <c>CONTEXT_UTF8</c>.</summary>
    private const int ContextModeUtf8 = 2;

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
    private static bool ShouldCompress(byte* data, nuint mask, ulong lastFlushPos, nuint bytes, nuint numLiterals, nuint numCommands)
    {
        if (bytes <= 2)
        {
            return false;
        }

        if (numCommands < (bytes >> 8) + 2 && (double)numLiterals > 0.99 * bytes)
        {
            uint* literalHisto = stackalloc uint[256];
            new Span<uint>(literalHisto, 256).Clear();
            const uint sampleRate = 13;
            const double minEntropy = 7.92;
            double bitCostThreshold = (double)bytes * minEntropy / sampleRate;
            nuint t = (bytes + sampleRate - 1) / sampleRate;
            uint pos = (uint)lastFlushPos;
            for (nuint i = 0; i < t; i++)
            {
                ++literalHisto[data[pos & mask]];
                pos += sampleRate;
            }

            if (FastLog.BitsEntropy(literalHisto, 256) > bitCostThreshold)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>brotli's <c>ChooseContextMap</c>: 1, 2 or 3 contexts, from the bigrams of the bytes' top two bits.</summary>
    private static void ChooseContextMap(int quality, uint* bigramHisto, out nuint numLiteralContexts, out ReadOnlySpan<uint> literalContextMap)
    {
        uint* monogramHisto = stackalloc uint[3];
        uint* twoPrefixHisto = stackalloc uint[6];
        new Span<uint>(monogramHisto, 3).Clear();
        new Span<uint>(twoPrefixHisto, 6).Clear();
        double* entropy = stackalloc double[4];
        for (int i = 0; i < 9; ++i)
        {
            monogramHisto[i % 3] += bigramHisto[i];
            twoPrefixHisto[i % 6] += bigramHisto[i];
        }

        entropy[1] = FastLog.ShannonEntropy(monogramHisto, 3, out _);
        entropy[2] = FastLog.ShannonEntropy(twoPrefixHisto, 3, out _) + FastLog.ShannonEntropy(twoPrefixHisto + 3, 3, out _);
        entropy[3] = 0;
        for (int i = 0; i < 3; ++i)
        {
            entropy[3] += FastLog.ShannonEntropy(bigramHisto + (3 * i), 3, out _);
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
    private bool ShouldUseComplexStaticContextMap(byte* input, nuint startPos, nuint length, nuint mask, uint* arena, ref nuint numLiteralContexts, ref ReadOnlySpan<uint> literalContextMap)
    {
        if (_sizeHint < (1 << 20))
        {
            return false;
        }

        nuint endPos = startPos + length;
        uint* combinedHisto = arena;
        uint* contextHisto = arena + 32;
        uint total = 0;
        byte* utf8Lut = BitStream.ContextLut(ContextModeUtf8);
        ReadOnlySpan<uint> map = StaticContextMapComplexUtf8;
        new Span<uint>(arena, 32 * 14).Clear();
        for (; startPos + 64 <= endPos; startPos += 4096)
        {
            nuint strideEndPos = startPos + 64;
            byte prev2 = input[startPos & mask];
            byte prev1 = input[(startPos + 1) & mask];
            for (nuint pos = startPos + 2; pos < strideEndPos; ++pos)
            {
                byte literal = input[pos & mask];
                byte context = (byte)map[utf8Lut[prev1] | utf8Lut[256 + prev2]];
                ++total;
                ++combinedHisto[literal >> 3];
                ++contextHisto[(context << 5) + (literal >> 3)];
                prev2 = prev1;
                prev1 = literal;
            }
        }

        double entropy1 = FastLog.ShannonEntropy(combinedHisto, 32, out _);
        double entropy2 = 0;
        for (int i = 0; i < 13; ++i)
        {
            entropy2 += FastLog.ShannonEntropy(contextHisto + (i << 5), 32, out _);
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
    private void DecideOverLiteralContextModeling(byte* input, nuint startPos, nuint length, nuint mask, ref nuint numLiteralContexts, ref ReadOnlySpan<uint> literalContextMap)
    {
        if (_quality < 5 || length < 64)
        {
            return;
        }

        uint* arena = _contextArena.Ensure(14 * 32);
        if (ShouldUseComplexStaticContextMap(input, startPos, length, mask, arena, ref numLiteralContexts, ref literalContextMap))
        {
            return;
        }

        // The bigrams of the bytes' top two bits, in 64-byte strides every 4 KiB.
        nuint endPos = startPos + length;
        uint* bigramPrefixHisto = arena;
        new Span<uint>(bigramPrefixHisto, 9).Clear();
        ReadOnlySpan<int> lut = [0, 0, 1, 2];
        for (; startPos + 64 <= endPos; startPos += 4096)
        {
            nuint strideEndPos = startPos + 64;
            int prev = lut[input[startPos & mask] >> 6] * 3;
            for (nuint pos = startPos + 1; pos < strideEndPos; ++pos)
            {
                byte literal = input[pos & mask];
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
    private void WriteMetaBlockInternal(byte* data, nuint mask, ulong lastFlushPos, uint bytes, bool isLast, byte* literalContextLut, Command* commands, ref int storageIx, byte* storage)
    {
        uint wrappedLastFlushPos = WrapPosition(lastFlushPos);
        nuint numLiterals = _numLiterals;
        nuint numCommands = _numCommands;
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
            new Span<int>(_savedDistCache, 4).CopyTo(new Span<int>(_distCache, 4));
            BitStream.StoreUncompressedMetaBlock(isLast, data, wrappedLastFlushPos, mask, (int)bytes, ref storageIx, storage);
            return;
        }

        ushort lastBytes = (ushort)((storage[1] << 8) | storage[0]);
        int lastBytesBits = storageIx;
        if (_quality <= 2)
        {
            BitStream.StoreMetaBlockFast(_metaBlockStorage, data, wrappedLastFlushPos, (int)bytes, mask, isLast, _dist, commands, numCommands, ref storageIx, storage);
        }
        else if (_quality < 4)
        {
            BitStream.StoreMetaBlockTrivial(_metaBlockStorage, data, wrappedLastFlushPos, (int)bytes, mask, isLast, _dist, commands, numCommands, ref storageIx, storage);
        }
        else
        {
            MetaBlockSplit mb = _mb;
            mb.Init();
            nuint numLiteralContexts = 1;
            ReadOnlySpan<uint> literalContextMap = default;
            DecideOverLiteralContextModeling(data, wrappedLastFlushPos, bytes, mask, ref numLiteralContexts, ref literalContextMap);
            fixed (uint* map = literalContextMap)
            {
                _greedy.Build(data, wrappedLastFlushPos, mask, _prevByte, _prevByte2, literalContextLut, numLiteralContexts, map, commands, numCommands, mb);
            }

            _greedy.OptimizeHistograms(_dist.AlphabetSizeLimit, mb);
            BitStream.StoreMetaBlock(_metaBlockStorage, data, wrappedLastFlushPos, (int)bytes, mask, _prevByte, _prevByte2, isLast, _dist, ContextModeUtf8, commands, numCommands, mb, ref storageIx, storage);
        }

        if (bytes + 4 < (uint)(storageIx >> 3))
        {
            // Stored is smaller: the distance cache and the last byte as they were.
            new Span<int>(_savedDistCache, 4).CopyTo(new Span<int>(_distCache, 4));
            storage[0] = (byte)lastBytes;
            storage[1] = (byte)(lastBytes >> 8);
            storageIx = lastBytesBits;
            BitStream.StoreUncompressedMetaBlock(isLast, data, wrappedLastFlushPos, mask, (int)bytes, ref storageIx, storage);
        }
    }
}
