using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>brotli's <c>HistogramPair</c>: two clusters, and what merging them saves.</summary>
internal struct HistogramPair
{
    public uint Idx1;
    public uint Idx2;
    public double CostCombo;
    public double CostDiff;
}

/// <summary>
/// brotli's histogram costs and clustering (<c>c/enc/bit_cost_inc.h</c>, <c>c/enc/cluster_inc.h</c>):
/// the cost of a histogram's prefix code, and the greedy merges of histograms that save the most bits.
/// </summary>
internal static class Cluster
{
    /// <summary>brotli's <c>BrotliPopulationCost</c>: the bits of a histogram's symbols and of its code.</summary>
    public static double PopulationCost<T>(ref T histogram)
        where T : struct, IHistogram
    {
        const double oneSymbolHistogramCost = 12;
        const double twoSymbolHistogramCost = 20;
        const double threeSymbolHistogramCost = 28;
        const double fourSymbolHistogramCost = 37;
        ReadOnlySpan<uint> data = histogram.Data;
        int dataSize = data.Length;
        nuint totalCount = histogram.TotalCount;
        int count = 0;
        Span<int> s = stackalloc int[5];
        double bits = 0.0;
        if (totalCount == 0)
        {
            return oneSymbolHistogramCost;
        }

        count = FirstNonZero(data, s);
        if (count == 1)
        {
            return oneSymbolHistogramCost;
        }

        if (count == 2)
        {
            return twoSymbolHistogramCost + (double)totalCount;
        }

        if (count == 3)
        {
            uint histo0 = data[s[0]];
            uint histo1 = data[s[1]];
            uint histo2 = data[s[2]];
            uint histomax = Math.Max(histo0, Math.Max(histo1, histo2));
            return threeSymbolHistogramCost + (2 * (histo0 + histo1 + histo2)) - histomax;
        }

        if (count == 4)
        {
            Span<uint> histo = stackalloc uint[4];
            for (int i = 0; i < 4; ++i)
            {
                histo[i] = data[s[i]];
            }

            for (int i = 0; i < 4; ++i)
            {
                for (int j = i + 1; j < 4; ++j)
                {
                    if (histo[j] > histo[i])
                    {
                        (histo[j], histo[i]) = (histo[i], histo[j]);
                    }
                }
            }

            uint h23 = histo[2] + histo[3];
            uint histomax = Math.Max(h23, histo[0]);
            return fourSymbolHistogramCost + (3 * h23) + (2 * (histo[0] + histo[1])) - histomax;
        }

        {
            // The entropy, and the code length code's histogram, with the zero repeat code 17 but not 16.
            nuint maxDepth = 1;
            Span<uint> depthHisto = stackalloc uint[Prefix.NumCodeLengthCodes];
            double log2Total = FastLog.FastLog2(totalCount);
            for (int i = 0; i < dataSize;)
            {
                if (data[i] > 0)
                {
                    double log2P = log2Total - FastLog.FastLog2(data[i]);
                    nuint depth = (nuint)(log2P + 0.5);
                    bits += data[i] * log2P;
                    if (depth > 15)
                    {
                        depth = 15;
                    }

                    if (depth > maxDepth)
                    {
                        maxDepth = depth;
                    }

                    ++depthHisto[(int)depth];
                    ++i;
                }
                else
                {
                    uint reps = (uint)ZeroRun(data, i + 1) + 1;
                    i += (int)reps;
                    if (i == dataSize)
                    {
                        // The last run of zeros costs nothing: it is implicit.
                        break;
                    }

                    if (reps < 3)
                    {
                        depthHisto[0] += reps;
                    }
                    else
                    {
                        reps -= 2;
                        while (reps > 0)
                        {
                            ++depthHisto[Prefix.RepeatZeroCodeLength];
                            bits += 3;
                            reps >>= 3;
                        }
                    }
                }
            }

            bits += (double)(18 + (2 * maxDepth));
            bits += FastLog.BitsEntropy(depthHisto);
        }

        return bits;
    }

