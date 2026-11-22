using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Internal;

/// <summary>
/// Memory the libraries read and write through pointers and references, without bounds checks: their
/// tables, windows and buffers. A pinned array in the builds that ship; in the <c>Checked</c>
/// configuration, which the tests and the fuzzing run, native memory against an inaccessible page
/// (see <c>GuardedMemory</c>), so that the first access past its end faults where an array
/// would let it corrupt the heap silently.
/// </summary>
/// <remarks>
/// A copy shares the memory. The memory lives as long as a copy is reachable: the array, or the
/// guarded block, which frees its pages once collected. A pointer kept past that is a bug either way,
/// and the Checked build faults on it.
/// </remarks>
internal readonly unsafe struct PinnedBuffer<T>
    where T : unmanaged
{
#if CHECKED
    private readonly GuardedMemory? _owner;
#else
    private readonly T[]? _array;
#endif

    /// <summary>The first element; null for an empty buffer that was never allocated.</summary>
    public readonly T* Pointer;

    /// <summary>The number of elements.</summary>
    public readonly int Length;

#if CHECKED
    private PinnedBuffer(GuardedMemory owner, int length)
    {
        _owner = owner;
        Pointer = (T*)owner.Pointer;
        Length = length;
    }
#else
    private PinnedBuffer(T[] array)
    {
        _array = array;
        Pointer = (T*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        Length = array.Length;
    }
#endif

    /// <summary>
    /// A buffer of <paramref name="length"/> elements, cleared, or left with whatever the memory held
    /// when <paramref name="clear"/> is false: the Checked build then fills it with a pattern, so that
    /// a read before a write shows in the output.
    /// </summary>
    public static PinnedBuffer<T> Allocate(int length, bool clear = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
#if CHECKED
        return new PinnedBuffer<T>(GuardedMemory.Allocate((nuint)length * (nuint)sizeof(T), clear), length);
#else
        return new PinnedBuffer<T>(clear ? GC.AllocateArray<T>(length, pinned: true) : GC.AllocateUninitializedArray<T>(length, pinned: true));
#endif
    }

    public bool IsEmpty => Length == 0;

    /// <summary>The elements, as a span: bounds-checked, where <see cref="Pointer"/> is not.</summary>
    public Span<T> Span => new(Pointer, Length);

    /// <summary>The first element, as a reference for <see cref="Unsafe.Add{T}(ref T, int)"/>.</summary>
    public ref T Reference
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref *Pointer;
    }

    /// <summary>An element, bounds-checked as the array's indexer is.</summary>
    public ref T this[int index] => ref Span[index];

    /// <summary>An element, bounds-checked, at an unsigned index, as an array takes one.</summary>
    public ref T this[nuint index] => ref Span[checked((int)index)];

    /// <summary>For <c>fixed</c>: the memory is pinned already, and stays where it is.</summary>
    public ref T GetPinnableReference() => ref *Pointer;

    /// <summary>Every element set to its default.</summary>
    public void Clear() => Span.Clear();

    public Span<T> AsSpan() => Span;

    public Span<T> AsSpan(int start) => Span.Slice(start);

    public Span<T> AsSpan(int start, int length) => Span.Slice(start, length);

    /// <summary>
    /// The elements as memory a caller may keep: the array's, or in the Checked build a manager over
    /// the guarded block, which it keeps alive.
    /// </summary>
    public Memory<T> AsMemory(int start, int length) =>
#if CHECKED
        new GuardedMemory.Manager<T>(_owner!, Pointer, Length).Memory.Slice(start, length);
#else
        _array.AsMemory(start, length);
#endif

    public static implicit operator Span<T>(PinnedBuffer<T> buffer) => buffer.Span;

    public static implicit operator ReadOnlySpan<T>(PinnedBuffer<T> buffer) => buffer.Span;
}
