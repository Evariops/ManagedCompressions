using ManagedCompressions.Internal;

namespace ManagedCompressions.Zstd.Internal;

/// <summary>The match finders' tables: pinned buffers, their first entry on a cache line's boundary.</summary>
internal static unsafe class Tables
{
    /// <summary>
    /// The alignment of the match finders' tables: a cache line of Apple's cores. A row of the row
    /// match finder (16 to 64 bytes of tags, 64 to 256 of indices) then never straddles two lines;
    /// libzstd's workspace aligns its tables to 64 bytes. A pinned array's data is only 8-byte aligned.
    /// </summary>
    public const int Alignment = 128;

    /// <summary>
    /// A table of at least <paramref name="size"/> entries from a <see cref="Alignment"/> boundary:
    /// the buffer grown (and so cleared) when too small, else cleared when <paramref name="clear"/>.
    /// The Checked build sizes it exactly and leaves it unaligned, its end against the guard page:
    /// the alignment is for speed alone.
    /// </summary>
    /// <returns>The table's first entry, or null when no table was ever needed.</returns>
    public static T* Reserve<T>(ref PinnedBuffer<T> table, int size, bool clear)
        where T : unmanaged
    {
#if CHECKED
        int padded = size;
#else
        int padded = size == 0 ? 0 : size + (Alignment / sizeof(T));
#endif
        if (table.Length < padded)
        {
            table = PinnedBuffer<T>.Allocate(padded);
        }
        else if (clear)
        {
            table.Clear();
        }

        if (table.Length == 0)
        {
            return null;
        }

#if CHECKED
        return table.Pointer;
#else
        nuint start = (nuint)table.Pointer;
        return (T*)((start + (Alignment - 1)) & ~(nuint)(Alignment - 1));
#endif
    }
}
