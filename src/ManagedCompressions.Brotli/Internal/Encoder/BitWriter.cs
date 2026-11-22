using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

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
internal static class BitWriter
{
    /// <summary>Writes the <paramref name="count"/> low bits of <paramref name="bits"/>, at most 56.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(int count, ulong bits, ref int position, Span<byte> storage)
    {
        Span<byte> p = storage.Slice(position >> 3, 8);
        ulong v = p[0];
        v |= bits << (position & 7);
        BinaryPrimitives.WriteUInt64LittleEndian(p, v);
        position += count;
    }

    /// <summary>
    /// The codes of the literals of <paramref name="input"/>, as <see cref="Write"/> writes them one by one, of
    /// at most 15 bits each, through a 64-bit accumulator: the same bytes, and the same zeros past the
    /// position.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int WriteLiterals(ReadOnlySpan<byte> input, ReadOnlySpan<byte> depth, ReadOnlySpan<ushort> bits, int position, Span<byte> storage)
    {
        depth = depth.Slice(0, 256);
        bits = bits.Slice(0, 256);
        int p = position >> 3;
        int used = position & 7;

        // The bits of the position's byte past the position are zeros, as every write leaves them.
        ulong acc = storage[p];
        for (int i = 0; i < input.Length; i++)
        {
            int literal = input[i];
            acc |= (ulong)bits[literal] << used;
            used += depth[literal];
            if (used >= 48)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(storage.Slice(p, 8), acc);
                int full = used >> 3;
                p += full;
                acc >>= full << 3;
                used &= 7;
            }
        }

        BinaryPrimitives.WriteUInt64LittleEndian(storage.Slice(p, 8), acc);
        return (p << 3) + used;
    }

    /// <summary>Writes a literal's code into a <see cref="BitSink"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLiteral(ref BitSink sink, nuint literal, ReadOnlySpan<byte> depth, ReadOnlySpan<ushort> bits) => sink.Write(depth[(int)literal], bits[(int)literal]);

    /// <summary>
    /// Codes combined for the loops that write them: each code's bits, with its depth from bit 16, so that a
    /// symbol is one load. Sixteen at a time.
    /// </summary>
    public static void CombineCodes(ReadOnlySpan<byte> depth, ReadOnlySpan<ushort> bits, Span<uint> codes, int count)
    {
        depth = depth.Slice(0, count);
        bits = bits.Slice(0, count);
        codes = codes.Slice(0, count);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= count; i += 16)
            {
                (Vector128<ushort> depthLow, Vector128<ushort> depthHigh) = Vector128.Widen(Vector128.Create(depth.Slice(i, 16)));
                (Vector128<uint> d0, Vector128<uint> d1) = Vector128.Widen(depthLow);
                (Vector128<uint> d2, Vector128<uint> d3) = Vector128.Widen(depthHigh);
                (Vector128<uint> b0, Vector128<uint> b1) = Vector128.Widen(Vector128.Create(bits.Slice(i, 8)));
                (Vector128<uint> b2, Vector128<uint> b3) = Vector128.Widen(Vector128.Create(bits.Slice(i + 8, 8)));
                (b0 | (d0 << 16)).CopyTo(codes.Slice(i, 4));
                (b1 | (d1 << 16)).CopyTo(codes.Slice(i + 4, 4));
                (b2 | (d2 << 16)).CopyTo(codes.Slice(i + 8, 4));
                (b3 | (d3 << 16)).CopyTo(codes.Slice(i + 12, 4));
            }
        }

        for (; i < count; ++i)
        {
            codes[i] = bits[i] | ((uint)depth[i] << 16);
        }
    }

    /// <summary>Clears the byte at a byte-aligned position, brotli's <c>BrotliWriteBitsPrepareStorage</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrepareStorage(int position, Span<byte> storage) => storage[position >> 3] = 0;

    /// <summary>Moves to the next byte boundary, and clears the byte there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void JumpToByteBoundary(ref int position, Span<byte> storage)
    {
        position = (position + 7) & ~7;
        storage[position >> 3] = 0;
    }

    /// <summary>
    /// Moves the position back to <paramref name="newPosition"/>, clearing the bits of its byte that lie
    /// past it, brotli's <c>RewindBitPosition</c>.
    /// </summary>
    public static void Rewind(int newPosition, ref int position, Span<byte> storage)
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
/// the accumulator. Nothing else may touch the storage between <see cref="BitSink(int, Span{byte})"/> and
/// <see cref="Finish"/>.
/// </summary>
internal ref struct BitSink
{
    private readonly Span<byte> _storage;
    private ulong _acc;
    private int _used;
    private int _p;

    /// <summary>A sink at the bit <paramref name="position"/> of <paramref name="storage"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BitSink(int position, Span<byte> storage)
    {
        _storage = storage;
        _p = position >> 3;
        _used = position & 7;

        // The bits of the position's byte past the position are zeros, as every write leaves them.
        _acc = storage[_p];
    }

    /// <summary>Writes the <paramref name="count"/> low bits of <paramref name="bits"/>, at most 56 with what the accumulator holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int count, ulong bits)
    {
        ulong acc = _acc | (bits << _used);
        int used = _used + count;
        BinaryPrimitives.WriteUInt64LittleEndian(_storage.Slice(_p, 8), acc);
        _p += used >> 3;
        _acc = acc >> (used & ~7);
        _used = used & 7;
    }

    /// <summary>Stores the accumulator, and returns the bit position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int Finish()
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_storage.Slice(_p, 8), _acc);
        return (_p << 3) + _used;
    }
}
