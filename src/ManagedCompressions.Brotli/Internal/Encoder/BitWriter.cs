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

    /// <summary>
    /// The codes of <paramref name="length"/> literals, as <see cref="Write"/> writes them one by one, of
    /// at most 15 bits each, through a 64-bit accumulator: the same bytes, and the same zeros past the
    /// position.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int WriteLiterals(byte* input, nuint length, byte* depth, ushort* bits, int position, byte* storage)
    {
        int pos = position;
        byte* p = storage + (pos >> 3);
        int used = pos & 7;

        // The bits of the position's byte past the position are zeros, as every write leaves them.
        ulong acc = *p;
        byte* end = input + length;
        for (; input < end; input++)
        {
            nuint literal = *input;
            acc |= (ulong)bits[literal] << used;
            used += depth[literal];
            if (used >= 48)
            {
                Unsafe.WriteUnaligned(p, acc);
                int full = used >> 3;
                p += full;
                acc >>= full << 3;
                used &= 7;
            }
        }

        Unsafe.WriteUnaligned(p, acc);
        return (int)((p - storage) << 3) + used;
    }

    /// <summary>Writes a literal's code into a <see cref="BitSink"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLiteral(ref BitSink sink, nuint literal, byte* depth, ushort* bits) => sink.Write(depth[literal], bits[literal]);

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

/// <summary>
/// The bits <see cref="BitWriter.Write"/> writes, kept in a 64-bit accumulator that is stored 8 bytes at a
/// time, for the hot loops: the same bytes, and zeros past the position, once <see cref="Finish"/> stores
/// the accumulator. Nothing else may touch the storage between <see cref="BitSink(int, byte*)"/> and
/// <see cref="Finish"/>.
/// </summary>
internal unsafe struct BitSink
{
    private ulong _acc;
    private int _used;
    private byte* _p;
    private readonly byte* _storage;

    /// <summary>A sink at the bit <paramref name="position"/> of <paramref name="storage"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BitSink(int position, byte* storage)
    {
        _storage = storage;
        _p = storage + (position >> 3);
        _used = position & 7;

        // The bits of the position's byte past the position are zeros, as every write leaves them.
        _acc = *_p;
    }

    /// <summary>
    /// Writes the <paramref name="count"/> low bits of <paramref name="bits"/>, at most 56: the
    /// accumulator stored, and the whole bytes left behind, without a branch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int count, ulong bits)
    {
        ulong acc = _acc | (bits << _used);
        int used = _used + count;
        Unsafe.WriteUnaligned(_p, acc);
        _p += used >> 3;
        _acc = acc >> (used & ~7);
        _used = used & 7;
    }

    /// <summary>Stores the accumulator: the bit position past the bits written.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int Finish()
    {
        Unsafe.WriteUnaligned(_p, _acc);
        return (int)((_p - _storage) << 3) + _used;
    }
}
