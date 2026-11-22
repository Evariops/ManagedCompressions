using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>An array on the pinned heap, grown when needed and kept.</summary>
internal sealed unsafe class PinnedBuffer<T>
    where T : unmanaged
{
    private T[] _array = [];

    /// <summary>The buffer, of at least <paramref name="count"/> items; its content is not kept when it grows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T* Ensure(nuint count)
    {
        if ((nuint)_array.Length < count)
        {
            Grow(count);
        }

        return (T*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_array));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(nuint count) =>
        _array = GC.AllocateUninitializedArray<T>(checked((int)Math.Max(count, (nuint)_array.Length * 2)), pinned: true);
}

/// <summary>brotli's <c>BlockSplit</c>: the blocks of a category, their types and lengths.</summary>
internal sealed unsafe class BlockSplit
{
    private readonly PinnedBuffer<byte> _types = new();
    private readonly PinnedBuffer<uint> _lengths = new();

    public nuint NumTypes;
    public nuint NumBlocks;
    public byte* Types;
    public uint* Lengths;

    /// <summary>brotli's <c>BrotliInitBlockSplit</c>.</summary>
    public void Init()
    {
        NumTypes = 0;
        NumBlocks = 0;
    }

    /// <summary>brotli's <c>BROTLI_ENSURE_CAPACITY</c> of the types and the lengths.</summary>
    public void EnsureCapacity(nuint count)
    {
        Types = _types.Ensure(count);
        Lengths = _lengths.Ensure(count);
    }
}

/// <summary>brotli's <c>MetaBlockSplit</c>: the blocks, context maps and histograms of a meta-block.</summary>
internal sealed unsafe class MetaBlockSplit
{
    private readonly PinnedBuffer<uint> _literalContextMap = new();
    private readonly PinnedBuffer<uint> _distanceContextMap = new();
    private readonly PinnedBuffer<HistogramLiteral> _literalHistograms = new();
    private readonly PinnedBuffer<HistogramCommand> _commandHistograms = new();
    private readonly PinnedBuffer<HistogramDistance> _distanceHistograms = new();

    public readonly BlockSplit LiteralSplit = new();
    public readonly BlockSplit CommandSplit = new();
    public readonly BlockSplit DistanceSplit = new();
    public uint* LiteralContextMap;
    public nuint LiteralContextMapSize;
    public uint* DistanceContextMap;
    public nuint DistanceContextMapSize;
    public HistogramLiteral* LiteralHistograms;
    public nuint LiteralHistogramsSize;
    public HistogramCommand* CommandHistograms;
    public nuint CommandHistogramsSize;
    public HistogramDistance* DistanceHistograms;
    public nuint DistanceHistogramsSize;

    /// <summary>brotli's <c>InitMetaBlockSplit</c>.</summary>
    public void Init()
    {
        LiteralSplit.Init();
        CommandSplit.Init();
        DistanceSplit.Init();
        LiteralContextMap = null;
        LiteralContextMapSize = 0;
        DistanceContextMap = null;
        DistanceContextMapSize = 0;
        LiteralHistograms = null;
        LiteralHistogramsSize = 0;
        CommandHistograms = null;
        CommandHistogramsSize = 0;
        DistanceHistograms = null;
        DistanceHistogramsSize = 0;
    }

    public uint* AllocateLiteralContextMap(nuint size) => LiteralContextMap = _literalContextMap.Ensure(size);

    public uint* AllocateDistanceContextMap(nuint size) => DistanceContextMap = _distanceContextMap.Ensure(size);

    public HistogramLiteral* AllocateLiteralHistograms(nuint size) => LiteralHistograms = _literalHistograms.Ensure(size);

    public HistogramCommand* AllocateCommandHistograms(nuint size) => CommandHistograms = _commandHistograms.Ensure(size);

    public HistogramDistance* AllocateDistanceHistograms(nuint size) => DistanceHistograms = _distanceHistograms.Ensure(size);
}

