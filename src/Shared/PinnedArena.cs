using System;
#if CHECKED
using System.Collections.Generic;
#endif

namespace ManagedCompressions.Internal;

/// <summary>
/// Small tables carved one after the other from a single <see cref="PinnedBuffer{T}"/>, each right
/// after the previous one. The Checked build gives each table a guarded block of its own instead, of
/// its exact size: carved from one block, a table that overran would run into the next one unnoticed.
/// </summary>
internal sealed unsafe class PinnedArena
{
#if CHECKED
    private readonly List<PinnedBuffer<byte>> _tables = [];
    private int _left;

    /// <summary>An arena of <paramref name="size"/> bytes, the sum of its tables'.</summary>
    public PinnedArena(int size) => _left = size;
#else
    private readonly PinnedBuffer<byte> _memory;
    private int _next;

    /// <summary>An arena of <paramref name="size"/> bytes, the sum of its tables'.</summary>
    public PinnedArena(int size) => _memory = PinnedBuffer<byte>.Allocate(size);
#endif

    /// <summary>The next table, of <paramref name="bytes"/> bytes, cleared.</summary>
    /// <exception cref="InvalidOperationException">The arena was sized for fewer tables.</exception>
    public T* Take<T>(int bytes)
        where T : unmanaged
    {
#if CHECKED
        _left -= bytes;
        if (_left < 0)
        {
            throw new InvalidOperationException("The arena is smaller than its tables.");
        }

        PinnedBuffer<byte> table = PinnedBuffer<byte>.Allocate(bytes);
        _tables.Add(table);
        return (T*)table.Pointer;
#else
        int at = _next;
        _next += bytes;
        return _next <= _memory.Length ? (T*)(_memory.Pointer + at) : throw new InvalidOperationException("The arena is smaller than its tables.");
#endif
    }
}
