using System;
using System.Collections.Generic;
using ManagedCompressions.Internal;

namespace ManagedCompressions.Zstd.Internal;

/// <summary>
/// One pinned buffer carved into small tables, as libzstd's <c>ZSTD_cwksp</c> carves its workspace: a
/// single allocation on the pinned object heap, whose allocations all take the allocator's slow path,
/// in place of one per table, some twenty for a compressor. Each table starts a cache line of its own,
/// cleared. Whatever holds a table keeps a reference to its slab, which keeps the memory alive.
/// </summary>
/// <remarks>
/// The Checked build gives each table a guarded block of its own instead, of its exact size: carved
/// from one block, a table that overran would run into the next one unnoticed.
/// </remarks>
internal sealed unsafe class PinnedSlab
{
    private const int LineSize = 64;

#if CHECKED
    private readonly List<PinnedBuffer<byte>> _tables = [];
    private int _left;

    /// <summary>A slab of <paramref name="size"/> bytes, the sum of the <see cref="SizeOf{T}"/> of its tables.</summary>
    public PinnedSlab(int size) => _left = size;
#else
    private readonly PinnedBuffer<byte> _memory;
    private readonly byte* _end;
    private byte* _next;

    /// <summary>A slab of <paramref name="size"/> bytes, the sum of the <see cref="SizeOf{T}"/> of its tables.</summary>
    public PinnedSlab(int size)
    {
        _memory = PinnedBuffer<byte>.Allocate(size + LineSize);
        byte* start = _memory.Pointer;
        _next = (byte*)(((nuint)start + LineSize - 1) & ~(nuint)(LineSize - 1));
        _end = start + _memory.Length;
    }
#endif

    /// <summary>The room a table of <paramref name="count"/> values takes: whole cache lines.</summary>
    public static int SizeOf<T>(int count)
        where T : unmanaged => ((count * sizeof(T)) + LineSize - 1) & ~(LineSize - 1);

    /// <summary>The next table, of <paramref name="count"/> values.</summary>
    /// <exception cref="InvalidOperationException">The slab was sized for fewer tables.</exception>
    public T* Take<T>(int count)
        where T : unmanaged
    {
#if CHECKED
        _left -= SizeOf<T>(count);
        if (_left < 0)
        {
            throw new InvalidOperationException("The slab is smaller than its tables.");
        }

        PinnedBuffer<byte> table = PinnedBuffer<byte>.Allocate(count * sizeof(T));
        _tables.Add(table);
        return (T*)table.Pointer;
#else
        T* table = (T*)_next;
        _next += SizeOf<T>(count);
        return _next <= _end ? table : throw new InvalidOperationException("The slab is smaller than its tables.");
#endif
    }
}
