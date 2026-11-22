using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedCompressions.Internal;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's <c>ZopfliNode</c>: the best way found to reach a position, its cost replaced by the distance
/// shortcut once evaluated, and by the next command's length once the path is chosen.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct ZopfliNode
{
    /// <summary>The copy length in the low 25 bits, and 9 plus the length less the length code in the high 7.</summary>
    [FieldOffset(0)]
    public uint Length;

    [FieldOffset(4)]
    public uint Distance;

    /// <summary>The insert length in the low 27 bits, and the short distance code plus 1 in the high 5.</summary>
    [FieldOffset(8)]
    public uint DcodeInsertLength;

    [FieldOffset(12)]
    public float Cost;

    [FieldOffset(12)]
    public uint Next;

    [FieldOffset(12)]
    public uint Shortcut;

    public readonly uint CopyLength => Length & 0x1FFFFFF;

    public readonly uint LengthCode => CopyLength + 9u - (Length >> 25);

    public readonly uint DistanceCode
    {
        get
        {
            uint shortCode = DcodeInsertLength >> 27;
            return shortCode == 0 ? Distance + Prefix.NumDistanceShortCodes - 1 : shortCode - 1;
        }
    }

    public readonly uint CommandLength => CopyLength + (DcodeInsertLength & 0x7FFFFFF);
}

/// <summary>
/// brotli's Zopfli (<c>c/enc/backward_references_hq.c</c>): the shortest path of commands under a cost
/// model, over every match the binary tree and the dictionary find; quality 11 runs it twice, its second
/// cost model taken from the first's commands.
/// </summary>
internal sealed unsafe class Zopfli
{
    private const float Infinity = 1.7e38f;
    private const int MaxEffectiveDistanceAlphabetSize = 544;
    private const int LongCopyQuickStep = 16384;
    private const int MaxZopfliLenQuality10 = 150;
    private const int MaxZopfliLenQuality11 = 325;

    /// <summary>The lengths <see cref="CopyLengthCodes"/> holds: from 2118 on, the copy code is 23.</summary>
    private const int CopyLengthCodesSize = 2118;

    private static readonly PinnedBuffer<byte> CopyLengthCodesArray = BuildCopyLengthCodes();
    private static readonly PinnedBuffer<ushort> CommandCodesArray = BuildCommandCodes();

    /// <summary><see cref="Commands.GetCopyLengthCode"/> of the lengths 2 to 2117.</summary>
    private static readonly byte* CopyLengthCodes = CopyLengthCodesArray.Pointer;

    /// <summary><see cref="Commands.CombineLengthCodes"/> of each insert code times 24 plus copy code, without the last distance.</summary>
    private static readonly ushort* CommandCodes = CommandCodesArray.Pointer;

    private static PinnedBuffer<byte> BuildCopyLengthCodes()
    {
        PinnedBuffer<byte> codes = PinnedBuffer<byte>.Allocate(CopyLengthCodesSize);
        for (int len = 2; len < CopyLengthCodesSize; len++)
        {
            codes[len] = (byte)Commands.GetCopyLengthCode((nuint)len);
        }

        return codes;
    }

    private static PinnedBuffer<ushort> BuildCommandCodes()
    {
        PinnedBuffer<ushort> codes = PinnedBuffer<ushort>.Allocate(24 * 24);
        for (uint insCode = 0; insCode < 24; insCode++)
        {
            for (uint copyCode = 0; copyCode < 24; copyCode++)
            {
                codes[(insCode * 24) + copyCode] = Commands.CombineLengthCodes(insCode, copyCode, false);
            }
        }

        return codes;
    }

    private static ReadOnlySpan<byte> DistanceCacheIndex => [0, 1, 2, 3, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1];

    private static ReadOnlySpan<sbyte> DistanceCacheOffset => [0, 0, 0, 0, -1, 1, -2, 2, -3, 3, -1, 1, -2, 2, -3, 3];

    private readonly GrowingBuffer<ZopfliNode> _nodes = new();
    private readonly GrowingBuffer<BackwardMatch> _matches = new();
    private readonly GrowingBuffer<uint> _numMatches = new();
    private readonly GrowingBuffer<float> _literalCosts = new();
    private readonly GrowingBuffer<float> _costDist = new();
    private readonly GrowingBuffer<float> _costCmd = new();
    private readonly GrowingBuffer<nuint> _literalHistograms = new();
    private readonly GrowingBuffer<uint> _arena = new();
    private readonly GrowingBuffer<PosData> _queue = new();

