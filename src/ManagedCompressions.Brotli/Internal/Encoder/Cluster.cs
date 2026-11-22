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
internal static unsafe class Cluster
{
    /// <summary>brotli's <c>BrotliPopulationCost</c>: the bits of a histogram's symbols and of its code.</summary>
    public static double PopulationCost<T>(T* histogram)
        where T : unmanaged, IHistogram
    {
        const double oneSymbolHistogramCost = 12;
        const double twoSymbolHistogramCost = 20;
        const double threeSymbolHistogramCost = 28;
        const double fourSymbolHistogramCost = 37;
        int dataSize = T.DataSize;
        uint* data = Histogram.Data(histogram);
        nuint totalCount = *Histogram.TotalCount(histogram);
        int count = 0;
        int* s = stackalloc int[5];
        double bits = 0.0;
        if (totalCount == 0)
        {
            return oneSymbolHistogramCost;
        }

        count = FirstNonZero(data, dataSize, s);

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
            uint* histo = stackalloc uint[4];
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
            uint* depthHisto = stackalloc uint[Prefix.NumCodeLengthCodes];
            new Span<uint>(depthHisto, Prefix.NumCodeLengthCodes).Clear();
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

                    ++depthHisto[depth];
                    ++i;
                }
                else
                {
                    uint reps = (uint)ZeroRun(data, i + 1, dataSize) + 1;

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
            bits += FastLog.BitsEntropy(depthHisto, Prefix.NumCodeLengthCodes);
        }