    /// <summary>The indices of the first nonzero counts into <paramref name="s"/>, five at most: their number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FirstNonZero(ReadOnlySpan<uint> data, Span<int> s)
    {
        int size = data.Length;
        int count = 0;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= size; i += 8)
            {
                uint mask = ~Vector256.Equals(Vector256.Create(data.Slice(i, 8)), Vector256<uint>.Zero).ExtractMostSignificantBits() & 0xFF;
                for (; mask != 0; mask &= mask - 1)
                {
                    s[count++] = i + BitOperations.TrailingZeroCount(mask);
                    if (count > 4)
                    {
                        return count;
                    }
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 4 <= size; i += 4)
            {
                uint mask = ~Vector128.Equals(Vector128.Create(data.Slice(i, 4)), Vector128<uint>.Zero).ExtractMostSignificantBits() & 0xF;
                for (; mask != 0; mask &= mask - 1)
                {
                    s[count++] = i + BitOperations.TrailingZeroCount(mask);
                    if (count > 4)
                    {
                        return count;
                    }
                }
            }
        }

        for (; i < size; ++i)
        {
            if (data[i] > 0)
            {
                s[count++] = i;
                if (count > 4)
                {
                    return count;
                }
            }
        }

        return count;
    }

    /// <summary>The number of zero counts from <paramref name="from"/> on.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ZeroRun(ReadOnlySpan<uint> data, int from)
    {
        int size = data.Length;
        int k = from;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; k + 8 <= size; k += 8)
            {
                uint nonZero = ~Vector256.Equals(Vector256.Create(data.Slice(k, 8)), Vector256<uint>.Zero).ExtractMostSignificantBits() & 0xFF;
                if (nonZero != 0)
                {
                    return k + BitOperations.TrailingZeroCount(nonZero) - from;
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; k + 4 <= size; k += 4)
            {
                uint nonZero = ~Vector128.Equals(Vector128.Create(data.Slice(k, 4)), Vector128<uint>.Zero).ExtractMostSignificantBits() & 0xF;
                if (nonZero != 0)
                {
                    return k + BitOperations.TrailingZeroCount(nonZero) - from;
                }
            }
        }

        while (k < size && data[k] == 0)
        {
            ++k;
        }

        return k - from;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HistogramPairIsLess(in HistogramPair p1, in HistogramPair p2)
    {
        if (p1.CostDiff != p2.CostDiff)
        {
            return p1.CostDiff > p2.CostDiff;
        }

        return p1.Idx2 - p1.Idx1 > p2.Idx2 - p2.Idx1;
    }

    /// <summary>brotli's <c>ClusterCostDiff</c>: the context map's entropy saved by merging two clusters.</summary>
    private static double ClusterCostDiff(nuint sizeA, nuint sizeB)
    {
        nuint sizeC = sizeA + sizeB;
        return ((double)sizeA * FastLog.FastLog2(sizeA)) + ((double)sizeB * FastLog.FastLog2(sizeB)) - ((double)sizeC * FastLog.FastLog2(sizeC));
    }

    /// <summary>brotli's <c>BrotliCompareAndPushToQueue</c>.</summary>
    private static void CompareAndPushToQueue<T>(Span<T> output, ref T tmp, Span<uint> clusterSize, uint idx1, uint idx2, nuint maxNumPairs, Span<HistogramPair> pairs, ref nuint numPairs)
        where T : struct, IHistogram
    {
        bool isGoodPair = false;
        HistogramPair p;
        if (idx1 == idx2)
        {
            return;
        }

        if (idx2 < idx1)
        {
            (idx1, idx2) = (idx2, idx1);
        }

        ref T out1 = ref output[(int)idx1];
        ref T out2 = ref output[(int)idx2];
        p.Idx1 = idx1;
        p.Idx2 = idx2;
        p.CostCombo = 0;
        p.CostDiff = 0.5 * ClusterCostDiff(clusterSize[(int)idx1], clusterSize[(int)idx2]);
        p.CostDiff -= out1.BitCost;
        p.CostDiff -= out2.BitCost;
        if (out1.TotalCount == 0)
        {
            p.CostCombo = out2.BitCost;
            isGoodPair = true;
        }
        else if (out2.TotalCount == 0)
        {
            p.CostCombo = out1.BitCost;
            isGoodPair = true;
        }
        else
        {
            double threshold = numPairs == 0 ? 1e99 : Math.Max(0.0, pairs[0].CostDiff);
            Histogram.Sum(ref tmp, ref out1, ref out2);
            double costCombo = PopulationCost(ref tmp);
            if (costCombo < threshold - p.CostDiff)
            {
                p.CostCombo = costCombo;
                isGoodPair = true;
            }
        }

        if (isGoodPair)
        {
            p.CostDiff += p.CostCombo;
            if (numPairs > 0 && HistogramPairIsLess(in pairs[0], in p))
            {
                // The new top of the queue.
                if (numPairs < maxNumPairs)
                {
                    pairs[(int)numPairs] = pairs[0];
                    ++numPairs;
                }

                pairs[0] = p;
            }
            else if (numPairs < maxNumPairs)
            {
                pairs[(int)numPairs] = p;
                ++numPairs;
            }
        }
    }

    /// <summary>brotli's <c>BrotliHistogramCombine</c>: merges the best pairs until none saves bits, or to <paramref name="maxClusters"/>.</summary>
    public static nuint HistogramCombine<T>(Span<T> output, ref T tmp, Span<uint> clusterSize, Span<uint> symbols, Span<uint> clusters, Span<HistogramPair> pairs, nuint numClusters, nuint symbolsSize, nuint maxClusters, nuint maxNumPairs)
        where T : struct, IHistogram
    {
        double costDiffThreshold = 0.0;
        nuint minClusterSize = 1;
        nuint numPairs = 0;
        for (int idx1 = 0; idx1 < (int)numClusters; ++idx1)
        {
            for (int idx2 = idx1 + 1; idx2 < (int)numClusters; ++idx2)
            {
                CompareAndPushToQueue(output, ref tmp, clusterSize, clusters[idx1], clusters[idx2], maxNumPairs, pairs, ref numPairs);
            }
        }

        while (numClusters > minClusterSize)
        {
            if (pairs[0].CostDiff >= costDiffThreshold)
            {
                costDiffThreshold = 1e99;
                minClusterSize = maxClusters;
                continue;
            }

            // The best pair, at the top.
            uint bestIdx1 = pairs[0].Idx1;
            uint bestIdx2 = pairs[0].Idx2;
            Histogram.AddHistogram(ref output[(int)bestIdx1], ref output[(int)bestIdx2]);
            output[(int)bestIdx1].BitCost = pairs[0].CostCombo;
            clusterSize[(int)bestIdx1] += clusterSize[(int)bestIdx2];
            for (int i = 0; i < (int)symbolsSize; ++i)
            {
                if (symbols[i] == bestIdx2)
                {
                    symbols[i] = bestIdx1;
                }
            }

            for (int i = 0; i < (int)numClusters; ++i)
            {
                if (clusters[i] == bestIdx2)
                {
                    clusters.Slice(i + 1, (int)numClusters - i - 1).CopyTo(clusters.Slice(i));
                    break;
                }
            }

            --numClusters;
            {
                // The pairs of either cluster go.
                int copyToIdx = 0;
                for (int i = 0; i < (int)numPairs; ++i)
                {
                    HistogramPair p = pairs[i];
                    if (p.Idx1 == bestIdx1 || p.Idx2 == bestIdx1 || p.Idx1 == bestIdx2 || p.Idx2 == bestIdx2)
                    {
                        continue;
                    }

                    if (HistogramPairIsLess(in pairs[0], in p))
                    {
                        HistogramPair front = pairs[0];
                        pairs[0] = p;
                        pairs[copyToIdx] = front;
                    }
                    else
                    {
                        pairs[copyToIdx] = p;
                    }

                    ++copyToIdx;
                }

                numPairs = (nuint)copyToIdx;
            }

            // The pairs of the merged cluster.
            for (int i = 0; i < (int)numClusters; ++i)
            {
                CompareAndPushToQueue(output, ref tmp, clusterSize, bestIdx1, clusters[i], maxNumPairs, pairs, ref numPairs);
            }
        }

        return numClusters;
    }

    /// <summary>brotli's <c>BrotliHistogramBitCostDistance</c>: the bits moving a histogram into a candidate costs.</summary>
    public static double HistogramBitCostDistance<T>(ref T histogram, ref T candidate, ref T tmp)
        where T : struct, IHistogram
    {
        if (histogram.TotalCount == 0)
        {
            return 0.0;
        }

        Histogram.Sum(ref tmp, ref histogram, ref candidate);
        return PopulationCost(ref tmp) - candidate.BitCost;
    }

    /// <summary>brotli's <c>BrotliHistogramRemap</c>: each input to its best cluster, and the clusters rebuilt.</summary>
    private static void HistogramRemap<T>(Span<T> input, nuint inSize, Span<uint> clusters, nuint numClusters, Span<T> output, ref T tmp, Span<uint> symbols)
        where T : struct, IHistogram
    {
        for (int i = 0; i < (int)inSize; ++i)
        {
            uint bestOut = i == 0 ? symbols[0] : symbols[i - 1];
            double bestBits = HistogramBitCostDistance(ref input[i], ref output[(int)bestOut], ref tmp);
            for (int j = 0; j < (int)numClusters; ++j)
            {
                double curBits = HistogramBitCostDistance(ref input[i], ref output[(int)clusters[j]], ref tmp);
                if (curBits < bestBits)
                {
                    bestBits = curBits;
                    bestOut = clusters[j];
                }
            }

            symbols[i] = bestOut;
        }

        for (int i = 0; i < (int)numClusters; ++i)
        {
            Histogram.Clear(ref output[(int)clusters[i]]);
        }

        for (int i = 0; i < (int)inSize; ++i)
        {
            Histogram.AddHistogram(ref output[(int)symbols[i]], ref input[i]);
        }
    }

    /// <summary>brotli's <c>BrotliHistogramReindex</c>: the clusters renumbered in their first use's order.</summary>
    private static nuint HistogramReindex<T>(ClusterArena arena, Span<T> output, Span<uint> symbols, nuint length)
        where T : struct, IHistogram
    {
        const uint invalidIndex = uint.MaxValue;
        Span<uint> newIndex = arena.NewIndex(length);
        newIndex.Slice(0, (int)length).Fill(invalidIndex);
        uint nextIndex = 0;
        for (int i = 0; i < (int)length; ++i)
        {
            if (newIndex[(int)symbols[i]] == invalidIndex)
            {
                newIndex[(int)symbols[i]] = nextIndex;
                ++nextIndex;
            }
        }

        Span<T> tmp = arena.Histograms<T>(nextIndex);
        nextIndex = 0;
        for (int i = 0; i < (int)length; ++i)
        {
            if (newIndex[(int)symbols[i]] == nextIndex)
            {
                tmp[(int)nextIndex] = output[(int)symbols[i]];
                ++nextIndex;
            }

            symbols[i] = newIndex[(int)symbols[i]];
        }

        tmp.Slice(0, (int)nextIndex).CopyTo(output);
        return nextIndex;
    }

    /// <summary>
    /// brotli's <c>BrotliClusterHistograms</c>: <paramref name="input"/>'s histograms merged into at most
    /// <paramref name="maxHistograms"/>, in <paramref name="output"/>, with the map of each to its cluster.
    /// </summary>
    public static void ClusterHistograms<T>(ClusterArena arena, Span<T> input, nuint inSize, nuint maxHistograms, Span<T> output, out nuint outSize, Span<uint> histogramSymbols)
        where T : struct, IHistogram
    {
        Span<uint> clusterSize = arena.ClusterSize(inSize);
        Span<uint> clusters = arena.Clusters(inSize);
        nuint numClusters = 0;
        const nuint maxInputHistograms = 64;
        nuint pairsCapacity = maxInputHistograms * maxInputHistograms / 2;
        Span<HistogramPair> pairs = arena.Pairs(pairsCapacity + 1);
        ref T tmp = ref arena.Tmp<T>()[0];
        clusterSize.Slice(0, (int)inSize).Fill(1);
        for (int i = 0; i < (int)inSize; ++i)
        {
            output[i] = input[i];
            output[i].BitCost = PopulationCost(ref input[i]);
            histogramSymbols[i] = (uint)i;
        }

        for (nuint i = 0; i < inSize; i += maxInputHistograms)
        {
            nuint numToCombine = Math.Min(inSize - i, maxInputHistograms);
            for (nuint j = 0; j < numToCombine; ++j)
            {
                clusters[(int)(numClusters + j)] = (uint)(i + j);
            }

            nuint numNewClusters = HistogramCombine(output, ref tmp, clusterSize, histogramSymbols.Slice((int)i), clusters.Slice((int)numClusters), pairs, numToCombine, numToCombine, maxHistograms, pairsCapacity);
            numClusters += numNewClusters;
        }

        {
            // The second pass: a bounded number of pairs.
            nuint maxNumPairs = Math.Min(64 * numClusters, numClusters / 2 * numClusters);
            if (pairsCapacity < maxNumPairs + 1)
            {
                pairs = arena.GrowPairs(pairsCapacity + 1, maxNumPairs + 1);
            }

            numClusters = HistogramCombine(output, ref tmp, clusterSize, histogramSymbols, clusters, pairs, numClusters, inSize, maxHistograms, maxNumPairs);
        }

        HistogramRemap(input, inSize, clusters, numClusters, output, ref tmp, histogramSymbols);
        outSize = HistogramReindex(arena, output, histogramSymbols, inSize);
    }
}