/// <summary>
/// brotli's <c>BlockSplitter</c> (<c>c/enc/metablock_inc.h</c>): the greedy block splitter of a
/// category, which merges a block of symbols with the last block or the one before it unless their
/// entropies say to start a new type.
/// </summary>
internal unsafe struct BlockSplitter<THistogram>
    where THistogram : unmanaged, IHistogram
{
    private nuint _alphabetSize;
    private nuint _minBlockSize;
    private double _splitThreshold;
    private nuint _numBlocks;
    private BlockSplit _split;
    private THistogram* _histograms;
    private nuint* _histogramsSize;
    private THistogram* _combinedHisto;
    private nuint _targetBlockSize;
    private nuint _blockSize;
    private nuint _currHistogramIx;
    private nuint _lastHistogramIx0;
    private nuint _lastHistogramIx1;
    private double _lastEntropy0;
    private double _lastEntropy1;
    private nuint _mergeLastCount;

    /// <summary>brotli's <c>InitBlockSplitter</c>: <paramref name="combinedHisto"/> holds two histograms.</summary>
    public void Init(nuint alphabetSize, nuint minBlockSize, double splitThreshold, nuint numSymbols, BlockSplit split, THistogram* histograms, nuint* histogramsSize, THistogram* combinedHisto)
    {
        _alphabetSize = alphabetSize;
        _minBlockSize = minBlockSize;
        _splitThreshold = splitThreshold;
        _numBlocks = 0;
        _split = split;
        _histogramsSize = histogramsSize;
        _targetBlockSize = minBlockSize;
        _blockSize = 0;
        _currHistogramIx = 0;
        _mergeLastCount = 0;
        _histograms = histograms;
        _combinedHisto = combinedHisto;
        Histogram.Clear(_histograms);
        _lastHistogramIx0 = _lastHistogramIx1 = 0;
    }

    /// <summary>The number of histograms <see cref="Init"/> needs: one more than the types, at most.</summary>
    public static nuint MaxNumTypes(nuint numSymbols, nuint minBlockSize, out nuint maxNumBlocks)
    {
        maxNumBlocks = (numSymbols / minBlockSize) + 1;
        return Math.Min(maxNumBlocks, Prefix.MaxBlockTypes + 1);
    }

    /// <summary>brotli's <c>BlockSplitterFinishBlock</c>.</summary>
    public void FinishBlock(bool isFinal)
    {
        BlockSplit split = _split;
        THistogram* histograms = _histograms;
        _blockSize = Math.Max(_blockSize, _minBlockSize);
        if (_numBlocks == 0)
        {
            // The first block.
            split.Lengths[0] = (uint)_blockSize;
            split.Types[0] = 0;
            _lastEntropy0 = FastLog.BitsEntropy(Histogram.Data(histograms), (int)_alphabetSize);
            _lastEntropy1 = _lastEntropy0;
            ++_numBlocks;
            ++split.NumTypes;
            ++_currHistogramIx;
            if (_currHistogramIx < *_histogramsSize)
            {
                Histogram.Clear(histograms + _currHistogramIx);
            }

            _blockSize = 0;
        }
        else if (_blockSize > 0)
        {
            double entropy = FastLog.BitsEntropy(Histogram.Data(histograms + _currHistogramIx), (int)_alphabetSize);
            double* combinedEntropy = stackalloc double[2];
            double* diff = stackalloc double[2];
            for (int j = 0; j < 2; ++j)
            {
                nuint lastHistogramIx = j == 0 ? _lastHistogramIx0 : _lastHistogramIx1;
                _combinedHisto[j] = histograms[_currHistogramIx];
                Histogram.AddHistogram(_combinedHisto + j, histograms + lastHistogramIx);
                combinedEntropy[j] = FastLog.BitsEntropy(Histogram.Data(_combinedHisto + j), (int)_alphabetSize);
                diff[j] = combinedEntropy[j] - entropy - (j == 0 ? _lastEntropy0 : _lastEntropy1);
            }

            if (split.NumTypes < Prefix.MaxBlockTypes && diff[0] > _splitThreshold && diff[1] > _splitThreshold)
            {
                // A new block type.
                split.Lengths[_numBlocks] = (uint)_blockSize;
                split.Types[_numBlocks] = (byte)split.NumTypes;
                _lastHistogramIx1 = _lastHistogramIx0;
                _lastHistogramIx0 = (byte)split.NumTypes;
                _lastEntropy1 = _lastEntropy0;
                _lastEntropy0 = entropy;
                ++_numBlocks;
                ++split.NumTypes;
                ++_currHistogramIx;
                if (_currHistogramIx < *_histogramsSize)
                {
                    Histogram.Clear(histograms + _currHistogramIx);
                }

                _blockSize = 0;
                _mergeLastCount = 0;
                _targetBlockSize = _minBlockSize;
            }
            else if (diff[1] < diff[0] - 20.0)
            {
                // The type of the block before the last.
                split.Lengths[_numBlocks] = (uint)_blockSize;
                split.Types[_numBlocks] = split.Types[_numBlocks - 2];
                (_lastHistogramIx0, _lastHistogramIx1) = (_lastHistogramIx1, _lastHistogramIx0);
                histograms[_lastHistogramIx0] = _combinedHisto[1];
                _lastEntropy1 = _lastEntropy0;
                _lastEntropy0 = combinedEntropy[1];
                ++_numBlocks;
                _blockSize = 0;
                Histogram.Clear(histograms + _currHistogramIx);
                _mergeLastCount = 0;
                _targetBlockSize = _minBlockSize;
            }
            else
            {
                // Merged into the last block.
                split.Lengths[_numBlocks - 1] += (uint)_blockSize;
                histograms[_lastHistogramIx0] = _combinedHisto[0];
                _lastEntropy0 = combinedEntropy[0];
                if (split.NumTypes == 1)
                {
                    _lastEntropy1 = _lastEntropy0;
                }

                _blockSize = 0;
                Histogram.Clear(histograms + _currHistogramIx);
                if (++_mergeLastCount > 1)
                {
                    _targetBlockSize += _minBlockSize;
                }
            }
        }

        if (isFinal)
        {
            *_histogramsSize = split.NumTypes;
            split.NumBlocks = _numBlocks;
        }
    }

    /// <summary>brotli's <c>BlockSplitterAddSymbol</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddSymbol(nuint symbol)
    {
        Histogram.Add(_histograms + _currHistogramIx, symbol);
        ++_blockSize;
        if (_blockSize == _targetBlockSize)
        {
            FinishBlock(isFinal: false);
        }
    }
}

