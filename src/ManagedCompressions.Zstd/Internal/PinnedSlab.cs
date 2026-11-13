using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Zstd.Internal;

/// <summary>
/// One pinned array carved into small tables, as libzstd's <c>ZSTD_cwksp</c> carves its workspace: a
/// single allocation on the pinned object heap, whose allocations all take the allocator's slow path,
/// in place of one per table, some twenty for a compressor. Each table starts a cache line of its own,
/// cleared. Whatever holds a table keeps a reference to its slab, which keeps the array alive.
/// </summary>
internal sealed unsafe class PinnedSlab
{
    private const int LineSize = 64;

    private readonly byte[] _memory;
    private readonly byte* _end;
    private byte* _next;

    /// <summary>A slab of <paramref name="size"/> bytes, the sum of the <see cref="SizeOf{T}"/> of its tables.</summary>
    public PinnedSlab(int size)
    {
        _memory = GC.AllocateArray<byte>(size + LineSize, pinned: true);
        byte* start = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_memory));
        _next = (byte*)(((nuint)start + LineSize - 1) & ~(nuint)(LineSize - 1));
        _end = start + _memory.Length;
    }

    /// <summary>The room a table of <paramref name="count"/> values takes: whole cache lines.</summary>
    public static int SizeOf<T>(int count)
        where T : unmanaged => ((count * sizeof(T)) + LineSize - 1) & ~(LineSize - 1);

    /// <summary>The next table, of <paramref name="count"/> values.</summary>
    /// <exception cref="InvalidOperationException">The slab was sized for fewer tables.</exception>
    public T* Take<T>(int count)
        where T : unmanaged
    {
        T* table = (T*)_next;
        _next += SizeOf<T>(count);
        return _next <= _end ? table : throw new InvalidOperationException("The slab is smaller than its tables.");
    }
}
