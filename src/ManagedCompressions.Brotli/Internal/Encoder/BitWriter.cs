using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's bit writer (<c>c/enc/write_bits.h</c>): bits appended least significant first, at a bit
/// position into a byte array, by a 64-bit store at the byte of the position.
/// </summary>
/// <remarks>
/// A write ORs into the byte at the position and stores the seven next bytes, which it overwrites with
/// zeros and the bits that spill into them: the storage must have eight bytes of room past the last bit,
/// and the bytes past the position hold nothing that is kept, exactly as in brotli, whose code reads
/// back <c>storage[pos &gt;&gt; 3]</c> and rewinds and rewrites bits in place.
/// </remarks>
internal static unsafe class BitWriter
{
    /// <summary>Writes the <paramref name="count"/> low bits of <paramref name="bits"/>, at most 56.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(int count, ulong bits, ref int position, byte* storage)
    {
        byte* p = storage + (position >> 3);
        ulong v = *p;
        v |= bits << (position & 7);
        Unsafe.WriteUnaligned(p, v);
        position += count;
    }

    /// <summary>Clears the byte at a byte-aligned position, brotli's <c>BrotliWriteBitsPrepareStorage</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrepareStorage(int position, byte* storage) => storage[position >> 3] = 0;

    /// <summary>Moves to the next byte boundary, and clears the byte there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void JumpToByteBoundary(ref int position, byte* storage)
    {
        position = (position + 7) & ~7;
        storage[position >> 3] = 0;
    }

    /// <summary>
    /// Moves the position back to <paramref name="newPosition"/>, clearing the bits of its byte that lie
    /// past it, brotli's <c>RewindBitPosition</c>.
    /// </summary>
    public static void Rewind(int newPosition, ref int position, byte* storage)
    {
        int bit = newPosition & 7;
        int mask = (1 << bit) - 1;
        storage[newPosition >> 3] &= (byte)mask;
        position = newPosition;
    }
}
