using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// The library's only unsafe code: a cache prefetch hint, which .NET takes as an address only.
/// </summary>
/// <remarks>
/// <para>
/// A prefetch reads nothing the program sees and writes nothing: it asks the processor to bring a cache line
/// closer, and an address it cannot fetch is ignored rather than faulting. The address here is that of an
/// element reached through a bounds-checked index, so it lies within the array; should the garbage
/// collector move the array before the hint runs, the hint names memory the array has left and is merely
/// wasted. Only the timing can tell the difference.
/// </para>
/// <para>
/// The build keeps unsafe code to this file: the project's CheckUnsafeKernel target fails on any other file
/// that uses the word, and BannedSymbols.txt bans <c>System.Runtime.CompilerServices.Unsafe</c> everywhere but here.
/// </para>
/// </remarks>
internal static unsafe class Prefetch
{
    /// <summary>Whether <see cref="Element{T}"/> does anything: on x86 and x64, where .NET has a prefetch.</summary>
    public static bool IsSupported => Sse.IsSupported;

    /// <summary>Hints that the cache line of <c>array[index]</c> will be read soon.</summary>
    /// <exception cref="System.IndexOutOfRangeException"><paramref name="index"/> is past the array.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Element<T>(T[] array, nuint index)
        where T : unmanaged
    {
        if (Sse.IsSupported)
        {
#pragma warning disable RS0030 // The address of a checked element, for a hint that reads nothing.
            Sse.Prefetch0(Unsafe.AsPointer(ref array[index]));
#pragma warning restore RS0030
        }
    }
}