        return bits;
    }

    /// <summary>The indices of the first nonzero counts into <paramref name="s"/>, five at most: their number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FirstNonZero(uint* data, int size, int* s)
    {
        int count = 0;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= size; i += 8)
            {
                uint mask = ~Vector256.Equals(Vector256.Load(data + i), Vector256<uint>.Zero).ExtractMostSignificantBits() & 0xFF;
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
                uint mask = ~Vector128.Equals(Vector128.Load(data + i), Vector128<uint>.Zero).ExtractMostSignificantBits() & 0xF;
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
    private static int ZeroRun(uint* data, int from, int size)
    {
        int k = from;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; k + 8 <= size; k += 8)
            {
                uint nonZero = ~Vector256.Equals(Vector256.Load(data + k), Vector256<uint>.Zero).ExtractMostSignificantBits() & 0xFF;
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
                uint nonZero = ~Vector128.Equals(Vector128.Load(data + k), Vector128<uint>.Zero).ExtractMostSignificantBits() & 0xF;
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
    private static bool HistogramPairIsLess(HistogramPair* p1, HistogramPair* p2)
    {
        if (p1->CostDiff != p2->CostDiff)
        {
            return p1->CostDiff > p2->CostDiff;
        }

        return p1->Idx2 - p1->Idx1 > p2->Idx2 - p2->Idx1;
    }

    /// <summary>brotli's <c>ClusterCostDiff</c>: the context map's entropy saved by merging two clusters.</summary>
    private static double ClusterCostDiff(nuint sizeA, nuint sizeB)
    {
        nuint sizeC = sizeA + sizeB;
        return ((double)sizeA * FastLog.FastLog2(sizeA)) + ((double)sizeB * FastLog.FastLog2(sizeB)) - ((double)sizeC * FastLog.FastLog2(sizeC));
    }

    /// <summary>brotli's <c>BrotliCompareAndPushToQueue</c>.</summary>
    private static void CompareAndPushToQueue<T>(T* output, T* tmp, uint* clusterSize, uint idx1, uint idx2, nuint maxNumPairs, HistogramPair* pairs, ref nuint numPairs)
        where T : unmanaged, IHistogram
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

        p.Idx1 = idx1;
        p.Idx2 = idx2;
        p.CostCombo = 0;
        p.CostDiff = 0.5 * ClusterCostDiff(clusterSize[idx1], clusterSize[idx2]);
        p.CostDiff -= *Histogram.BitCost(output + idx1);
        p.CostDiff -= *Histogram.BitCost(output + idx2);
        if (*Histogram.TotalCount(output + idx1) == 0)
        {
            p.CostCombo = *Histogram.BitCost(output + idx2);
            isGoodPair = true;
        }
        else if (*Histogram.TotalCount(output + idx2) == 0)
        {
            p.CostCombo = *Histogram.BitCost(output + idx1);
            isGoodPair = true;
        }
        else
        {
            double threshold = numPairs == 0 ? 1e99 : Math.Max(0.0, pairs[0].CostDiff);
            Histogram.Sum(tmp, output + idx1, output + idx2);
            double costCombo = PopulationCost(tmp);
            if (costCombo < threshold - p.CostDiff)
            {
                p.CostCombo = costCombo;
                isGoodPair = true;
            }
        }

        if (isGoodPair)
        {
            p.CostDiff += p.CostCombo;
            if (numPairs > 0 && HistogramPairIsLess(pairs, &p))
            {
                // The new top of the queue.
                if (numPairs < maxNumPairs)
                {
                    pairs[numPairs] = pairs[0];
                    ++numPairs;
                }

                pairs[0] = p;
            }
            else if (numPairs < maxNumPairs)
            {
                pairs[numPairs] = p;
                ++numPairs;
            }
        }
    }

    /// <summary>brotli's <c>BrotliHistogramCombine</c>: merges the best pairs until none saves bits, or to <paramref name="maxClusters"/>.</summary>
    public static nuint HistogramCombine<T>(T* output, T* tmp, uint* clusterSize, uint* symbols, uint* clusters, HistogramPair* pairs, nuint numClusters, nuint symbolsSize, nuint maxClusters, nuint maxNumPairs)
        where T : unmanaged, IHistogram
    {
        double costDiffThreshold = 0.0;
        nuint minClusterSize = 1;
        nuint numPairs = 0;
        for (nuint idx1 = 0; idx1 < numClusters; ++idx1)
        {
            for (nuint idx2 = idx1 + 1; idx2 < numClusters; ++idx2)
            {
                CompareAndPushToQueue(output, tmp, clusterSize, clusters[idx1], clusters[idx2], maxNumPairs, pairs, ref numPairs);
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
            Histogram.AddHistogram(output + bestIdx1, output + bestIdx2);
            *Histogram.BitCost(output + bestIdx1) = pairs[0].CostCombo;
            clusterSize[bestIdx1] += clusterSize[bestIdx2];
            for (nuint i = 0; i < symbolsSize; ++i)
            {
                if (symbols[i] == bestIdx2)
                {
                    symbols[i] = bestIdx1;
                }
            }

            for (nuint i = 0; i < numClusters; ++i)
            {
                if (clusters[i] == bestIdx2)
                {
                    Buffer.MemoryCopy(clusters + i + 1, clusters + i, (numClusters - i - 1) * sizeof(uint), (numClusters - i - 1) * sizeof(uint));
                    break;
                }
            }

            --numClusters;
            {
                // The pairs of either cluster go.
                nuint copyToIdx = 0;
                for (nuint i = 0; i < numPairs; ++i)
                {
                    HistogramPair* p = pairs + i;
                    if (p->Idx1 == bestIdx1 || p->Idx2 == bestIdx1 || p->Idx1 == bestIdx2 || p->Idx2 == bestIdx2)
                    {
                        continue;
                    }

                    if (HistogramPairIsLess(pairs, p))
                    {
                        HistogramPair front = pairs[0];
                        pairs[0] = *p;
                        pairs[copyToIdx] = front;
                    }
                    else
                    {
                        pairs[copyToIdx] = *p;
                    }

                    ++copyToIdx;
                }

                numPairs = copyToIdx;
            }

            // The pairs of the merged cluster.
            for (nuint i = 0; i < numClusters; ++i)
            {
                CompareAndPushToQueue(output, tmp, clusterSize, bestIdx1, clusters[i], maxNumPairs, pairs, ref numPairs);
            }
        }

        return numClusters;
    }

    /// <summary>brotli's <c>BrotliHistogramBitCostDistance</c>: the bits moving a histogram into a candidate costs.</summary>
    public static double HistogramBitCostDistance<T>(T* histogram, T* candidate, T* tmp)
        where T : unmanaged, IHistogram
    {
        if (*Histogram.TotalCount(histogram) == 0)
        {
            return 0.0;
        }

        Histogram.Sum(tmp, histogram, candidate);
        return PopulationCost(tmp) - *Histogram.BitCost(candidate);
    }

    /// <summary>brotli's <c>BrotliHistogramRemap</c>: each input to its best cluster, and the clusters rebuilt.</summary>
    private static void HistogramRemap<T>(T* input, nuint inSize, uint* clusters, nuint numClusters, T* output, T* tmp, uint* symbols)
        where T : unmanaged, IHistogram
    {
        for (nuint i = 0; i < inSize; ++i)
        {
            uint bestOut = i == 0 ? symbols[0] : symbols[i - 1];
            double bestBits = HistogramBitCostDistance(input + i, output + bestOut, tmp);
            for (nuint j = 0; j < numClusters; ++j)
            {
                double curBits = HistogramBitCostDistance(input + i, output + clusters[j], tmp);
                if (curBits < bestBits)
                {
                    bestBits = curBits;
                    bestOut = clusters[j];
                }
            }

            symbols[i] = bestOut;
        }

        for (nuint i = 0; i < numClusters; ++i)
        {
            Histogram.Clear(output + clusters[i]);
        }

        for (nuint i = 0; i < inSize; ++i)
        {
            Histogram.AddHistogram(output + symbols[i], input + i);
        }
    }

    /// <summary>brotli's <c>BrotliHistogramReindex</c>: the clusters renumbered in their first use's order.</summary>
    private static nuint HistogramReindex<T>(ClusterArena arena, T* output, uint* symbols, nuint length)
        where T : unmanaged, IHistogram
    {
        const uint invalidIndex = uint.MaxValue;
        uint* newIndex = arena.NewIndex(length);
        for (nuint i = 0; i < length; ++i)
        {
            newIndex[i] = invalidIndex;
        }

        uint nextIndex = 0;
        for (nuint i = 0; i < length; ++i)
        {
            if (newIndex[symbols[i]] == invalidIndex)
            {
                newIndex[symbols[i]] = nextIndex;
                ++nextIndex;
            }
        }

        T* tmp = (T*)arena.Histograms((nuint)sizeof(T) * nextIndex);
        nextIndex = 0;
        for (nuint i = 0; i < length; ++i)
        {
            if (newIndex[symbols[i]] == nextIndex)
            {
                tmp[nextIndex] = output[symbols[i]];
                ++nextIndex;
            }

            symbols[i] = newIndex[symbols[i]];
        }

        for (nuint i = 0; i < nextIndex; ++i)
        {
            output[i] = tmp[i];
        }

        return nextIndex;
    }

    /// <summary>
    /// brotli's <c>BrotliClusterHistograms</c>: <paramref name="input"/>'s histograms merged into at most
    /// <paramref name="maxHistograms"/>, in <paramref name="output"/>, with the map of each to its cluster.
    /// </summary>
    public static void ClusterHistograms<T>(ClusterArena arena, T* input, nuint inSize, nuint maxHistograms, T* output, out nuint outSize, uint* histogramSymbols)
        where T : unmanaged, IHistogram
    {
        uint* clusterSize = arena.ClusterSize(inSize);
        uint* clusters = arena.Clusters(inSize);
        nuint numClusters = 0;
        const nuint maxInputHistograms = 64;
        nuint pairsCapacity = maxInputHistograms * maxInputHistograms / 2;
        HistogramPair* pairs = arena.Pairs(pairsCapacity + 1);
        T* tmp = (T*)arena.Tmp((nuint)sizeof(T));
        for (nuint i = 0; i < inSize; ++i)
        {
            clusterSize[i] = 1;
        }

        for (nuint i = 0; i < inSize; ++i)
        {
            output[i] = input[i];
            *Histogram.BitCost(output + i) = PopulationCost(input + i);
            histogramSymbols[i] = (uint)i;
        }

        for (nuint i = 0; i < inSize; i += maxInputHistograms)
        {
            nuint numToCombine = Math.Min(inSize - i, maxInputHistograms);
            for (nuint j = 0; j < numToCombine; ++j)
            {
                clusters[numClusters + j] = (uint)(i + j);
            }

            nuint numNewClusters = HistogramCombine(output, tmp, clusterSize, histogramSymbols + i, clusters + numClusters, pairs, numToCombine, numToCombine, maxHistograms, pairsCapacity);
            numClusters += numNewClusters;
        }

        {
            // The second pass: a bounded number of pairs.
            nuint maxNumPairs = Math.Min(64 * numClusters, numClusters / 2 * numClusters);
            if (pairsCapacity < maxNumPairs + 1)
            {
                pairs = arena.GrowPairs(pairsCapacity + 1, maxNumPairs + 1);
            }

            numClusters = HistogramCombine(output, tmp, clusterSize, histogramSymbols, clusters, pairs, numClusters, inSize, maxHistograms, maxNumPairs);
        }

        HistogramRemap(input, inSize, clusters, numClusters, output, tmp, histogramSymbols);
        outSize = HistogramReindex(arena, output, histogramSymbols, inSize);
    }
}

/// <summary>The memory of the clustering: brotli's allocations, pinned and kept.</summary>
internal sealed unsafe class ClusterArena
{
    private readonly GrowingBuffer<uint> _clusterSize = new();
    private readonly GrowingBuffer<uint> _clusters = new();
    private readonly GrowingBuffer<uint> _newIndex = new();
    private readonly GrowingBuffer<HistogramPair> _pairs = new();
    private readonly GrowingBuffer<HistogramPair> _pairsGrown = new();
    private readonly GrowingBuffer<byte> _histograms = new();
    private readonly GrowingBuffer<byte> _tmp = new();

    public uint* ClusterSize(nuint count) => _clusterSize.Ensure(count);

    public uint* Clusters(nuint count) => _clusters.Ensure(count);

    public uint* NewIndex(nuint count) => _newIndex.Ensure(count);

    public HistogramPair* Pairs(nuint count) => _pairs.Ensure(count);

    /// <summary>brotli's <c>BROTLI_ENSURE_CAPACITY</c> of the pairs: a larger array, which keeps the first <paramref name="keep"/>.</summary>
    public HistogramPair* GrowPairs(nuint keep, nuint count)
    {
        HistogramPair* grown = _pairsGrown.Ensure(count);
        Buffer.MemoryCopy(_pairs.Ensure(keep), grown, keep * (nuint)sizeof(HistogramPair), keep * (nuint)sizeof(HistogramPair));
        return grown;
    }

    public byte* Histograms(nuint bytes) => _histograms.Ensure(bytes);

    public byte* Tmp(nuint bytes) => _tmp.Ensure(bytes);
}
