using System;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's <c>BrotliBuildMetaBlock</c> (<c>c/enc/metablock.c</c>), qualities 10 and 11: the distance
/// parameters that cost the least, the block splits, and the context-modeled histograms clustered.
/// </summary>
internal sealed unsafe class HqMetaBlockBuilder
{
    private const int MaxNumberOfHistograms = 256;
    private const int MaxNpostfix = 3;

    private readonly BlockSplitter _splitter = new();
    private readonly ClusterArena _clusterArena = new();
    private readonly GrowingBuffer<HistogramDistance> _tmp = new();
    private readonly GrowingBuffer<HistogramLiteral> _literalHistograms = new();
    private readonly GrowingBuffer<HistogramDistance> _distanceHistograms = new();

    /// <summary>
    /// The meta-block's split into <paramref name="mb"/>; <paramref name="dist"/> becomes the distance
    /// parameters chosen, and the commands' distance codes are rewritten for them.
    /// </summary>
    public void Build(byte* ringBuffer, nuint pos, nuint mask, int quality, ref DistanceParams dist, byte prevByte, byte prevByte2, Command* cmds, nuint numCommands, int literalContextMode, MetaBlockSplit mb)
    {
        uint ndirectMsb = 0;
        bool checkOrig = true;
        double bestDistCost = 1e99;
        DistanceParams origParams = dist;
        HistogramDistance* tmp = _tmp.Ensure(1);
        for (uint npostfix = 0; npostfix <= MaxNpostfix; npostfix++)
        {
            for (; ndirectMsb < 16; ndirectMsb++)
            {
                uint ndirect = ndirectMsb << (int)npostfix;
                DistanceParams newParams = DistanceParams.Create(npostfix, ndirect);
                if (npostfix == origParams.PostfixBits && ndirect == origParams.NumDirectCodes)
                {
                    checkOrig = false;
                }

                bool skip = !ComputeDistanceCost(cmds, numCommands, origParams, newParams, out double distCost, tmp);
                if (skip || distCost > bestDistCost)
                {
                    break;
                }

                bestDistCost = distCost;
                dist = newParams;
            }

            if (ndirectMsb > 0)
            {
                ndirectMsb--;
            }

            ndirectMsb /= 2;
        }

        if (checkOrig)
        {
            ComputeDistanceCost(cmds, numCommands, origParams, origParams, out double distCost, tmp);
            if (distCost < bestDistCost)
            {
                dist = origParams;
            }
        }

        RecomputeDistancePrefixes(cmds, numCommands, origParams, dist);
        _splitter.SplitBlock(cmds, numCommands, ringBuffer, pos, mask, quality, mb.LiteralSplit, mb.CommandSplit, mb.DistanceSplit);

        // Context modeling: a histogram for each block type and context.
        nuint literalContextMultiplier = 1 << Prefix.LiteralContextBits;
        nuint literalHistogramsSize = mb.LiteralSplit.NumTypes * literalContextMultiplier;
        HistogramLiteral* literalHistograms = _literalHistograms.Ensure(literalHistogramsSize);
        Histogram.ClearAll(literalHistograms, literalHistogramsSize);
        nuint distanceHistogramsSize = mb.DistanceSplit.NumTypes << Prefix.DistanceContextBits;
        HistogramDistance* distanceHistograms = _distanceHistograms.Ensure(distanceHistogramsSize);
        Histogram.ClearAll(distanceHistograms, distanceHistogramsSize);
        mb.CommandHistogramsSize = mb.CommandSplit.NumTypes;
        HistogramCommand* commandHistograms = mb.AllocateCommandHistograms(mb.CommandHistogramsSize);
        Histogram.ClearAll(commandHistograms, mb.CommandHistogramsSize);
        BuildHistogramsWithContext(cmds, numCommands, mb.LiteralSplit, mb.CommandSplit, mb.DistanceSplit, ringBuffer, pos, mask, prevByte, prevByte2, literalContextMode, literalHistograms, commandHistograms, distanceHistograms);

        mb.LiteralContextMapSize = mb.LiteralSplit.NumTypes << Prefix.LiteralContextBits;
        uint* literalContextMap = mb.AllocateLiteralContextMap(mb.LiteralContextMapSize);
        HistogramLiteral* literalOut = mb.AllocateLiteralHistograms(mb.LiteralContextMapSize);
        Cluster.ClusterHistograms(_clusterArena, literalHistograms, literalHistogramsSize, MaxNumberOfHistograms, literalOut, out mb.LiteralHistogramsSize, literalContextMap);

        mb.DistanceContextMapSize = mb.DistanceSplit.NumTypes << Prefix.DistanceContextBits;
        uint* distanceContextMap = mb.AllocateDistanceContextMap(mb.DistanceContextMapSize);
        HistogramDistance* distanceOut = mb.AllocateDistanceHistograms(mb.DistanceContextMapSize);
        Cluster.ClusterHistograms(_clusterArena, distanceHistograms, mb.DistanceContextMapSize, MaxNumberOfHistograms, distanceOut, out mb.DistanceHistogramsSize, distanceContextMap);
    }