    private int _quality;
    private int _lgwin;
    private DistanceParams _dist;

    // The cost model: brotli's ZopfliCostModel.
    private float* _costCmdP;
    private float* _costDistP;
    private uint _distanceHistogramSize;
    private float* _literalCostsP;
    private float _minCostCmd;
    private nuint _numBytes;

    /// <summary>brotli's <c>PosData</c>.</summary>
    private struct PosData
    {
        public nuint Pos;
        public int DistanceCache0;
        public int DistanceCache1;
        public int DistanceCache2;
        public int DistanceCache3;
        public float Costdiff;
        public float Cost;
    }

    /// <summary>brotli's <c>StartPosQueue</c>: the 8 start positions of the smallest cost differences.</summary>
    private struct StartPosQueue
    {
        public PosData* Q;
        public nuint Idx;

        public readonly nuint Size => Math.Min(Idx, 8);

        public void Push(PosData* posdata)
        {
            nuint offset = ~Idx++ & 7;
            nuint len = Size;
            PosData* q = Q;
            q[offset] = *posdata;
            for (nuint i = 1; i < len; ++i)
            {
                if (q[offset & 7].Costdiff > q[(offset + 1) & 7].Costdiff)
                {
                    (q[offset & 7], q[(offset + 1) & 7]) = (q[(offset + 1) & 7], q[offset & 7]);
                }

                ++offset;
            }
        }

        public readonly PosData* At(nuint k) => Q + ((k - Idx) & 7);
    }

    public void Setup(int quality, int lgwin)
    {
        _quality = quality;
        _lgwin = lgwin;
    }

    private nuint MaxZopfliLen => _quality <= 10 ? (nuint)MaxZopfliLenQuality10 : MaxZopfliLenQuality11;

    private nuint MaxZopfliCandidates => _quality <= 10 ? 1u : 5u;

    private nuint MaxBackwardLimit => ((nuint)1 << _lgwin) - Prefix.WindowGap;

    private static void InitZopfliNodes(ZopfliNode* array, nuint length)
    {
        ZopfliNode stub = default;
        stub.Length = 1;
        stub.Distance = 0;
        stub.DcodeInsertLength = 0;
        stub.Cost = Infinity;
        for (nuint i = 0; i < length; ++i)
        {
            array[i] = stub;
        }
    }

    /// <summary>brotli's <c>InitZopfliCostModel</c>.</summary>
    private void InitCostModel(nuint numBytes)
    {
        _numBytes = numBytes;
        _literalCostsP = _literalCosts.Ensure(numBytes + 2);
        _costDistP = _costDist.Ensure(_dist.AlphabetSizeLimit);
        _costCmdP = _costCmd.Ensure(Prefix.NumCommandSymbols);
        _distanceHistogramSize = _dist.AlphabetSizeLimit;
    }

    /// <summary>brotli's <c>SetCost</c>: the bits of each symbol of a histogram, as floats.</summary>
    private static void SetCost(uint* histogram, nuint histogramSize, bool literalHistogram, float* cost)
    {
        nuint sum = 0;
        for (nuint i = 0; i < histogramSize; i++)
        {
            sum += histogram[i];
        }

        float log2Sum = (float)FastLog.FastLog2(sum);
        nuint missingSymbolSum = sum;
        if (!literalHistogram)
        {
            for (nuint i = 0; i < histogramSize; i++)
            {
                if (histogram[i] == 0)
                {
                    missingSymbolSum++;
                }
            }
        }

        float missingSymbolCost = (float)FastLog.FastLog2(missingSymbolSum) + 2;
        for (nuint i = 0; i < histogramSize; i++)
        {
            if (histogram[i] == 0)
            {
                cost[i] = missingSymbolCost;
                continue;
            }

            cost[i] = log2Sum - (float)FastLog.FastLog2(histogram[i]);
            if (cost[i] < 1)
            {
                cost[i] = 1;
            }
        }
    }

