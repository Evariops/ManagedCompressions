using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's block splitter of qualities 10 and 11 (<c>c/enc/block_splitter.c</c>,
/// <c>c/enc/block_splitter_inc.h</c>): entropy codes sampled and refined, the cheapest path through
/// them, and the blocks clustered into at most 256 types.
/// </summary>
internal sealed class BlockSplitter
{
    private const int MaxLiteralHistograms = 100;
    private const int MaxCommandHistograms = 50;
    private const double LiteralBlockSwitchCost = 28.1;
    private const double CommandBlockSwitchCost = 13.5;
    private const double DistanceBlockSwitchCost = 14.6;
    private const int LiteralStrideLength = 70;
    private const int CommandStrideLength = 40;
    private const int DistanceStrideLength = 40;
    private const int SymbolsPerLiteralHistogram = 544;
    private const int SymbolsPerCommandHistogram = 530;
    private const int SymbolsPerDistanceHistogram = 544;
    private const int MinLengthForBlockSplitting = 128;
    private const int IterMulForRefining = 2;
    private const int MinItersForRefining = 100;
    private const int HistogramsPerBatch = 64;
    private const int ClustersPerBatch = 16;

    private readonly GrowableBuffer<byte> _literals = new();
    private readonly GrowableBuffer<ushort> _codes = new();
    private readonly TypedBuffer _histograms = new();
    private readonly TypedBuffer _allHistograms = new();
    private readonly GrowableBuffer<byte> _blockIds = new();
    private readonly GrowableBuffer<double> _insertCost = new();
    private readonly GrowableBuffer<double> _cost = new();
    private readonly GrowableBuffer<byte> _switchSignal = new();
    private readonly GrowableBuffer<ushort> _newId = new();
    private readonly GrowableBuffer<uint> _histogramSymbols = new();
    private readonly GrowableBuffer<uint> _u32 = new();
    private readonly TypedBuffer _clusterSize = new();
    private readonly GrowableBuffer<uint> _clusters = new();
    private readonly GrowableBuffer<uint> _newIndex = new();
    private readonly GrowableBuffer<HistogramPair> _pairs = new();
    private readonly TypedBuffer _tmp = new();

    /// <summary>brotli's <c>BrotliSplitBlock</c>: the splits of the literals, the commands and the distances.</summary>
    public void SplitBlock(ReadOnlySpan<Command> cmds, ReadOnlySpan<byte> data, nuint pos, nuint mask, int quality, BlockSplit literalSplit, BlockSplit insertAndCopySplit, BlockSplit distSplit)
    {
        {
            nuint literalsCount = 0;
            for (int i = 0; i < cmds.Length; ++i)
            {
                literalsCount += cmds[i].InsertLen;
            }

            byte[] literals = _literals.Ensure(literalsCount);
            CopyLiteralsToByteArray(cmds, data, pos, mask, literals);
            SplitByteVector<HistogramLiteral, byte>(literals.AsSpan(0, (int)literalsCount), SymbolsPerLiteralHistogram, MaxLiteralHistograms, LiteralStrideLength, LiteralBlockSwitchCost, quality, literalSplit);
        }

        {
            ushort[] insertAndCopyCodes = _codes.Ensure((nuint)cmds.Length);
            for (int i = 0; i < cmds.Length; ++i)
            {
                insertAndCopyCodes[i] = cmds[i].CmdPrefix;
            }

            SplitByteVector<HistogramCommand, ushort>(insertAndCopyCodes.AsSpan(0, cmds.Length), SymbolsPerCommandHistogram, MaxCommandHistograms, CommandStrideLength, CommandBlockSwitchCost, quality, insertAndCopySplit);
        }

        {
            ushort[] distancePrefixes = _codes.Ensure((nuint)cmds.Length);
            int j = 0;
            for (int i = 0; i < cmds.Length; ++i)
            {
                ref readonly Command cmd = ref cmds[i];
                if (Commands.CopyLen(in cmd) != 0 && cmd.CmdPrefix >= 128)
                {
                    distancePrefixes[j++] = (ushort)(cmd.DistPrefix & 0x3FF);
                }
            }

            SplitByteVector<HistogramDistance, ushort>(distancePrefixes.AsSpan(0, j), SymbolsPerDistanceHistogram, MaxCommandHistograms, DistanceStrideLength, DistanceBlockSwitchCost, quality, distSplit);
        }
    }

