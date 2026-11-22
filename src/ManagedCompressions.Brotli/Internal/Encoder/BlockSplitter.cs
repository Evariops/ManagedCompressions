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
internal sealed unsafe class BlockSplitter
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

    private readonly PinnedBuffer<byte> _literals = new();
    private readonly PinnedBuffer<ushort> _codes = new();
    private readonly PinnedBuffer<byte> _histograms = new();
    private readonly PinnedBuffer<byte> _allHistograms = new();
    private readonly PinnedBuffer<byte> _blockIds = new();
    private readonly PinnedBuffer<double> _insertCost = new();
    private readonly PinnedBuffer<double> _cost = new();
    private readonly PinnedBuffer<byte> _switchSignal = new();
    private readonly PinnedBuffer<ushort> _newId = new();
    private readonly PinnedBuffer<uint> _histogramSymbols = new();
    private readonly PinnedBuffer<uint> _u32 = new();
    private readonly PinnedBuffer<uint> _clusterSize = new();
    private readonly PinnedBuffer<uint> _clusters = new();
    private readonly PinnedBuffer<uint> _newIndex = new();
    private readonly PinnedBuffer<HistogramPair> _pairs = new();
    private readonly PinnedBuffer<byte> _tmp = new();

    /// <summary>brotli's <c>BrotliSplitBlock</c>: the splits of the literals, the commands and the distances.</summary>
    public void SplitBlock(Command* cmds, nuint numCommands, byte* data, nuint pos, nuint mask, int quality, BlockSplit literalSplit, BlockSplit insertAndCopySplit, BlockSplit distSplit)
    {
        {
            nuint literalsCount = 0;
            for (nuint i = 0; i < numCommands; ++i)
            {
                literalsCount += cmds[i].InsertLen;
            }

            byte* literals = _literals.Ensure(literalsCount);
            CopyLiteralsToByteArray(cmds, numCommands, data, pos, mask, literals);
            SplitByteVector<HistogramLiteral, byte>(literals, literalsCount, SymbolsPerLiteralHistogram, MaxLiteralHistograms, LiteralStrideLength, LiteralBlockSwitchCost, quality, literalSplit);
        }

        {
            ushort* insertAndCopyCodes = _codes.Ensure(numCommands);
            for (nuint i = 0; i < numCommands; ++i)
            {
                insertAndCopyCodes[i] = cmds[i].CmdPrefix;
            }

            SplitByteVector<HistogramCommand, ushort>(insertAndCopyCodes, numCommands, SymbolsPerCommandHistogram, MaxCommandHistograms, CommandStrideLength, CommandBlockSwitchCost, quality, insertAndCopySplit);
        }

        {
            ushort* distancePrefixes = _codes.Ensure(numCommands);
            nuint j = 0;
            for (nuint i = 0; i < numCommands; ++i)
            {
                Command* cmd = cmds + i;
                if (Commands.CopyLen(cmd) != 0 && cmd->CmdPrefix >= 128)
                {
                    distancePrefixes[j++] = (ushort)(cmd->DistPrefix & 0x3FF);
                }
            }

            SplitByteVector<HistogramDistance, ushort>(distancePrefixes, j, SymbolsPerDistanceHistogram, MaxCommandHistograms, DistanceStrideLength, DistanceBlockSwitchCost, quality, distSplit);
        }
    }

    private static void CopyLiteralsToByteArray(Command* cmds, nuint numCommands, byte* data, nuint offset, nuint mask, byte* literals)
    {
        nuint pos = 0;
        nuint fromPos = offset & mask;
        for (nuint i = 0; i < numCommands; ++i)
        {
            nuint insertLen = cmds[i].InsertLen;
            if (fromPos + insertLen > mask)
            {
                nuint headSize = mask + 1 - fromPos;
                Buffer.MemoryCopy(data + fromPos, literals + pos, headSize, headSize);
                fromPos = 0;
                pos += headSize;
                insertLen -= headSize;
            }

            if (insertLen > 0)
            {
                Buffer.MemoryCopy(data + fromPos, literals + pos, insertLen, insertLen);
                pos += insertLen;
            }

            fromPos = (fromPos + insertLen + Commands.CopyLen(cmds + i)) & mask;
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
    private static void HistogramAddVector<THistogram, TData>(THistogram* h, TData* p, nuint n)
        where THistogram : unmanaged, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        *Histogram.TotalCount(h) += n;
        uint* data = Histogram.Data(h);
        for (nuint i = 0; i < n; ++i)
        {
            ++data[nuint.CreateTruncating(p[i])];
        }
    }

    private static void InitialEntropyCodes<THistogram, TData>(TData* data, nuint length, nuint stride, nuint numHistograms, THistogram* histograms)
        where THistogram : unmanaged, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        uint seed = 7;
        nuint blockLength = length / numHistograms;
        Histogram.ClearAll(histograms, numHistograms);
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

            HistogramAddVector(histograms + i, data + pos, stride);
        }
    }

    private static void RandomSample<THistogram, TData>(ref uint seed, TData* data, nuint length, nuint stride, THistogram* sample)
        where THistogram : unmanaged, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        nuint pos = 0;
        if (stride >= length)
        {
            stride = length;
        }
        else
        {
            pos = MyRand(ref seed) % (length - stride + 1);
        }

        HistogramAddVector(sample, data + pos, stride);
    }

    private static void RefineEntropyCodes<THistogram, TData>(TData* data, nuint length, nuint stride, nuint numHistograms, THistogram* histograms, THistogram* tmp)
        where THistogram : unmanaged, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        nuint iters = (IterMulForRefining * length / stride) + MinItersForRefining;
        uint seed = 7;
        iters = (iters + numHistograms - 1) / numHistograms * numHistograms;
        for (nuint iter = 0; iter < iters; ++iter)
        {
            Histogram.Clear(tmp);
            RandomSample(ref seed, data, length, stride, tmp);
            Histogram.AddHistogram(histograms + (iter % numHistograms), tmp);
        }
    }

    /// <summary>brotli's <c>FindBlocks</c>: the cheapest assignment of the symbols to the codes, with block switches.</summary>
    private static nuint FindBlocks<THistogram, TData>(TData* data, nuint length, double blockSwitchBitcost, nuint numHistograms, THistogram* histograms, double* insertCost, double* cost, byte* switchSignal, byte* blockId)
        where THistogram : unmanaged, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        nuint alphabetSize = (nuint)THistogram.DataSize;
        nuint bitmapLen = (numHistograms + 7) >> 3;
        nuint numBlocks = 1;
        if (numHistograms <= 1)
        {
            for (nuint i = 0; i < length; ++i)
            {
                blockId[i] = 0;
            }

            return 1;
        }

        // The bits of each symbol in each code: 2 + log2(total) for a symbol a code lacks.
        new Span<double>(insertCost, (int)(alphabetSize * numHistograms)).Clear();
        for (nuint i = 0; i < numHistograms; ++i)
        {
            insertCost[i] = FastLog.FastLog2((uint)*Histogram.TotalCount(histograms + i));
        }

        for (nuint i = alphabetSize; i != 0;)
        {
            --i;
            for (nuint j = 0; j < numHistograms; ++j)
            {
                insertCost[(i * numHistograms) + j] = insertCost[j] - BitCost(Histogram.Data(histograms + j)[i]);
            }
        }

        new Span<double>(cost, (int)numHistograms).Clear();
        new Span<byte>(switchSignal, (int)(length * bitmapLen)).Clear();
        for (nuint byteIx = 0; byteIx < length; ++byteIx)
        {
            nuint ix = byteIx * bitmapLen;
            nuint symbol = nuint.CreateTruncating(data[byteIx]);
            nuint insertCostIx = symbol * numHistograms;
            double blockSwitchCost = blockSwitchBitcost;

            // The symbol coded with each code; the cheapest code, the first of the cheapest.
            AddCosts(cost, insertCost + insertCostIx, numHistograms);
            double minCost = 1e99;
            int best = FirstMinimum(cost, numHistograms, ref minCost);
            if (best >= 0)
            {
                blockId[byteIx] = (byte)best;
            }

            // More blocks at the beginning.
            if (byteIx < 2000)
            {
                blockSwitchCost *= 0.77 + (0.07 * (double)byteIx / 2000);
            }

            SubtractAndCap(cost, numHistograms, minCost, blockSwitchCost, switchSignal + ix);
        }

        {
            // Back from the last position, switching at the marks.
            nuint byteIx = length - 1;
            nuint ix = byteIx * bitmapLen;
            byte curId = blockId[byteIx];
            while (byteIx > 0)
            {
                byte mask = (byte)(1u << (curId & 7));
                --byteIx;
                ix -= bitmapLen;
                if ((switchSignal[ix + (nuint)(curId >> 3)] & mask) != 0)
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
    private static void AddCosts(double* cost, double* insert, nuint n)
    {
        nuint k = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; k + 4 <= n; k += 4)
            {
                (Vector256.Load(cost + k) + Vector256.Load(insert + k)).Store(cost + k);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; k + 2 <= n; k += 2)
            {
                (Vector128.Load(cost + k) + Vector128.Load(insert + k)).Store(cost + k);
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
    private static int FirstMinimum(double* cost, nuint n, ref double minCost)
    {
        double least = minCost;
        nuint k = 0;
        if (Vector256.IsHardwareAccelerated && n >= 4)
        {
            Vector256<double> min = Vector256.Create(least);
            for (; k + 4 <= n; k += 4)
            {
                min = Vector256.Min(min, Vector256.Load(cost + k));
            }

            least = Math.Min(Math.Min(min[0], min[1]), Math.Min(min[2], min[3]));
        }
        else if (Vector128.IsHardwareAccelerated && n >= 2)
        {
            Vector128<double> min = Vector128.Create(least);
            for (; k + 2 <= n; k += 2)
            {
                min = Vector128.Min(min, Vector128.Load(cost + k));
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
                return (int)k;
            }
        }
    }

    /// <summary>
    /// <c>cost[k] -= minCost</c>, capped at <paramref name="blockSwitchCost"/>, the capped codes marked in
    /// <paramref name="switchSignal"/>'s bits, a vector at a time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SubtractAndCap(double* cost, nuint n, double minCost, double blockSwitchCost, byte* switchSignal)
    {
        nuint k = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<double> min = Vector256.Create(minCost);
            Vector256<double> cap = Vector256.Create(blockSwitchCost);
            for (; k + 8 <= n; k += 8)
            {
                Vector256<double> c0 = Vector256.Load(cost + k) - min;
                Vector256<double> c1 = Vector256.Load(cost + k + 4) - min;
                Vector256<double> m0 = Vector256.GreaterThanOrEqual(c0, cap);
                Vector256<double> m1 = Vector256.GreaterThanOrEqual(c1, cap);
                Vector256.ConditionalSelect(m0, cap, c0).Store(cost + k);
                Vector256.ConditionalSelect(m1, cap, c1).Store(cost + k + 4);
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
                    Vector128<double> c = Vector128.Load(cost + k + (nuint)i) - min;
                    Vector128<double> m = Vector128.GreaterThanOrEqual(c, cap);
                    Vector128.ConditionalSelect(m, cap, c).Store(cost + k + (nuint)i);
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
                switchSignal[k >> 3] |= (byte)(1u << (int)(k & 7));
            }
        }
    }

    private static nuint RemapBlockIds(byte* blockIds, nuint length, ushort* newId, nuint numHistograms)
    {
        const ushort invalidId = 256;
        ushort nextId = 0;
        for (nuint i = 0; i < numHistograms; ++i)
        {
            newId[i] = invalidId;
        }

        for (nuint i = 0; i < length; ++i)
        {
            if (newId[blockIds[i]] == invalidId)
            {
                newId[blockIds[i]] = nextId++;
            }
        }

        for (nuint i = 0; i < length; ++i)
        {
            blockIds[i] = (byte)newId[blockIds[i]];
        }

        return nextId;
    }

    private static void BuildBlockHistograms<THistogram, TData>(TData* data, nuint length, byte* blockIds, nuint numHistograms, THistogram* histograms)
        where THistogram : unmanaged, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        Histogram.ClearAll(histograms, numHistograms);
        for (nuint i = 0; i < length; ++i)
        {
            Histogram.Add(histograms + blockIds[i], nuint.CreateTruncating(data[i]));
        }
    }

    /// <summary>brotli's <c>ClusterBlocks</c>: the blocks' histograms clustered in batches, then together.</summary>
    private void ClusterBlocks<THistogram, TData>(TData* data, nuint length, nuint numBlocks, byte* blockIds, BlockSplit split)
        where THistogram : unmanaged, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
        uint* histogramSymbols = _histogramSymbols.Ensure(numBlocks);
        uint* u32 = _u32.Ensure(numBlocks + (4 * HistogramsPerBatch));
        nuint expectedNumClusters = ClustersPerBatch * (numBlocks + HistogramsPerBatch - 1) / HistogramsPerBatch;
        nuint allHistogramsSize = 0;
        nuint allHistogramsCapacity = expectedNumClusters;
        THistogram* allHistograms = (THistogram*)_allHistograms.Ensure(allHistogramsCapacity * (nuint)sizeof(THistogram));
        nuint clusterSizeSize = 0;
        nuint clusterSizeCapacity = expectedNumClusters;
        uint* clusterSize = _clusterSize.Ensure(clusterSizeCapacity);
        nuint numClusters = 0;
        THistogram* histograms = (THistogram*)_histograms.Ensure(Math.Min(numBlocks, HistogramsPerBatch) * (nuint)sizeof(THistogram));
        nuint maxNumPairs = HistogramsPerBatch * HistogramsPerBatch / 2;
        nuint pairsCapacity = maxNumPairs + 1;
        HistogramPair* pairs = _pairs.Ensure(pairsCapacity);
        nuint pos = 0;
        const uint invalidIndex = uint.MaxValue;
        uint* sizes = u32;
        uint* newClusters = u32 + HistogramsPerBatch;
        uint* symbols = u32 + (2 * HistogramsPerBatch);
        uint* remap = u32 + (3 * HistogramsPerBatch);
        uint* blockLengths = u32 + (4 * HistogramsPerBatch);
        THistogram* tmp = (THistogram*)_tmp.Ensure(2 * (nuint)sizeof(THistogram));
        new Span<uint>(u32, (int)(numBlocks + (4 * HistogramsPerBatch))).Clear();

        {
            // The blocks' lengths.
            nuint blockIdx = 0;
            for (nuint i = 0; i < length; ++i)
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
            for (nuint j = 0; j < numToCombine; ++j)
            {
                nuint blockLength = blockLengths[i + j];
                Histogram.Clear(histograms + j);
                for (nuint k = 0; k < blockLength; ++k)
                {
                    Histogram.Add(histograms + j, nuint.CreateTruncating(data[pos++]));
                }

                *Histogram.BitCost(histograms + j) = Cluster.PopulationCost(histograms + j);
                newClusters[j] = (uint)j;
                symbols[j] = (uint)j;
                sizes[j] = 1;
            }

            nuint numNewClusters = Cluster.HistogramCombine(histograms, tmp, sizes, symbols, newClusters, pairs, numToCombine, numToCombine, HistogramsPerBatch, maxNumPairs);
            if (allHistogramsCapacity < allHistogramsSize + numNewClusters)
            {
                allHistogramsCapacity = Math.Max(allHistogramsSize + numNewClusters, allHistogramsCapacity * 2);
                allHistograms = (THistogram*)Grow(_allHistograms, allHistogramsSize * (nuint)sizeof(THistogram), allHistogramsCapacity * (nuint)sizeof(THistogram));
            }

            if (clusterSizeCapacity < clusterSizeSize + numNewClusters)
            {
                clusterSizeCapacity = Math.Max(clusterSizeSize + numNewClusters, clusterSizeCapacity * 2);
                clusterSize = (uint*)Grow(_clusterSize, clusterSizeSize * sizeof(uint), clusterSizeCapacity * sizeof(uint));
            }

            for (nuint j = 0; j < numNewClusters; ++j)
            {
                allHistograms[allHistogramsSize++] = histograms[newClusters[j]];
                clusterSize[clusterSizeSize++] = sizes[newClusters[j]];
                remap[newClusters[j]] = (uint)j;
            }

            for (nuint j = 0; j < numToCombine; ++j)
            {
                histogramSymbols[i + j] = (uint)numClusters + remap[symbols[j]];
            }

            numClusters += numNewClusters;
        }

        // The final clustering.
        maxNumPairs = Math.Min(64 * numClusters, numClusters / 2 * numClusters);
        if (pairsCapacity < maxNumPairs + 1)
        {
            pairs = _pairs.Ensure(maxNumPairs + 1);
        }

        uint* clusters = _clusters.Ensure(numClusters);
        for (nuint i = 0; i < numClusters; ++i)
        {
            clusters[i] = (uint)i;
        }

        nuint numFinalClusters = Cluster.HistogramCombine(allHistograms, tmp, clusterSize, histogramSymbols, clusters, pairs, numClusters, numBlocks, Prefix.MaxBlockTypes, maxNumPairs);

        // Each block to its final histogram.
        uint* newIndex = _newIndex.Ensure(numClusters);
        for (nuint i = 0; i < numClusters; ++i)
        {
            newIndex[i] = invalidIndex;
        }

        pos = 0;
        {
            uint nextIndex = 0;
            for (nuint i = 0; i < numBlocks; ++i)
            {
                Histogram.Clear(tmp);
                for (nuint j = 0; j < blockLengths[i]; ++j)
                {
                    Histogram.Add(tmp, nuint.CreateTruncating(data[pos++]));
                }

                // Among equally good histograms, the last one used.
                uint bestOut = i == 0 ? histogramSymbols[0] : histogramSymbols[i - 1];
                double bestBits = Cluster.HistogramBitCostDistance(tmp, allHistograms + bestOut, tmp + 1);
                for (nuint j = 0; j < numFinalClusters; ++j)
                {
                    double curBits = Cluster.HistogramBitCostDistance(tmp, allHistograms + clusters[j], tmp + 1);
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
            for (nuint i = 0; i < numBlocks; ++i)
            {
                curLength += blockLengths[i];
                if (i + 1 == numBlocks || histogramSymbols[i] != histogramSymbols[i + 1])
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

    /// <summary>A buffer grown to <paramref name="bytes"/>, which keeps its first <paramref name="keep"/> bytes.</summary>
    private static byte* Grow<T>(PinnedBuffer<T> buffer, nuint keep, nuint bytes)
        where T : unmanaged
    {
        byte[] copy = new byte[(int)keep];
        T* old = buffer.Ensure(0);
        new ReadOnlySpan<byte>(old, (int)keep).CopyTo(copy);
        byte* grown = (byte*)buffer.Ensure((bytes + (nuint)sizeof(T) - 1) / (nuint)sizeof(T));
        copy.CopyTo(new Span<byte>(grown, (int)keep));
        return grown;
    }

    /// <summary>brotli's <c>SplitByteVector</c>: the split of a category's symbols.</summary>
    private void SplitByteVector<THistogram, TData>(TData* data, nuint length, nuint symbolsPerHistogram, nuint maxHistograms, nuint samplingStrideLength, double blockSwitchCost, int quality, BlockSplit split)
        where THistogram : unmanaged, IHistogram
        where TData : unmanaged, IBinaryInteger<TData>
    {
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

        THistogram* histograms = (THistogram*)_histograms.Ensure((numHistograms + 1) * (nuint)sizeof(THistogram));
        THistogram* tmp = histograms + numHistograms;

        // Good entropy codes.
        InitialEntropyCodes(data, length, samplingStrideLength, numHistograms, histograms);
        RefineEntropyCodes(data, length, samplingStrideLength, numHistograms, histograms, tmp);
        {
            // A good path through the symbols with them.
            byte* blockIds = _blockIds.Ensure(length);
            nuint numBlocks = 0;
            nuint bitmapLen = (numHistograms + 7) >> 3;
            double* insertCost = _insertCost.Ensure(dataSize * numHistograms);
            double* cost = _cost.Ensure(numHistograms);
            byte* switchSignal = _switchSignal.Ensure(length * bitmapLen);
            ushort* newId = _newId.Ensure(numHistograms);
            int iters = quality < 11 ? 3 : 10;
            for (int i = 0; i < iters; ++i)
            {
                numBlocks = FindBlocks(data, length, blockSwitchCost, numHistograms, histograms, insertCost, cost, switchSignal, blockIds);
                numHistograms = RemapBlockIds(blockIds, length, newId, numHistograms);
                BuildBlockHistograms(data, length, blockIds, numHistograms, histograms);
            }

            ClusterBlocks<THistogram, TData>(data, length, numBlocks, blockIds, split);
        }
    }
}