    /// <summary>brotli's <c>ZopfliCostModelSetFromCommands</c>: the costs of the histograms of the first pass's commands.</summary>
    private void SetCostModelFromCommands(nuint position, byte* ringBuffer, nuint ringBufferMask, Command* commands, nuint numCommands, nuint lastInsertLen)
    {
        uint* arena = _arena.Ensure(Prefix.NumLiteralSymbols + Prefix.NumCommandSymbols + MaxEffectiveDistanceAlphabetSize + Prefix.NumLiteralSymbols);
        uint* histogramLiteral = arena;
        uint* histogramCmd = histogramLiteral + Prefix.NumLiteralSymbols;
        uint* histogramDist = histogramCmd + Prefix.NumCommandSymbols;
        float* costLiteral = (float*)(histogramDist + MaxEffectiveDistanceAlphabetSize);
        nuint pos = position - lastInsertLen;
        float minCostCmd = Infinity;
        float* costCmd = _costCmdP;
        new Span<uint>(arena, Prefix.NumLiteralSymbols + Prefix.NumCommandSymbols + MaxEffectiveDistanceAlphabetSize).Clear();
        for (nuint i = 0; i < numCommands; i++)
        {
            nuint insLength = commands[i].InsertLen;
            nuint copyLength = Commands.CopyLen(commands + i);
            nuint distCode = (nuint)(commands[i].DistPrefix & 0x3FF);
            nuint cmdCode = commands[i].CmdPrefix;
            histogramCmd[cmdCode]++;
            if (cmdCode >= 128)
            {
                histogramDist[distCode]++;
            }

            for (nuint j = 0; j < insLength; j++)
            {
                histogramLiteral[ringBuffer[(pos + j) & ringBufferMask]]++;
            }

            pos += insLength + copyLength;
        }

        SetCost(histogramLiteral, Prefix.NumLiteralSymbols, true, costLiteral);
        SetCost(histogramCmd, Prefix.NumCommandSymbols, false, costCmd);
        SetCost(histogramDist, _distanceHistogramSize, false, _costDistP);
        for (nuint i = 0; i < Prefix.NumCommandSymbols; ++i)
        {
            minCostCmd = Math.Min(minCostCmd, costCmd[i]);
        }

        _minCostCmd = minCostCmd;
        {
            float* literalCosts = _literalCostsP;
            float literalCarry = 0.0f;
            nuint numBytes = _numBytes;
            literalCosts[0] = 0.0f;
            for (nuint i = 0; i < numBytes; ++i)
            {
                literalCarry += costLiteral[ringBuffer[(position + i) & ringBufferMask]];
                literalCosts[i + 1] = literalCosts[i] + literalCarry;
                literalCarry -= literalCosts[i + 1] - literalCosts[i];
            }
        }
    }

