using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>libzstd's overflow correction, before each block (<c>ZSTD_overflowCorrectIfNeeded</c>).</summary>
public sealed unsafe partial class ZstdCompressor
{
    /// <summary>libzstd's <c>window.nbOverflowCorrections</c>: the corrections since the indices last restarted.</summary>
    private uint _overflowCorrections;

    /// <summary>
    /// Corrects the indices as soon as they may be, as libzstd built with
    /// <c>ZSTD_WINDOW_OVERFLOW_CORRECT_FREQUENTLY</c> does, rather than near 3500 MiB: for the tests,
    /// which check that the frames stay the same bytes.
    /// </summary>
    internal bool CorrectOverflowFrequently { get; set; }

    /// <summary>The corrections made since the indices last restarted, for the tests.</summary>
    internal uint OverflowCorrections => _overflowCorrections;

    /// <summary>
    /// libzstd's <c>ZSTD_overflowCorrectIfNeeded</c> before the block from <paramref name="blockStart"/>
    /// to <paramref name="blockEnd"/>: when it would take the indices past <c>ZSTD_CURRENT_MAX</c>,
    /// every index moves down (<c>ZSTD_window_correctOverflow</c>), in the window and in the tables
    /// (<c>ZSTD_reduceIndex</c>), and the dictionary is dropped.
    /// </summary>
    private void CorrectOverflowIfNeeded(byte* blockStart, byte* blockEnd)
    {
        ref MatchState window = ref _matchState;
        CompressionParameters parameters = window.Parameters;

        // libzstd's ZSTD_cycleLog: the chains' size, half of it for the binary trees.
        int cycleLog = parameters.ChainLog - (parameters.Strategy >= Strategy.BinaryTreeLazy2 ? 1 : 0);
        uint maxDistance = 1u << parameters.WindowLog;
        uint current = (uint)(blockStart - window.Base);
        if (!OverflowCorrection.Needed(
            current, (uint)(blockEnd - window.Base), cycleLog, maxDistance, window.LoadedDictEnd, _overflowCorrections, CorrectOverflowFrequently))
        {
            return;
        }

        uint correction = OverflowCorrection.Correction(current, cycleLog, maxDistance);
        window.Base += correction;
        window.DictionaryBase += correction;
        window.LowLimit = OverflowCorrection.Limit(window.LowLimit, correction);
        window.DictLimit = OverflowCorrection.Limit(window.DictLimit, correction);
        window.End = window.End < correction ? 0 : window.End - correction;
        _overflowCorrections++;

        OverflowCorrection.Reduce(window.HashTable, 1 << parameters.HashLog, correction, preserveMark: false);
        if (parameters.Strategy != Strategy.Fast && !parameters.UsesRowMatchFinder)
        {
            OverflowCorrection.Reduce(window.ChainTable, 1 << parameters.ChainLog, correction, parameters.Strategy == Strategy.BinaryTreeLazy2);
        }

        if (window.HashLog3 != 0)
        {
            OverflowCorrection.Reduce(window.HashTable3, 1 << window.HashLog3, correction, preserveMark: false);
        }

        window.NextToUpdate = window.NextToUpdate < correction ? 0 : window.NextToUpdate - correction;
        window.LoadedDictEnd = 0;
        window.Dictionary = null;
    }
}