/// <summary>
/// brotli's <c>ContextBlockSplitter</c> (<c>c/enc/metablock.c</c>): the greedy splitter of literals
/// under a static context map, a histogram per context and block type.
/// </summary>
internal unsafe struct ContextBlockSplitter
{
    /// <summary>brotli's <c>BROTLI_MAX_STATIC_CONTEXTS</c>.</summary>
    public const int MaxStaticContexts = 13;

    private nuint _alphabetSize;
    private nuint _numContexts;
    private nuint _maxBlockTypes;
    private nuint _minBlockSize;
    private double _splitThreshold;
    private nuint _numBlocks;
    private BlockSplit _split;
    private HistogramLiteral* _histograms;
    private nuint* _histogramsSize;
    private HistogramLiteral* _combinedHisto;
    private nuint _targetBlockSize;
    private nuint _blockSize;
    private nuint _currHistogramIx;
    private nuint _lastHistogramIx0;
    private nuint _lastHistogramIx1;
    private double* _lastEntropy;
    private nuint _mergeLastCount;

    /// <summary>The number of histograms <see cref="Init"/> needs.</summary>
    public static nuint HistogramsSize(nuint numContexts, nuint numSymbols, nuint minBlockSize, out nuint maxNumBlocks)
    {
        maxNumBlocks = (numSymbols / minBlockSize) + 1;
        nuint maxNumTypes = Math.Min(maxNumBlocks, (Prefix.MaxBlockTypes / numContexts) + 1);
        return maxNumTypes * numContexts;
    }

    /// <summary>
    /// brotli's <c>InitContextBlockSplitter</c>: <paramref name="combinedHisto"/> holds 2 *
    /// <see cref="MaxStaticContexts"/> histograms, <paramref name="lastEntropy"/> as many doubles.
    /// </summary>
    public void Init(nuint alphabetSize, nuint numContexts, nuint minBlockSize, double splitThreshold, BlockSplit split, HistogramLiteral* histograms, nuint* histogramsSize, HistogramLiteral* combinedHisto, double* lastEntropy)
    {
        _alphabetSize = alphabetSize;
        _numContexts = numContexts;
        _maxBlockTypes = Prefix.MaxBlockTypes / numContexts;
        _minBlockSize = minBlockSize;
        _splitThreshold = splitThreshold;
        _numBlocks = 0;
        _split = split;
        _histogramsSize = histogramsSize;
        _targetBlockSize = minBlockSize;
        _blockSize = 0;
        _currHistogramIx = 0;
        _mergeLastCount = 0;
        _histograms = histograms;
        _combinedHisto = combinedHisto;
        _lastEntropy = lastEntropy;
        Histogram.ClearAll(_histograms, numContexts);
        _lastHistogramIx0 = _lastHistogramIx1 = 0;
    }