    /// <summary>brotli's <c>RecomputeDistancePrefixes</c>.</summary>
    private static void RecomputeDistancePrefixes(Command* cmds, nuint numCommands, in DistanceParams origParams, in DistanceParams newParams)
    {
        if (origParams.PostfixBits == newParams.PostfixBits && origParams.NumDirectCodes == newParams.NumDirectCodes)
        {
            return;
        }

        for (nuint i = 0; i < numCommands; ++i)
        {
            Command* cmd = cmds + i;
            if (Commands.CopyLen(cmd) != 0 && cmd->CmdPrefix >= 128)
            {
                Commands.PrefixEncodeCopyDistance(Commands.RestoreDistanceCode(cmd, origParams), newParams.NumDirectCodes, (int)newParams.PostfixBits, out cmd->DistPrefix, out cmd->DistExtra);
            }
        }
    }

    /// <summary>brotli's <c>ComputeDistanceCost</c>: the bits of the distances under other parameters, false when one does not fit them.</summary>
    private static bool ComputeDistanceCost(Command* cmds, nuint numCommands, in DistanceParams origParams, in DistanceParams newParams, out double cost, HistogramDistance* tmp)
    {
        bool equalParams = origParams.PostfixBits == newParams.PostfixBits && origParams.NumDirectCodes == newParams.NumDirectCodes;
        double extraBits = 0.0;
        cost = 0;
        Histogram.Clear(tmp);
        for (nuint i = 0; i < numCommands; i++)
        {
            Command* cmd = cmds + i;
            if (Commands.CopyLen(cmd) != 0 && cmd->CmdPrefix >= 128)
            {
                ushort distPrefix;
                if (equalParams)
                {
                    distPrefix = cmd->DistPrefix;
                }
                else
                {
                    uint distance = Commands.RestoreDistanceCode(cmd, origParams);
                    if (distance > newParams.MaxDistance)
                    {
                        return false;
                    }

                    Commands.PrefixEncodeCopyDistance(distance, newParams.NumDirectCodes, (int)newParams.PostfixBits, out distPrefix, out _);
                }

                Histogram.Add(tmp, (nuint)(distPrefix & 0x3FF));
                extraBits += distPrefix >> 10;
            }
        }

        cost = Cluster.PopulationCost(tmp) + extraBits;
        return true;
    }

    /// <summary>brotli's <c>BrotliBuildHistogramsWithContext</c>.</summary>
    private static void BuildHistogramsWithContext(Command* cmds, nuint numCommands, BlockSplit literalSplit, BlockSplit insertAndCopySplit, BlockSplit distSplit, byte* ringBuffer, nuint startPos, nuint mask, byte prevByte, byte prevByte2, int contextMode, HistogramLiteral* literalHistograms, HistogramCommand* insertAndCopyHistograms, HistogramDistance* copyDistHistograms)
    {
        nuint pos = startPos;
        BlockSplitIterator literalIt = new(literalSplit);
        BlockSplitIterator insertAndCopyIt = new(insertAndCopySplit);
        BlockSplitIterator distIt = new(distSplit);
        byte* lut = BitStream.ContextLut(contextMode);
        for (nuint i = 0; i < numCommands; ++i)
        {
            Command* cmd = cmds + i;
            insertAndCopyIt.Next();
            Histogram.Add(insertAndCopyHistograms + insertAndCopyIt.Type, cmd->CmdPrefix);
            for (nuint j = cmd->InsertLen; j != 0; --j)
            {
                literalIt.Next();
                nuint context = (literalIt.Type << Prefix.LiteralContextBits) + (nuint)(lut[prevByte] | lut[256 + prevByte2]);
                Histogram.Add(literalHistograms + context, ringBuffer[pos & mask]);
                prevByte2 = prevByte;
                prevByte = ringBuffer[pos & mask];
                ++pos;
            }

            uint copyLen = Commands.CopyLen(cmd);
            pos += copyLen;
            if (copyLen != 0)
            {
                prevByte2 = ringBuffer[(pos - 2) & mask];
                prevByte = ringBuffer[(pos - 1) & mask];
                if (cmd->CmdPrefix >= 128)
                {
                    distIt.Next();
                    nuint context = (distIt.Type << Prefix.DistanceContextBits) + Commands.DistanceContext(cmd);
                    Histogram.Add(copyDistHistograms + context, (nuint)(cmd->DistPrefix & 0x3FF));
                }
            }
        }
    }

    /// <summary>brotli's <c>BlockSplitIterator</c>.</summary>
    private struct BlockSplitIterator
    {
        private readonly byte* _types;
        private readonly uint* _lengths;
        private nuint _idx;
        private nuint _length;

        public BlockSplitIterator(BlockSplit split)
        {
            _types = split.Types;
            _lengths = split.Lengths;
            _idx = 0;
            Type = 0;
            _length = split.NumBlocks != 0 ? split.Lengths[0] : 0;
        }

        public nuint Type { get; private set; }

        public void Next()
        {
            if (_length == 0)
            {
                ++_idx;
                Type = _types[_idx];
                _length = _lengths[_idx];
            }

            --_length;
        }
    }
}