    private static void CopyLiteralsToByteArray(ReadOnlySpan<Command> cmds, ReadOnlySpan<byte> data, nuint offset, nuint mask, Span<byte> literals)
    {
        int pos = 0;
        nuint fromPos = offset & mask;
        for (int i = 0; i < cmds.Length; ++i)
        {
            nuint insertLen = cmds[i].InsertLen;
            if (fromPos + insertLen > mask)
            {
                nuint headSize = mask + 1 - fromPos;
                data.Slice((int)fromPos, (int)headSize).CopyTo(literals.Slice(pos));
                fromPos = 0;
                pos += (int)headSize;
                insertLen -= headSize;
            }

            if (insertLen > 0)
            {
                data.Slice((int)fromPos, (int)insertLen).CopyTo(literals.Slice(pos));
                pos += (int)insertLen;
            }

            fromPos = (fromPos + insertLen + Commands.CopyLen(in cmds[i])) & mask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MyRand(ref uint seed)
    {
        seed *= 16807U;
        return seed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double BitCost(nuint count) => count == 0 ? -2.0 : FastLog.FastLog2(count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HistogramAddVector<THistogram, TData>(ref THistogram h, ReadOnlySpan<TData> p)
        where THistogram : struct, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        h.TotalCount += (nuint)p.Length;
        Span<uint> data = h.Data;
        for (int i = 0; i < p.Length; ++i)
        {
            ++data[int.CreateTruncating(p[i])];
        }
    }

    private static void InitialEntropyCodes<THistogram, TData>(ReadOnlySpan<TData> data, nuint stride, nuint numHistograms, Span<THistogram> histograms)
        where THistogram : struct, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        nuint length = (nuint)data.Length;
        uint seed = 7;
        nuint blockLength = length / numHistograms;
        Histogram.ClearAll(histograms.Slice(0, (int)numHistograms));
        for (nuint i = 0; i < numHistograms; ++i)
        {
            nuint pos = length * i / numHistograms;
            if (i != 0)
            {
                pos += MyRand(ref seed) % blockLength;
            }

            if (pos + stride >= length)
            {
                pos = length - stride - 1;
            }

            HistogramAddVector(ref histograms[(int)i], data.Slice((int)pos, (int)stride));
        }
    }

    private static void RandomSample<THistogram, TData>(ref uint seed, ReadOnlySpan<TData> data, nuint stride, ref THistogram sample)
        where THistogram : struct, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        nuint length = (nuint)data.Length;
        nuint pos = 0;
        if (stride >= length)
        {
            stride = length;
        }
        else
        {
            pos = MyRand(ref seed) % (length - stride + 1);
        }

        HistogramAddVector(ref sample, data.Slice((int)pos, (int)stride));
    }

    private static void RefineEntropyCodes<THistogram, TData>(ReadOnlySpan<TData> data, nuint stride, nuint numHistograms, Span<THistogram> histograms, ref THistogram tmp)
        where THistogram : struct, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        nuint length = (nuint)data.Length;
        nuint iters = (IterMulForRefining * length / stride) + MinItersForRefining;
        uint seed = 7;
        iters = (iters + numHistograms - 1) / numHistograms * numHistograms;
        for (nuint iter = 0; iter < iters; ++iter)
        {
            Histogram.Clear(ref tmp);
            RandomSample(ref seed, data, stride, ref tmp);
            Histogram.AddHistogram(ref histograms[(int)(iter % numHistograms)], ref tmp);
        }
    }

    /// <summary>brotli's <c>FindBlocks</c>: the cheapest assignment of the symbols to the codes, with block switches.</summary>
    private static nuint FindBlocks<THistogram, TData>(ReadOnlySpan<TData> data, double blockSwitchBitcost, nuint numHistograms, Span<THistogram> histograms, Span<double> insertCost, Span<double> cost, Span<byte> switchSignal, Span<byte> blockId)
        where THistogram : struct, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        int length = data.Length;
        int alphabetSize = THistogram.DataSize;
        int numCodes = (int)numHistograms;
        int bitmapLen = (numCodes + 7) >> 3;
        nuint numBlocks = 1;
        if (numHistograms <= 1)
        {
            blockId.Slice(0, length).Clear();
            return 1;
        }

        // The bits of each symbol in each code: 2 + log2(total) for a symbol a code lacks.
        insertCost = insertCost.Slice(0, alphabetSize * numCodes);
        insertCost.Clear();
        for (int i = 0; i < numCodes; ++i)
        {
            insertCost[i] = FastLog.FastLog2((uint)histograms[i].TotalCount);
        }

