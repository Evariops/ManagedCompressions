using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Zstd.Internal;

/// <summary>The match finders' tables: pinned arrays, their first entry on a cache line's boundary.</summary>
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
    /// the array grown (and so cleared) when too small, else cleared when <paramref name="clear"/>.
    /// </summary>
    /// <returns>The table's first entry, or null when no table was ever needed.</returns>
    public static T* Reserve<T>(ref T[] table, int size, bool clear)
        where T : unmanaged
    {
        int padded = size == 0 ? 0 : size + (Alignment / sizeof(T));
        if (table.Length < padded)
        {
            table = GC.AllocateArray<T>(padded, pinned: true);
        }
        else if (clear)
        {
            Array.Clear(table);
        }

        if (table.Length == 0)
        {
            return null;
        }

        nuint start = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(table));
        return (T*)((start + (Alignment - 1)) & ~(nuint)(Alignment - 1));
    }

    /// <summary>
    /// Asserts that <paramref name="table"/>, which <see cref="Reserve"/> took in <paramref name="array"/>, holds
    /// <paramref name="count"/> entries: those the match finders index it with, by hashes of the parameters'
    /// widths and masks of their logs, without a check.
    /// </summary>
    [Conditional("DEBUG")]
    public static void AssertHolds<T>(T[] array, T* table, int count)
        where T : unmanaged
    {
        T* start = (T*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        Debug.Assert(count == 0 || (table >= start && table + count <= start + array.Length), "a match finder's table of its parameters' size");
    }
}
