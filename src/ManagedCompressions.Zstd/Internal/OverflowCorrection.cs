using System;

namespace ManagedCompressions.Zstd.Internal;

/// <summary>
/// libzstd's overflow correction of a window's indices (<c>zstd_compress_internal.h</c>): before an
/// index passes <see cref="CurrentMax"/>, every index moves down by a correction that keeps the window
/// whole and the low bits the chains and trees cycle on, which changes no match, and so no byte.
/// </summary>
internal static unsafe class OverflowCorrection
{
    /// <summary>libzstd's <c>ZSTD_CURRENT_MAX</c> on 64-bit processors.</summary>
    public const uint CurrentMax = 3500u << 20;

    /// <summary>libzstd's <c>ZSTD_WINDOW_START_INDEX</c>: the lowest index; those below are special.</summary>
    public const uint WindowStartIndex = 2;

    /// <summary>libzstd's <c>ZSTD_DUBT_UNSORTED_MARK</c>: btlazy2's candidate not sorted yet, which a correction keeps.</summary>
    public const uint UnsortedMark = 1;

    /// <summary>
    /// libzstd's <c>ZSTD_window_needOverflowCorrection</c>: whether the block from <paramref name="current"/>
    /// to <paramref name="end"/> takes the indices past <see cref="CurrentMax"/>; or, with
    /// <paramref name="frequently"/> (<c>ZSTD_WINDOW_OVERFLOW_CORRECT_FREQUENTLY</c>, for the tests),
    /// whether they may be corrected already (<c>ZSTD_window_canOverflowCorrect</c>): far enough past
    /// the last correction, and past the window of a dictionary, whose loss would change the frame.
    /// </summary>
    public static bool Needed(uint current, uint end, int cycleLog, uint maxDistance, uint loadedDictEnd, uint corrections, bool frequently)
    {
        if (frequently)
        {
            uint cycleSize = 1u << cycleLog;
            uint minimum = cycleSize + Math.Max(maxDistance, cycleSize) + WindowStartIndex;
            uint adjusted = Math.Max(unchecked(minimum * (corrections + 1)), minimum);
            if (current > adjusted && current > unchecked(maxDistance + loadedDictEnd))
            {
                return true;
            }
        }

        return end > CurrentMax;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_window_correctOverflow</c>, its correction: what brings <paramref name="current"/>
    /// down to the window's size and a cycle, the low <paramref name="cycleLog"/> bits kept.
    /// </summary>
    public static uint Correction(uint current, int cycleLog, uint maxDistance)
    {
        uint cycleSize = 1u << cycleLog;
        uint currentCycle = current & (cycleSize - 1);
        uint currentCycleCorrection = currentCycle < WindowStartIndex ? Math.Max(cycleSize, WindowStartIndex) : 0;
        uint newCurrent = currentCycle + currentCycleCorrection + Math.Max(maxDistance, cycleSize);
        return current - newCurrent;
    }

    /// <summary>
    /// The rest of <c>ZSTD_window_correctOverflow</c>: a limit of the window moved down by
    /// <paramref name="correction"/>, no lower than <see cref="WindowStartIndex"/>.
    /// </summary>
    public static uint Limit(uint limit, uint correction) =>
        limit < correction + WindowStartIndex ? WindowStartIndex : limit - correction;

    /// <summary>
    /// libzstd's <c>ZSTD_reduceTable</c>, or with <paramref name="preserveMark"/> <c>ZSTD_reduceTable_btlazy2</c>:
    /// each index moved down by <paramref name="correction"/>, those it would take below the window's
    /// start squashed to 0, btlazy2's mark kept.
    /// </summary>
    public static void Reduce(uint* table, int size, uint correction, bool preserveMark)
    {
        uint threshold = correction + WindowStartIndex;
        for (int i = 0; i < size; i++)
        {
            uint value = table[i];
            table[i] = preserveMark && value == UnsortedMark ? UnsortedMark : value < threshold ? 0 : value - correction;
        }
    }
}