        for (int i = alphabetSize; i != 0;)
        {
            --i;
            for (int j = 0; j < numCodes; ++j)
            {
                insertCost[(i * numCodes) + j] = insertCost[j] - BitCost(histograms[j].Data[i]);
            }
        }

        cost = cost.Slice(0, numCodes);
        cost.Clear();
        switchSignal.Slice(0, length * bitmapLen).Clear();
        for (int byteIx = 0; byteIx < length; ++byteIx)
        {
            int ix = byteIx * bitmapLen;
            int symbol = int.CreateTruncating(data[byteIx]);
            int insertCostIx = symbol * numCodes;
            double blockSwitchCost = blockSwitchBitcost;

            // The symbol coded with each code; the cheapest code, the first of the cheapest.
            AddCosts(cost, insertCost.Slice(insertCostIx, numCodes));
            double minCost = 1e99;
            int best = FirstMinimum(cost, ref minCost);
            if (best >= 0)
            {
                blockId[byteIx] = (byte)best;
            }

            // More blocks at the beginning.
            if (byteIx < 2000)
            {
                blockSwitchCost *= 0.77 + (0.07 * (double)byteIx / 2000);
            }

            SubtractAndCap(cost, minCost, blockSwitchCost, switchSignal.Slice(ix, bitmapLen));
        }

        {
            // Back from the last position, switching at the marks.
            int byteIx = length - 1;
            int ix = byteIx * bitmapLen;
            byte curId = blockId[byteIx];
            while (byteIx > 0)
            {
                byte mask = (byte)(1u << (curId & 7));
                --byteIx;
                ix -= bitmapLen;
                if ((switchSignal[ix + (curId >> 3)] & mask) != 0)
                {
                    if (curId != blockId[byteIx])
                    {
                        curId = blockId[byteIx];
                        ++numBlocks;
                    }
                }

                blockId[byteIx] = curId;
            }
        }