    /// <summary>brotli's <c>ContextBlockSplitterFinishBlock</c>.</summary>
    public void FinishBlock(bool isFinal)
    {
        BlockSplit split = _split;
        nuint numContexts = _numContexts;
        double* lastEntropy = _lastEntropy;
        HistogramLiteral* histograms = _histograms;
        if (_blockSize < _minBlockSize)
        {
            _blockSize = _minBlockSize;
        }

        if (_numBlocks == 0)
        {
            split.Lengths[0] = (uint)_blockSize;
            split.Types[0] = 0;
            for (nuint i = 0; i < numContexts; ++i)
            {
                lastEntropy[i] = FastLog.BitsEntropy(Histogram.Data(histograms + i), (int)_alphabetSize);
                lastEntropy[numContexts + i] = lastEntropy[i];
            }

            ++_numBlocks;
            ++split.NumTypes;
            _currHistogramIx += numContexts;
            if (_currHistogramIx < *_histogramsSize)
            {
                Histogram.ClearAll(histograms + _currHistogramIx, numContexts);
            }

            _blockSize = 0;
        }
        else if (_blockSize > 0)
        {
            double* entropy = stackalloc double[MaxStaticContexts];
            double* combinedEntropy = stackalloc double[2 * MaxStaticContexts];
            double diff0 = 0.0;
            double diff1 = 0.0;
            HistogramLiteral* combinedHisto = _combinedHisto;
            for (nuint i = 0; i < numContexts; ++i)
            {
                nuint currHistoIx = _currHistogramIx + i;
                entropy[i] = FastLog.BitsEntropy(Histogram.Data(histograms + currHistoIx), (int)_alphabetSize);
                for (nuint j = 0; j < 2; ++j)
                {
                    nuint jx = (j * numContexts) + i;
                    nuint lastHistogramIx = (j == 0 ? _lastHistogramIx0 : _lastHistogramIx1) + i;
                    combinedHisto[jx] = histograms[currHistoIx];
                    Histogram.AddHistogram(combinedHisto + jx, histograms + lastHistogramIx);
                    combinedEntropy[jx] = FastLog.BitsEntropy(Histogram.Data(combinedHisto + jx), (int)_alphabetSize);
                    double d = combinedEntropy[jx] - entropy[i] - lastEntropy[jx];
                    if (j == 0)
                    {
                        diff0 += d;
                    }
                    else
                    {
                        diff1 += d;
                    }
                }
            }

            if (split.NumTypes < _maxBlockTypes && diff0 > _splitThreshold && diff1 > _splitThreshold)
            {
                split.Lengths[_numBlocks] = (uint)_blockSize;
                split.Types[_numBlocks] = (byte)split.NumTypes;
                _lastHistogramIx1 = _lastHistogramIx0;
                _lastHistogramIx0 = split.NumTypes * numContexts;
                for (nuint i = 0; i < numContexts; ++i)
                {
                    lastEntropy[numContexts + i] = lastEntropy[i];
                    lastEntropy[i] = entropy[i];
                }

                ++_numBlocks;
                ++split.NumTypes;
                _currHistogramIx += numContexts;
                if (_currHistogramIx < *_histogramsSize)
                {
                    Histogram.ClearAll(histograms + _currHistogramIx, numContexts);
                }

                _blockSize = 0;
                _mergeLastCount = 0;
                _targetBlockSize = _minBlockSize;
            }
            else if (diff1 < diff0 - 20.0)
            {
                split.Lengths[_numBlocks] = (uint)_blockSize;
                split.Types[_numBlocks] = split.Types[_numBlocks - 2];
                (_lastHistogramIx0, _lastHistogramIx1) = (_lastHistogramIx1, _lastHistogramIx0);
                for (nuint i = 0; i < numContexts; ++i)
                {
                    histograms[_lastHistogramIx0 + i] = combinedHisto[numContexts + i];
                    lastEntropy[numContexts + i] = lastEntropy[i];
                    lastEntropy[i] = combinedEntropy[numContexts + i];
                    Histogram.Clear(histograms + _currHistogramIx + i);
                }

                ++_numBlocks;
                _blockSize = 0;
                _mergeLastCount = 0;
                _targetBlockSize = _minBlockSize;
            }
            else
            {
                split.Lengths[_numBlocks - 1] += (uint)_blockSize;
                for (nuint i = 0; i < numContexts; ++i)
                {
                    histograms[_lastHistogramIx0 + i] = combinedHisto[i];
                    lastEntropy[i] = combinedEntropy[i];
                    if (split.NumTypes == 1)
                    {
                        lastEntropy[numContexts + i] = lastEntropy[i];
                    }

                    Histogram.Clear(histograms + _currHistogramIx + i);
                }

                _blockSize = 0;
                if (++_mergeLastCount > 1)
                {
                    _targetBlockSize += _minBlockSize;
                }
            }
        }

        if (isFinal)
        {
            *_histogramsSize = split.NumTypes * numContexts;
            split.NumBlocks = _numBlocks;
        }
    }