    /// <summary>brotli's <c>ZopfliCostModelSetFromLiteralCosts</c>: the literals' estimated costs, and fixed command and distance costs.</summary>
    private void SetCostModelFromLiteralCosts(nuint position, byte* ringBuffer, nuint ringBufferMask)
    {
        float* literalCosts = _literalCostsP;
        float literalCarry = 0.0f;
        float* costDist = _costDistP;
        float* costCmd = _costCmdP;
        nuint numBytes = _numBytes;
        LiteralCost.EstimateBitCostsForLiterals(position, numBytes, ringBufferMask, ringBuffer, _literalHistograms.Ensure(3 * 256), literalCosts + 1);
        literalCosts[0] = 0.0f;
        for (nuint i = 0; i < numBytes; ++i)
        {
            literalCarry += literalCosts[i + 1];
            literalCosts[i + 1] = literalCosts[i] + literalCarry;
            literalCarry -= literalCosts[i + 1] - literalCosts[i];
        }

        for (uint i = 0; i < Prefix.NumCommandSymbols; ++i)
        {
            costCmd[i] = (float)FastLog.FastLog2(11 + i);
        }

        for (uint i = 0; i < _distanceHistogramSize; ++i)
        {
            costDist[i] = (float)FastLog.FastLog2(20 + i);
        }

        _minCostCmd = (float)FastLog.FastLog2(11);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetLiteralCosts(nuint from, nuint to) => _literalCostsP[to] - _literalCostsP[from];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateZopfliNode(ZopfliNode* nodes, nuint pos, nuint startPos, nuint len, nuint lenCode, nuint dist, nuint shortCode, float cost)
    {
        ZopfliNode* next = nodes + pos + len;
        next->Length = (uint)(len | ((len + 9u - lenCode) << 25));
        next->Distance = (uint)dist;
        next->DcodeInsertLength = (uint)((shortCode << 27) | (pos - startPos));
        next->Cost = cost;
    }

    /// <summary>brotli's <c>ComputeMinimumCopyLength</c>: the shortest copy that can improve a later position.</summary>
    private static nuint ComputeMinimumCopyLength(float startCost, ZopfliNode* nodes, nuint numBytes, nuint pos)
    {
        float minCost = startCost;
        nuint len = 2;
        nuint nextLenBucket = 4;
        nuint nextLenOffset = 10;
        while (pos + len <= numBytes && nodes[pos + len].Cost <= minCost)
        {
            ++len;
            if (len == nextLenOffset)
            {
                // One more extra bit for the next copy length code.
                minCost += 1.0f;
                nextLenOffset += nextLenBucket;
                nextLenBucket *= 2;
            }
        }

        return len;
    }

    /// <summary>brotli's <c>ComputeDistanceShortcut</c>: the last position whose command updated the distances.</summary>
    private static uint ComputeDistanceShortcut(nuint blockStart, nuint pos, nuint maxBackwardLimit, nuint gap, ZopfliNode* nodes)
    {
        nuint clen = nodes[pos].CopyLength;
        nuint ilen = nodes[pos].DcodeInsertLength & 0x7FFFFFF;
        nuint dist = nodes[pos].Distance;
        if (pos == 0)
        {
            return 0;
        }

        if (dist + clen <= blockStart + pos + gap && dist <= maxBackwardLimit + gap && nodes[pos].DistanceCode > 0)
        {
            return (uint)pos;
        }

        return nodes[pos - clen - ilen].Shortcut;
    }

    /// <summary>brotli's <c>ComputeDistanceCache</c>: the last four distances at a position, on its best path.</summary>
    private static void ComputeDistanceCache(nuint pos, int* startingDistCache, ZopfliNode* nodes, int* distCache)
    {
        int idx = 0;
        nuint p = nodes[pos].Shortcut;
        while (idx < 4 && p > 0)
        {
            nuint ilen = nodes[p].DcodeInsertLength & 0x7FFFFFF;
            nuint clen = nodes[p].CopyLength;
            nuint dist = nodes[p].Distance;
            distCache[idx++] = (int)dist;
            p = nodes[p - clen - ilen].Shortcut;
        }

        for (; idx < 4; ++idx)
        {
            distCache[idx] = *startingDistCache++;
        }
    }

    /// <summary>brotli's <c>EvaluateNode</c>: the shortcut of a reached position, and the position into the queue when it is cheap enough.</summary>
    private void EvaluateNode(nuint blockStart, nuint pos, nuint maxBackwardLimit, nuint gap, int* startingDistCache, ref StartPosQueue queue, ZopfliNode* nodes)
    {
        float nodeCost = nodes[pos].Cost;
        nodes[pos].Shortcut = ComputeDistanceShortcut(blockStart, pos, maxBackwardLimit, gap, nodes);
        if (nodeCost <= GetLiteralCosts(0, pos))
        {
            PosData posdata;
            posdata.Pos = pos;
            posdata.Cost = nodeCost;
            posdata.Costdiff = nodeCost - GetLiteralCosts(0, pos);
            ComputeDistanceCache(pos, startingDistCache, nodes, &posdata.DistanceCache0);
            queue.Push(&posdata);
        }
    }

    /// <summary>brotli's <c>UpdateNodes</c>: the positions a command from the queue's start positions reaches; the longest copy.</summary>
    private nuint UpdateNodes(nuint numBytes, nuint blockStart, nuint pos, byte* ringBuffer, nuint ringBufferMask, nuint maxBackwardLimit, int* startingDistCache, nuint numMatches, BackwardMatch* matches, ref StartPosQueue queue, ZopfliNode* nodes)
    {
        nuint curIx = blockStart + pos;
        nuint curIxMasked = curIx & ringBufferMask;
        nuint maxDistance = Math.Min(curIx, maxBackwardLimit);
        nuint dictionaryStart = Math.Min(curIx, maxBackwardLimit);
        nuint maxLen = numBytes - pos;
        nuint maxZopfliLen = MaxZopfliLen;
        nuint maxIters = MaxZopfliCandidates;
        nuint result = 0;
        const nuint gap = 0;
        ReadOnlySpan<byte> distanceCacheIndex = DistanceCacheIndex;
        ReadOnlySpan<sbyte> distanceCacheOffset = DistanceCacheOffset;

        // The model in locals: the JIT reloads fields after every store through a pointer.
        float* costCmd = _costCmdP;
        float* costDist = _costDistP;
        float* literalCosts = _literalCostsP;
        nuint numDirectCodes = _dist.NumDirectCodes;
        int postfixBits = (int)_dist.PostfixBits;
        byte* copyCodes = CopyLengthCodes;
        ushort* commandCodes = CommandCodes;
        EvaluateNode(blockStart, pos, maxBackwardLimit, gap, startingDistCache, ref queue, nodes);
        nuint minLen;
        {
            PosData* posdata = queue.At(0);
            float minCost = posdata->Cost + _minCostCmd + (literalCosts[pos] - literalCosts[posdata->Pos]);
            minLen = ComputeMinimumCopyLength(minCost, nodes, numBytes, pos);
        }

        // The start positions, by increasing cost difference.
        for (nuint k = 0; k < maxIters && k < queue.Size; ++k)
        {
            PosData* posdata = queue.At(k);
            nuint start = posdata->Pos;
            uint insCode = Commands.GetInsertLengthCode(pos - start);
            float startCostdiff = posdata->Costdiff;
            float baseCost = startCostdiff + (float)Commands.GetInsertExtra(insCode) + (literalCosts[pos] - literalCosts[0]);

            // The last distances, from this start position's cache.
            nuint bestLen = minLen - 1;
            int* cache = &posdata->DistanceCache0;
            for (int j = 0; j < Prefix.NumDistanceShortCodes && bestLen < maxLen; ++j)
            {
                nuint idx = distanceCacheIndex[j];
                nuint backward = (nuint)(nint)(cache[idx] + distanceCacheOffset[j]);
                nuint prevIx = curIx - backward;
                nuint len = 0;
                byte continuation = ringBuffer[curIxMasked + bestLen];
                if (curIxMasked + bestLen > ringBufferMask)
                {
                    break;
                }

                if (backward > dictionaryStart + gap)
                {
                    // A dictionary word: not here.
                    continue;
                }

                if (backward <= maxDistance)
                {
                    if (prevIx >= curIx)
                    {
                        continue;
                    }

                    prevIx &= ringBufferMask;
                    if (prevIx + bestLen > ringBufferMask || continuation != ringBuffer[prevIx + bestLen])
                    {
                        continue;
                    }

                    len = FindMatchLength.WithLimit(ringBuffer + prevIx, ringBuffer + curIxMasked, maxLen);
                }
                else
                {
                    // Past the window and before the dictionary: there is no compound dictionary.
                    continue;
                }

                {
                    float distCost = baseCost + costDist[j];
                    for (nuint l = bestLen + 1; l <= len; ++l)
                    {
                        uint copyCode = l < CopyLengthCodesSize ? copyCodes[l] : 23u;
                        ushort cmdCode = Commands.CombineLengthCodes(insCode, copyCode, j == 0);
                        float cost = (cmdCode < 128 ? baseCost : distCost) + (float)Commands.GetCopyExtra(copyCode) + costCmd[cmdCode];
                        if (cost < nodes[pos + l].Cost)
                        {
                            UpdateZopfliNode(nodes, pos, start, l, l, backward, (nuint)j + 1, cost);
                            result = Math.Max(result, l);
                        }

                        bestLen = l;
                    }
                }
            }

            // From the third start position on, only the last distances.
            if (k >= 2)
            {
                continue;
            }

            {
                nuint len = minLen;
                for (nuint j = 0; j < numMatches; ++j)
                {
                    BackwardMatch match = matches[j];
                    nuint dist = match.Distance;
                    bool isDictionaryMatch = dist > dictionaryStart + gap;
                    nuint distCode = dist + Prefix.NumDistanceShortCodes - 1;
                    Commands.PrefixEncodeCopyDistance(distCode, numDirectCodes, postfixBits, out ushort distSymbol, out _);
                    uint distNumExtra = (uint)distSymbol >> 10;
                    float distCost = baseCost + (float)distNumExtra + costDist[distSymbol & 0x3FF];

                    // Every length up to the match's; only the longest for a dictionary word or a long match.
                    nuint maxMatchLen = match.Length;
                    if (len < maxMatchLen && (isDictionaryMatch || maxMatchLen > maxZopfliLen))
                    {
                        len = maxMatchLen;
                    }

                    for (; len <= maxMatchLen; ++len)
                    {
                        nuint lenCode = isDictionaryMatch ? match.LengthCode : len;
                        uint copyCode = lenCode < CopyLengthCodesSize ? copyCodes[lenCode] : 23u;
                        ushort cmdCode = commandCodes[(insCode * 24) + copyCode];
                        float cost = distCost + (float)Commands.GetCopyExtra(copyCode) + costCmd[cmdCode];
                        if (cost < nodes[pos + len].Cost)
                        {
                            UpdateZopfliNode(nodes, pos, start, len, lenCode, dist, 0, cost);
                            result = Math.Max(result, len);
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>brotli's <c>ComputeShortestPathFromNodes</c>: the chosen path, each node's next command length; the number of commands.</summary>
    private static nuint ComputeShortestPathFromNodes(nuint numBytes, ZopfliNode* nodes)
    {
        nuint index = numBytes;
        nuint numCommands = 0;
        while ((nodes[index].DcodeInsertLength & 0x7FFFFFF) == 0 && nodes[index].Length == 1)
        {
            --index;
        }

        nodes[index].Next = uint.MaxValue;
        while (index != 0)
        {
            nuint len = nodes[index].CommandLength;
            index -= len;
            nodes[index].Next = (uint)len;
            numCommands++;
        }

        return numCommands;
    }

    /// <summary>brotli's <c>BrotliZopfliCreateCommands</c>.</summary>
    private void CreateCommands(nuint numBytes, nuint blockStart, ZopfliNode* nodes, int* distCache, ref nuint lastInsertLen, Command* commands, ref nuint numLiterals)
    {
        nuint maxBackwardLimit = MaxBackwardLimit;
        nuint pos = 0;
        uint offset = nodes[0].Next;
        const nuint gap = 0;
        for (nuint i = 0; offset != uint.MaxValue; i++)
        {
            ZopfliNode* next = nodes + pos + offset;
            nuint copyLength = next->CopyLength;
            nuint insertLength = next->DcodeInsertLength & 0x7FFFFFF;
            pos += insertLength;
            offset = next->Next;
            if (i == 0)
            {
                insertLength += lastInsertLen;
                lastInsertLen = 0;
            }

            {
                nuint distance = next->Distance;
                nuint lenCode = next->LengthCode;
                nuint dictionaryStart = Math.Min(blockStart + pos, maxBackwardLimit);
                bool isDictionary = distance > dictionaryStart + gap;
                nuint distCode = next->DistanceCode;
                Commands.Init(commands + i, _dist, insertLength, copyLength, (int)lenCode - (int)copyLength, distCode);
                if (!isDictionary && distCode > 0)
                {
                    distCache[3] = distCache[2];
                    distCache[2] = distCache[1];
                    distCache[1] = distCache[0];
                    distCache[0] = (int)distance;
                }
            }

            numLiterals += insertLength;
            pos += copyLength;
        }

        lastInsertLen += numBytes - pos;
    }

    /// <summary>brotli's <c>ZopfliIterate</c>: a pass over matches found before.</summary>
    private nuint ZopfliIterate(nuint numBytes, nuint position, byte* ringBuffer, nuint ringBufferMask, int* distCache, uint* numMatches, BackwardMatch* matches, ZopfliNode* nodes)
    {
        nuint maxBackwardLimit = MaxBackwardLimit;
        nuint maxZopfliLen = MaxZopfliLen;
        StartPosQueue queue;
        queue.Q = _queue.Ensure(8);
        queue.Idx = 0;
        nuint curMatchPos = 0;
        const nuint gap = 0;
        nodes[0].Length = 0;
        nodes[0].Cost = 0;
        for (nuint i = 0; i + 3 < numBytes; i++)
        {
            nuint skip = UpdateNodes(numBytes, position, i, ringBuffer, ringBufferMask, maxBackwardLimit, distCache, numMatches[i], matches + curMatchPos, ref queue, nodes);
            if (skip < LongCopyQuickStep)
            {
                skip = 0;
            }

            curMatchPos += numMatches[i];
            if (numMatches[i] == 1 && matches[curMatchPos - 1].Length > maxZopfliLen)
            {
                skip = Math.Max(matches[curMatchPos - 1].Length, skip);
            }

            if (skip > 1)
            {
                skip--;
                while (skip != 0)
                {
                    i++;
                    if (i + 3 >= numBytes)
                    {
                        break;
                    }

                    EvaluateNode(position, i, maxBackwardLimit, gap, distCache, ref queue, nodes);
                    curMatchPos += numMatches[i];
                    skip--;
                }
            }
        }

        return ComputeShortestPathFromNodes(numBytes, nodes);
    }

    /// <summary>brotli's <c>BrotliZopfliComputeShortestPath</c>: one pass, the matches found as it goes.</summary>
    private nuint ComputeShortestPath(nuint numBytes, nuint position, byte* ringBuffer, nuint ringBufferMask, int* distCache, ref HashToBinaryTree hasher, ZopfliNode* nodes)
    {
        nuint maxBackwardLimit = MaxBackwardLimit;
        nuint maxZopfliLen = MaxZopfliLen;
        StartPosQueue queue;
        queue.Q = _queue.Ensure(8);
        queue.Idx = 0;
        BackwardMatch* matches = _matches.Ensure(2 * (HashToBinaryTree.MaxNumMatches + 64));
        nuint storeEnd = numBytes >= HashToBinaryTree.StoreLookahead ? position + numBytes - HashToBinaryTree.StoreLookahead + 1 : position;
        const nuint gap = 0;
        nodes[0].Length = 0;
        nodes[0].Cost = 0;
        InitCostModel(numBytes);
        SetCostModelFromLiteralCosts(position, ringBuffer, ringBufferMask);
        for (nuint i = 0; i + HashToBinaryTree.HashTypeLength - 1 < numBytes; i++)
        {
            nuint pos = position + i;
            nuint maxDistance = Math.Min(pos, maxBackwardLimit);
            nuint dictionaryStart = Math.Min(pos, maxBackwardLimit);
            nuint numFound = hasher.FindAllMatches(ringBuffer, ringBufferMask, pos, numBytes - i, maxDistance, dictionaryStart + gap, _quality, _dist.MaxDistance, matches);
            if (numFound > 0 && matches[numFound - 1].Length > maxZopfliLen)
            {
                matches[0] = matches[numFound - 1];
                numFound = 1;
            }

            nuint skip = UpdateNodes(numBytes, position, i, ringBuffer, ringBufferMask, maxBackwardLimit, distCache, numFound, matches, ref queue, nodes);
            if (skip < LongCopyQuickStep)
            {
                skip = 0;
            }

            if (numFound == 1 && matches[0].Length > maxZopfliLen)
            {
                skip = Math.Max(matches[0].Length, skip);
            }

            if (skip > 1)
            {
                // The copy's tail into the hasher.
                hasher.StoreRange(ringBuffer, ringBufferMask, pos + 1, Math.Min(pos + skip, storeEnd));
                skip--;
                while (skip != 0)
                {
                    i++;
                    if (i + HashToBinaryTree.HashTypeLength - 1 >= numBytes)
                    {
                        break;
                    }

                    EvaluateNode(position, i, maxBackwardLimit, gap, distCache, ref queue, nodes);
                    skip--;
                }
            }
        }

        return ComputeShortestPathFromNodes(numBytes, nodes);
    }

    /// <summary>brotli's <c>BrotliCreateZopfliBackwardReferences</c>, quality 10.</summary>
    public void CreateBackwardReferences(nuint numBytes, nuint position, byte* ringBuffer, nuint ringBufferMask, in DistanceParams dist, ref HashToBinaryTree hasher, int* distCache, ref nuint lastInsertLen, Command* commands, ref nuint numCommands, ref nuint numLiterals)
    {
        _dist = dist;
        ZopfliNode* nodes = _nodes.Ensure(numBytes + 1);
        InitZopfliNodes(nodes, numBytes + 1);
        numCommands += ComputeShortestPath(numBytes, position, ringBuffer, ringBufferMask, distCache, ref hasher, nodes);
        CreateCommands(numBytes, position, nodes, distCache, ref lastInsertLen, commands, ref numLiterals);
    }

    /// <summary>brotli's <c>BrotliCreateHqZopfliBackwardReferences</c>, quality 11: every match first, then two passes.</summary>
    public void CreateHqBackwardReferences(nuint numBytes, nuint position, byte* ringBuffer, nuint ringBufferMask, in DistanceParams dist, ref HashToBinaryTree hasher, int* distCache, ref nuint lastInsertLen, Command* commands, ref nuint numCommands, ref nuint numLiterals)
    {
        _dist = dist;
        nuint maxBackwardLimit = MaxBackwardLimit;
        uint* numMatches = _numMatches.Ensure(numBytes);
        nuint matchesSize = 4 * numBytes;
        nuint storeEnd = numBytes >= HashToBinaryTree.StoreLookahead ? position + numBytes - HashToBinaryTree.StoreLookahead + 1 : position;
        nuint curMatchPos = 0;
        const nuint gap = 0;
        BackwardMatch* matches = _matches.Ensure(matchesSize);
        int* origDistCache = stackalloc int[4];
        for (nuint i = 0; i + HashToBinaryTree.HashTypeLength - 1 < numBytes; ++i)
        {
            nuint pos = position + i;
            nuint maxDistance = Math.Min(pos, maxBackwardLimit);
            nuint dictionaryStart = Math.Min(pos, maxBackwardLimit);
            nuint maxLength = numBytes - i;
            if (matchesSize < curMatchPos + HashToBinaryTree.MaxNumMatches)
            {
                // brotli's BROTLI_ENSURE_CAPACITY, which keeps the matches found.
                nuint newSize = matchesSize == 0 ? curMatchPos + HashToBinaryTree.MaxNumMatches : matchesSize;
                while (newSize < curMatchPos + HashToBinaryTree.MaxNumMatches)
                {
                    newSize *= 2;
                }

                BackwardMatch[] kept = new ReadOnlySpan<BackwardMatch>(matches, (int)curMatchPos).ToArray();
                matches = _matches.Ensure(newSize);
                kept.CopyTo(new Span<BackwardMatch>(matches, (int)curMatchPos));
                matchesSize = newSize;
            }

            nuint numFoundMatches = hasher.FindAllMatches(ringBuffer, ringBufferMask, pos, maxLength, maxDistance, dictionaryStart + gap, _quality, _dist.MaxDistance, matches + curMatchPos);
            nuint curMatchEnd = curMatchPos + numFoundMatches;
            numMatches[i] = (uint)numFoundMatches;
            if (numFoundMatches > 0)
            {
                nuint matchLen = matches[curMatchEnd - 1].Length;
                if (matchLen > MaxZopfliLenQuality11)
                {
                    nuint skip = matchLen - 1;
                    matches[curMatchPos++] = matches[curMatchEnd - 1];
                    numMatches[i] = 1;

                    // The copy's tail into the hasher.
                    hasher.StoreRange(ringBuffer, ringBufferMask, pos + 1, Math.Min(pos + matchLen, storeEnd));
                    new Span<uint>(numMatches + i + 1, (int)skip).Clear();
                    i += skip;
                }
                else
                {
                    curMatchPos = curMatchEnd;
                }
            }
        }

        nuint origNumLiterals = numLiterals;
        nuint origLastInsertLen = lastInsertLen;
        new Span<int>(distCache, 4).CopyTo(new Span<int>(origDistCache, 4));
        nuint origNumCommands = numCommands;
        ZopfliNode* nodes = _nodes.Ensure(numBytes + 1);
        InitCostModel(numBytes);
        for (int i = 0; i < 2; i++)
        {
            InitZopfliNodes(nodes, numBytes + 1);
            if (i == 0)
            {
                SetCostModelFromLiteralCosts(position, ringBuffer, ringBufferMask);
            }
            else
            {
                SetCostModelFromCommands(position, ringBuffer, ringBufferMask, commands, numCommands - origNumCommands, origLastInsertLen);
            }

            numCommands = origNumCommands;
            numLiterals = origNumLiterals;
            lastInsertLen = origLastInsertLen;
            new Span<int>(origDistCache, 4).CopyTo(new Span<int>(distCache, 4));
            numCommands += ZopfliIterate(numBytes, position, ringBuffer, ringBufferMask, distCache, numMatches, matches, nodes);
            CreateCommands(numBytes, position, nodes, distCache, ref lastInsertLen, commands, ref numLiterals);
        }
    }
}