        return numBlocks;
    }

    /// <summary><c>cost[k] += insert[k]</c> for each code: the same additions, a vector at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddCosts(Span<double> cost, ReadOnlySpan<double> insert)
    {
        int n = cost.Length;
        insert = insert.Slice(0, n);
        int k = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; k + 4 <= n; k += 4)
            {
                (Vector256.Create(cost.Slice(k, 4)) + Vector256.Create(insert.Slice(k, 4))).CopyTo(cost.Slice(k, 4));
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; k + 2 <= n; k += 2)
            {
                (Vector128.Create(cost.Slice(k, 2)) + Vector128.Create(insert.Slice(k, 2))).CopyTo(cost.Slice(k, 2));
            }
        }

        for (; k < n; ++k)
        {
            cost[k] += insert[k];
        }
    }

    /// <summary>
    /// The first code whose cost is below <paramref name="minCost"/> and the least, as brotli's loop of
    /// strict comparisons finds it, and its cost in <paramref name="minCost"/>; -1 when none is below.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FirstMinimum(ReadOnlySpan<double> cost, ref double minCost)
    {
        int n = cost.Length;
        double least = minCost;
        int k = 0;
        if (Vector256.IsHardwareAccelerated && n >= 4)
        {
            Vector256<double> min = Vector256.Create(least);
            for (; k + 4 <= n; k += 4)
            {
                min = Vector256.Min(min, Vector256.Create(cost.Slice(k, 4)));
            }

            least = Math.Min(Math.Min(min[0], min[1]), Math.Min(min[2], min[3]));
        }
        else if (Vector128.IsHardwareAccelerated && n >= 2)
        {
            Vector128<double> min = Vector128.Create(least);
            for (; k + 2 <= n; k += 2)
            {
                min = Vector128.Min(min, Vector128.Create(cost.Slice(k, 2)));
            }

            least = Math.Min(min[0], min[1]);
        }

        for (; k < n; ++k)
        {
            least = Math.Min(least, cost[k]);
        }

        // The first code at the least cost, if it is below the bound: the one the strict comparisons keep.
        if (!(least < minCost))
        {
            return -1;
        }

        for (k = 0; ; ++k)
        {
            if (cost[k] == least)
            {
                minCost = cost[k];
                return k;
            }
        }
    }

    /// <summary>
    /// <c>cost[k] -= minCost</c>, capped at <paramref name="blockSwitchCost"/>, the capped codes marked in
    /// <paramref name="switchSignal"/>'s bits, a vector at a time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SubtractAndCap(Span<double> cost, double minCost, double blockSwitchCost, Span<byte> switchSignal)
    {
        int n = cost.Length;
        int k = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<double> min = Vector256.Create(minCost);
            Vector256<double> cap = Vector256.Create(blockSwitchCost);
            for (; k + 8 <= n; k += 8)
            {
                Span<double> eight = cost.Slice(k, 8);
                Vector256<double> c0 = Vector256.Create((ReadOnlySpan<double>)eight.Slice(0, 4)) - min;
                Vector256<double> c1 = Vector256.Create((ReadOnlySpan<double>)eight.Slice(4, 4)) - min;
                Vector256<double> m0 = Vector256.GreaterThanOrEqual(c0, cap);
                Vector256<double> m1 = Vector256.GreaterThanOrEqual(c1, cap);
                Vector256.ConditionalSelect(m0, cap, c0).CopyTo(eight.Slice(0, 4));
                Vector256.ConditionalSelect(m1, cap, c1).CopyTo(eight.Slice(4, 4));
                switchSignal[k >> 3] |= (byte)(m0.ExtractMostSignificantBits() | (m1.ExtractMostSignificantBits() << 4));
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<double> min = Vector128.Create(minCost);
            Vector128<double> cap = Vector128.Create(blockSwitchCost);
            for (; k + 8 <= n; k += 8)
            {
                uint bits = 0;
                for (int i = 0; i < 8; i += 2)
                {
                    Span<double> two = cost.Slice(k + i, 2);
                    Vector128<double> c = Vector128.Create((ReadOnlySpan<double>)two) - min;
                    Vector128<double> m = Vector128.GreaterThanOrEqual(c, cap);
                    Vector128.ConditionalSelect(m, cap, c).CopyTo(two);
                    bits |= m.ExtractMostSignificantBits() << i;
                }

                switchSignal[k >> 3] |= (byte)bits;
            }
        }

        for (; k < n; ++k)
        {
            cost[k] -= minCost;
            if (cost[k] >= blockSwitchCost)
            {
                cost[k] = blockSwitchCost;
                switchSignal[k >> 3] |= (byte)(1u << (k & 7));
            }
        }
    }

    private static nuint RemapBlockIds(Span<byte> blockIds, Span<ushort> newId, nuint numHistograms)
    {
        const ushort invalidId = 256;
        ushort nextId = 0;
        newId.Slice(0, (int)numHistograms).Fill(invalidId);
        for (int i = 0; i < blockIds.Length; ++i)
        {
            if (newId[blockIds[i]] == invalidId)
            {
                newId[blockIds[i]] = nextId++;
            }
        }

        for (int i = 0; i < blockIds.Length; ++i)
        {
            blockIds[i] = (byte)newId[blockIds[i]];
        }

        return nextId;
    }

    private static void BuildBlockHistograms<THistogram, TData>(ReadOnlySpan<TData> data, ReadOnlySpan<byte> blockIds, nuint numHistograms, Span<THistogram> histograms)
        where THistogram : struct, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        Histogram.ClearAll(histograms.Slice(0, (int)numHistograms));
        for (int i = 0; i < data.Length; ++i)
        {
            Histogram.Add(ref histograms[blockIds[i]], nuint.CreateTruncating(data[i]));
        }
    }

    /// <summary>brotli's <c>ClusterBlocks</c>: the blocks' histograms clustered in batches, then together.</summary>
    private void ClusterBlocks<THistogram, TData>(ReadOnlySpan<TData> data, nuint numBlocks, ReadOnlySpan<byte> blockIds, BlockSplit split)
        where THistogram : struct, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        int length = data.Length;
        uint[] histogramSymbols = _histogramSymbols.Ensure(numBlocks);
        Span<uint> u32 = _u32.Ensure(numBlocks + (4 * HistogramsPerBatch)).AsSpan(0, (int)numBlocks + (4 * HistogramsPerBatch));
        nuint expectedNumClusters = ClustersPerBatch * (numBlocks + HistogramsPerBatch - 1) / HistogramsPerBatch;
        nuint allHistogramsSize = 0;
        nuint allHistogramsCapacity = expectedNumClusters;
        THistogram[] allHistograms = _allHistograms.Ensure<THistogram>(allHistogramsCapacity);
        nuint clusterSizeSize = 0;
        nuint clusterSizeCapacity = expectedNumClusters;
        uint[] clusterSize = _clusterSize.Ensure<uint>(clusterSizeCapacity);
        nuint numClusters = 0;
        THistogram[] histograms = _histograms.Ensure<THistogram>(Math.Min(numBlocks, HistogramsPerBatch));
        nuint maxNumPairs = HistogramsPerBatch * HistogramsPerBatch / 2;
        nuint pairsCapacity = maxNumPairs + 1;
        HistogramPair[] pairs = _pairs.Ensure(pairsCapacity);
        int pos = 0;
        const uint invalidIndex = uint.MaxValue;
        Span<uint> sizes = u32.Slice(0, HistogramsPerBatch);
        Span<uint> newClusters = u32.Slice(HistogramsPerBatch, HistogramsPerBatch);
        Span<uint> symbols = u32.Slice(2 * HistogramsPerBatch, HistogramsPerBatch);
        Span<uint> remap = u32.Slice(3 * HistogramsPerBatch, HistogramsPerBatch);
        Span<uint> blockLengths = u32.Slice(4 * HistogramsPerBatch);
        THistogram[] tmp = _tmp.Ensure<THistogram>(2);
        u32.Clear();
        {
            // The blocks' lengths.
            int blockIdx = 0;
            for (int i = 0; i < length; ++i)
            {
                ++blockLengths[blockIdx];
                if (i + 1 == length || blockIds[i] != blockIds[i + 1])
                {
                    ++blockIdx;
                }
            }
        }

        // Batches of blocks, clustered.
        for (nuint i = 0; i < numBlocks; i += HistogramsPerBatch)
        {
            nuint numToCombine = Math.Min(numBlocks - i, HistogramsPerBatch);
            for (int j = 0; j < (int)numToCombine; ++j)
            {
                uint blockLength = blockLengths[(int)i + j];
                ref THistogram histogram = ref histograms[j];
                Histogram.Clear(ref histogram);
                for (uint k = 0; k < blockLength; ++k)
                {
                    Histogram.Add(ref histogram, nuint.CreateTruncating(data[pos++]));
                }

                histogram.BitCost = Cluster.PopulationCost(ref histogram);
                newClusters[j] = (uint)j;
                symbols[j] = (uint)j;
                sizes[j] = 1;
            }

            nuint numNewClusters = Cluster.HistogramCombine(histograms, ref tmp[0], sizes, symbols, newClusters, pairs, numToCombine, numToCombine, HistogramsPerBatch, maxNumPairs);
            if (allHistogramsCapacity < allHistogramsSize + numNewClusters)
            {
                allHistogramsCapacity = Math.Max(allHistogramsSize + numNewClusters, allHistogramsCapacity * 2);
                allHistograms = _allHistograms.Grow<THistogram>(allHistogramsSize, allHistogramsCapacity);
            }

            if (clusterSizeCapacity < clusterSizeSize + numNewClusters)
            {
                clusterSizeCapacity = Math.Max(clusterSizeSize + numNewClusters, clusterSizeCapacity * 2);
                clusterSize = _clusterSize.Grow<uint>(clusterSizeSize, clusterSizeCapacity);
            }

            for (int j = 0; j < (int)numNewClusters; ++j)
            {
                allHistograms[allHistogramsSize++] = histograms[newClusters[j]];
                clusterSize[clusterSizeSize++] = sizes[(int)newClusters[j]];
                remap[(int)newClusters[j]] = (uint)j;
            }

            for (int j = 0; j < (int)numToCombine; ++j)
            {
                histogramSymbols[(int)i + j] = (uint)numClusters + remap[(int)symbols[j]];
            }

            numClusters += numNewClusters;
        }

        // The final clustering.
        maxNumPairs = Math.Min(64 * numClusters, numClusters / 2 * numClusters);
        if (pairsCapacity < maxNumPairs + 1)
        {
            pairs = _pairs.Ensure(maxNumPairs + 1);
        }

        uint[] clusters = _clusters.Ensure(numClusters);
        for (nuint i = 0; i < numClusters; ++i)
        {
            clusters[i] = (uint)i;
        }

        nuint numFinalClusters = Cluster.HistogramCombine(allHistograms, ref tmp[0], clusterSize, histogramSymbols, clusters, pairs, numClusters, numBlocks, Prefix.MaxBlockTypes, maxNumPairs);

        // Each block to its final histogram.
        uint[] newIndex = _newIndex.Ensure(numClusters);
        newIndex.AsSpan(0, (int)numClusters).Fill(invalidIndex);
        pos = 0;
        {
            uint nextIndex = 0;
            for (int i = 0; i < (int)numBlocks; ++i)
            {
                Histogram.Clear(ref tmp[0]);
                for (uint j = 0; j < blockLengths[i]; ++j)
                {
                    Histogram.Add(ref tmp[0], nuint.CreateTruncating(data[pos++]));
                }

                // Among equally good histograms, the last one used.
                uint bestOut = i == 0 ? histogramSymbols[0] : histogramSymbols[i - 1];
                double bestBits = Cluster.HistogramBitCostDistance(ref tmp[0], ref allHistograms[bestOut], ref tmp[1]);
                for (nuint j = 0; j < numFinalClusters; ++j)
                {
                    double curBits = Cluster.HistogramBitCostDistance(ref tmp[0], ref allHistograms[clusters[j]], ref tmp[1]);
                    if (curBits < bestBits)
                    {
                        bestBits = curBits;
                        bestOut = clusters[j];
                    }
                }

                histogramSymbols[i] = bestOut;
                if (newIndex[bestOut] == invalidIndex)
                {
                    newIndex[bestOut] = nextIndex++;
                }
            }
        }

        split.EnsureCapacity(numBlocks);
        {
            uint curLength = 0;
            nuint blockIdx = 0;
            byte maxType = 0;
            for (int i = 0; i < (int)numBlocks; ++i)
            {
                curLength += blockLengths[i];
                if (i + 1 == (int)numBlocks || histogramSymbols[i] != histogramSymbols[i + 1])
                {
                    byte id = (byte)newIndex[histogramSymbols[i]];
                    split.Types[blockIdx] = id;
                    split.Lengths[blockIdx] = curLength;
                    maxType = Math.Max(maxType, id);
                    curLength = 0;
                    ++blockIdx;
                }
            }

            split.NumBlocks = blockIdx;
            split.NumTypes = (nuint)maxType + 1;
        }
    }

    /// <summary>brotli's <c>SplitByteVector</c>: the split of a category's symbols.</summary>
    private void SplitByteVector<THistogram, TData>(ReadOnlySpan<TData> data, nuint symbolsPerHistogram, nuint maxHistograms, nuint samplingStrideLength, double blockSwitchCost, int quality, BlockSplit split)
        where THistogram : struct, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        nuint length = (nuint)data.Length;
        nuint dataSize = (nuint)THistogram.DataSize;
        nuint numHistograms = (length / symbolsPerHistogram) + 1;
        if (numHistograms > maxHistograms)
        {
            numHistograms = maxHistograms;
        }

        if (length == 0)
        {
            split.NumTypes = 1;
            return;
        }

        if (length < MinLengthForBlockSplitting)
        {
            split.EnsureCapacity(split.NumBlocks + 1);
            split.NumTypes = 1;
            split.Types[split.NumBlocks] = 0;
            split.Lengths[split.NumBlocks] = (uint)length;
            split.NumBlocks++;
            return;
        }

        THistogram[] histograms = _histograms.Ensure<THistogram>(numHistograms + 1);
        ref THistogram tmp = ref histograms[numHistograms];

        // Good entropy codes.
        InitialEntropyCodes(data, samplingStrideLength, numHistograms, histograms);
        RefineEntropyCodes(data, samplingStrideLength, numHistograms, histograms, ref tmp);
        {
            // A good path through the symbols with them.
            Span<byte> blockIds = _blockIds.Ensure(length).AsSpan(0, (int)length);
            nuint numBlocks = 0;
            nuint bitmapLen = (numHistograms + 7) >> 3;
            Span<double> insertCost = _insertCost.Ensure(dataSize * numHistograms);
            Span<double> cost = _cost.Ensure(numHistograms);
            Span<byte> switchSignal = _switchSignal.Ensure(length * bitmapLen);
            Span<ushort> newId = _newId.Ensure(numHistograms);
            int iters = quality < 11 ? 3 : 10;
            for (int i = 0; i < iters; ++i)
            {
                numBlocks = FindBlocks(data, blockSwitchCost, numHistograms, histograms, insertCost, cost, switchSignal, blockIds);
                numHistograms = RemapBlockIds(blockIds, newId, numHistograms);
                BuildBlockHistograms(data, blockIds, numHistograms, histograms);
            }

            ClusterBlocks<THistogram, TData>(data, numBlocks, blockIds, split);
        }
    }
}