    /// <summary>brotli's <c>ContextBlockSplitterAddSymbol</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddSymbol(nuint symbol, nuint context)
    {
        Histogram.Add(_histograms + _currHistogramIx + context, symbol);
        ++_blockSize;
        if (_blockSize == _targetBlockSize)
        {
            FinishBlock(isFinal: false);
        }
    }
}

/// <summary>brotli's greedy meta-block builder and its histogram optimization (<c>c/enc/metablock.c</c>).</summary>
internal sealed unsafe class GreedyMetaBlockBuilder
{
    private readonly PinnedBuffer<HistogramLiteral> _literalCombined = new();
    private readonly PinnedBuffer<HistogramCommand> _commandCombined = new();
    private readonly PinnedBuffer<HistogramDistance> _distanceCombined = new();
    private readonly PinnedBuffer<double> _lastEntropy = new();
    private readonly PinnedBuffer<byte> _goodForRle = new();
    /// <summary>The numbers of histograms the splitters update: pinned, as the splitters hold pointers to them.</summary>
    private readonly PinnedBuffer<nuint> _histogramsSizes = new();

    /// <summary>brotli's <c>BrotliBuildMetaBlockGreedy</c>.</summary>
    public void Build(byte* ringBuffer, nuint pos, nuint mask, byte prevByte, byte prevByte2, byte* literalContextLut, nuint numContexts, uint* staticContextMap, Command* commands, nuint numCommands, MetaBlockSplit mb)
    {
        nuint numLiterals = 0;
        for (nuint i = 0; i < numCommands; ++i)
        {
            numLiterals += commands[i].InsertLen;
        }

        nuint* sizes = _histogramsSizes.Ensure(3);
        BlockSplitter<HistogramLiteral> plain = default;
        ContextBlockSplitter context = default;
        if (numContexts == 1)
        {
            nuint size = BlockSplitter<HistogramLiteral>.MaxNumTypes(numLiterals, 512, out nuint maxNumBlocks);
            mb.LiteralSplit.EnsureCapacity(maxNumBlocks);
            mb.LiteralSplit.NumBlocks = maxNumBlocks;
            sizes[0] = size;
            plain.Init(256, 512, 400.0, numLiterals, mb.LiteralSplit, mb.AllocateLiteralHistograms(size), sizes, _literalCombined.Ensure(2));
        }
        else
        {
            nuint size = ContextBlockSplitter.HistogramsSize(numContexts, numLiterals, 512, out nuint maxNumBlocks);
            mb.LiteralSplit.EnsureCapacity(maxNumBlocks);
            mb.LiteralSplit.NumBlocks = maxNumBlocks;
            sizes[0] = size;
            context.Init(256, numContexts, 512, 400.0, mb.LiteralSplit, mb.AllocateLiteralHistograms(size), sizes, _literalCombined.Ensure(2 * ContextBlockSplitter.MaxStaticContexts), _lastEntropy.Ensure(2 * ContextBlockSplitter.MaxStaticContexts));
        }

        BlockSplitter<HistogramCommand> commandBlocks = default;
        {
            nuint size = BlockSplitter<HistogramCommand>.MaxNumTypes(numCommands, 1024, out nuint maxNumBlocks);
            mb.CommandSplit.EnsureCapacity(maxNumBlocks);
            mb.CommandSplit.NumBlocks = maxNumBlocks;
            sizes[1] = size;
            commandBlocks.Init(Prefix.NumCommandSymbols, 1024, 500.0, numCommands, mb.CommandSplit, mb.AllocateCommandHistograms(size), sizes + 1, _commandCombined.Ensure(2));
        }

        BlockSplitter<HistogramDistance> distanceBlocks = default;
        {
            nuint size = BlockSplitter<HistogramDistance>.MaxNumTypes(numCommands, 512, out nuint maxNumBlocks);
            mb.DistanceSplit.EnsureCapacity(maxNumBlocks);
            mb.DistanceSplit.NumBlocks = maxNumBlocks;
            sizes[2] = size;
            distanceBlocks.Init(64, 512, 100.0, numCommands, mb.DistanceSplit, mb.AllocateDistanceHistograms(size), sizes + 2, _distanceCombined.Ensure(2));
        }

        for (nuint i = 0; i < numCommands; ++i)
        {
            Command* cmd = commands + i;
            commandBlocks.AddSymbol(cmd->CmdPrefix);
            for (nuint j = cmd->InsertLen; j != 0; --j)
            {
                byte literal = ringBuffer[pos & mask];
                if (numContexts == 1)
                {
                    plain.AddSymbol(literal);
                }
                else
                {
                    nuint ctx = (nuint)(literalContextLut[prevByte] | literalContextLut[256 + prevByte2]);
                    context.AddSymbol(literal, staticContextMap[ctx]);
                }

                prevByte2 = prevByte;
                prevByte = literal;
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
                    distanceBlocks.AddSymbol((nuint)(cmd->DistPrefix & 0x3FF));
                }
            }
        }