/// <summary>The memory of the clustering: brotli's allocations, kept.</summary>
internal sealed class ClusterArena
{
    private readonly GrowableBuffer<uint> _clusterSize = new();
    private readonly GrowableBuffer<uint> _clusters = new();
    private readonly GrowableBuffer<uint> _newIndex = new();
    private readonly GrowableBuffer<HistogramPair> _pairs = new();
    private readonly GrowableBuffer<HistogramPair> _pairsGrown = new();
    private readonly TypedBuffer _histograms = new();
    private readonly TypedBuffer _tmp = new();

    public uint[] ClusterSize(nuint count) => _clusterSize.Ensure(count);

    public uint[] Clusters(nuint count) => _clusters.Ensure(count);

    public uint[] NewIndex(nuint count) => _newIndex.Ensure(count);

    public HistogramPair[] Pairs(nuint count) => _pairs.Ensure(count);

    /// <summary>brotli's <c>BROTLI_ENSURE_CAPACITY</c> of the pairs: a larger array, which keeps the first <paramref name="keep"/>.</summary>
    public HistogramPair[] GrowPairs(nuint keep, nuint count)
    {
        HistogramPair[] grown = _pairsGrown.Ensure(count);
        _pairs.Ensure(keep).AsSpan(0, (int)keep).CopyTo(grown);
        return grown;
    }

    /// <summary>At least <paramref name="count"/> histograms of <typeparamref name="T"/>.</summary>
    public T[] Histograms<T>(nuint count)
        where T : struct => _histograms.Ensure<T>(Math.Max(count, 1));

    /// <summary>A histogram of <typeparamref name="T"/>, to compute with.</summary>
    public T[] Tmp<T>()
        where T : struct => _tmp.Ensure<T>(1);
}
