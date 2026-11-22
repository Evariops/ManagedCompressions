using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Internal;

/// <summary>
/// The bounds of the libraries' accesses to memory without bounds checks, asserted by a Debug build:
/// Microsoft's unsafe code guidance, "DO guard code with Debug.Assert bounds checks if unsafe code is
/// still necessary". The tests and the nightly fuzzing run the Debug build.
/// </summary>
/// <remarks>
/// <para>
/// Each method is <c>[Conditional("DEBUG")]</c>: a Release build drops the call along with the
/// evaluation of its arguments, so that the access it states, written next to it as it always was,
/// compiles to the same code as without it. An argument must therefore have no side effect, and a value
/// computed for the assertions alone belongs in their arguments, never in a local that a Release build
/// would compute for nothing.
/// </para>
/// <para>
/// The proof that an access stays within its bounds is the caller's, and lies where it calls: a table
/// whose indices come from a table built in range, a margin a loop keeps from the end of its buffers,
/// a length checked once for many accesses. A <c>// SAFETY:</c> comment says which, where the code
/// does not show it. A loop that runs against limits of its own (a margin from the end of the output,
/// say) has them asserted against its buffers where it sets them, and its accesses against them.
/// </para>
/// <para>
/// A region is its first byte and the byte past its last, as pointers or as references; a table is
/// its length. An access through a reference past its region is a bug even when nothing reads it, as is
/// a reference that is never dereferenced (Microsoft's guidance on invalid managed pointers): the
/// regions asserted here are those of the accesses, not of the addresses computed on the way.
/// </para>
/// </remarks>
internal static unsafe class Bounds
{
    /// <summary>Asserts that <paramref name="index"/> is an element of a table of <paramref name="length"/> elements.</summary>
    [Conditional("DEBUG")]
    public static void Index(nint index, nint length) =>
        Debug.Assert((nuint)index < (nuint)length, "an index within its table");

    /// <summary>
    /// Asserts that the <paramref name="count"/> elements from <paramref name="index"/> lie in a table of
    /// <paramref name="length"/> elements: several elements read or written at once.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Range(nint index, nint count, nint length) =>
        Debug.Assert(index >= 0 && count >= 0 && count <= length - index, "a range within its table");

    /// <summary>
    /// Asserts that the <paramref name="size"/> bytes from <paramref name="address"/> lie within the
    /// region from <paramref name="start"/> to <paramref name="end"/>: the bytes an access or a copy
    /// takes, wide copies' excess included.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Within(byte* address, nint size, byte* start, byte* end) =>
        Debug.Assert(size >= 0 && address >= start && address <= end && size <= end - address, "an access within its region");

    /// <inheritdoc cref="Within(byte*, nint, byte*, byte*)"/>
    [Conditional("DEBUG")]
    public static void Within(ref byte address, nint size, ref byte start, ref byte end) =>
        Debug.Assert(
            size >= 0 && !Unsafe.IsAddressLessThan(ref address, ref start) && !Unsafe.IsAddressGreaterThan(ref address, ref end)
                && size <= Unsafe.ByteOffset(ref address, ref end),
            "an access within its region");

    /// <summary>
    /// Asserts that the <paramref name="count"/> elements from <paramref name="address"/> lie within the
    /// region of elements from <paramref name="start"/> to <paramref name="end"/>.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Within<T>(ref T address, nint count, ref T start, ref T end) =>
        Debug.Assert(
            count >= 0 && !Unsafe.IsAddressLessThan(ref address, ref start) && !Unsafe.IsAddressGreaterThan(ref address, ref end)
                && count * Unsafe.SizeOf<T>() <= Unsafe.ByteOffset(ref address, ref end),
            "an access within its region");

    /// <summary>
    /// Asserts that the <paramref name="size"/> bytes from <paramref name="address"/> end at or before
    /// <paramref name="end"/>: the accesses of a pointer that only moves forward from within its region.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Before(byte* address, nint size, byte* end) =>
        Debug.Assert(size >= 0 && address <= end && size <= end - address, "an access before the end of its region");

    /// <inheritdoc cref="Before(byte*, nint, byte*)"/>
    [Conditional("DEBUG")]
    public static void Before(ref byte address, nint size, ref byte end) =>
        Debug.Assert(size >= 0 && !Unsafe.IsAddressGreaterThan(ref address, ref end) && size <= Unsafe.ByteOffset(ref address, ref end), "an access before the end of its region");

    /// <summary>
    /// Asserts that <paramref name="address"/> is at or after <paramref name="start"/>: the accesses of a
    /// reference that only moves back from within its region.
    /// </summary>
    [Conditional("DEBUG")]
    public static void After(ref byte address, ref byte start) =>
        Debug.Assert(!Unsafe.IsAddressLessThan(ref address, ref start), "an access after the start of its region");
}