        if (numContexts == 1)
        {
            plain.FinishBlock(isFinal: true);
        }
        else
        {
            context.FinishBlock(isFinal: true);
        }

        commandBlocks.FinishBlock(isFinal: true);
        distanceBlocks.FinishBlock(isFinal: true);
        mb.LiteralHistogramsSize = sizes[0];
        mb.CommandHistogramsSize = sizes[1];
        mb.DistanceHistogramsSize = sizes[2];

        if (numContexts > 1)
        {
            // brotli's MapStaticContexts.
            mb.LiteralContextMapSize = mb.LiteralSplit.NumTypes << Prefix.LiteralContextBits;
            uint* map = mb.AllocateLiteralContextMap(mb.LiteralContextMapSize);
            for (nuint i = 0; i < mb.LiteralSplit.NumTypes; ++i)
            {
                uint offset = (uint)(i * numContexts);
                for (nuint j = 0; j < (1u << Prefix.LiteralContextBits); ++j)
                {
                    map[(i << Prefix.LiteralContextBits) + j] = offset + staticContextMap[j];
                }
            }
        }
    }

    /// <summary>brotli's <c>BrotliOptimizeHistograms</c>.</summary>
    public void OptimizeHistograms(uint numDistanceCodes, MetaBlockSplit mb)
    {
        byte* goodForRle = _goodForRle.Ensure(Prefix.NumCommandSymbols);
        for (nuint i = 0; i < mb.LiteralHistogramsSize; ++i)
        {
            EntropyEncode.OptimizeHuffmanCountsForRle(256, Histogram.Data(mb.LiteralHistograms + i), goodForRle);
        }

        for (nuint i = 0; i < mb.CommandHistogramsSize; ++i)
        {
            EntropyEncode.OptimizeHuffmanCountsForRle(Prefix.NumCommandSymbols, Histogram.Data(mb.CommandHistograms + i), goodForRle);
        }

        for (nuint i = 0; i < mb.DistanceHistogramsSize; ++i)
        {
            EntropyEncode.OptimizeHuffmanCountsForRle((int)numDistanceCodes, Histogram.Data(mb.DistanceHistograms + i), goodForRle);
        }
    }
}
