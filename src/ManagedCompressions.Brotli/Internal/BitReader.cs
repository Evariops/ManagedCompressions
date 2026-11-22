using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// Brotli's bit stream, least significant bit first, through a 64-bit accumulator.
/// </summary>
/// <remarks>
/// <para>
/// The reader holds positions in the input, which each call passes again: <see cref="Next"/> is the
/// first byte not yet in the accumulator, whose bits start at position <see cref="Count"/>. Bits above
/// <see cref="Count"/> may hold the next bytes already, which a refill ORs in again unchanged. While
/// eight bytes remain before <see cref="End"/>, a refill is one unaligned load
/// (<see cref="RefillFast"/>); past that, <see cref="RefillCareful"/> loads byte by byte, and past
/// <see cref="End"/> it loads zeros and lets <see cref="Next"/> run on.
/// </para>
/// <para>
/// Those zeros stand for input that has not arrived. Brotli's decoder stops as soon as it needs a
/// bit it does not have, and reports that it needs more input; this reader decodes on, and
/// <see cref="Overrun"/> tells afterwards whether any of the zeros were consumed. Whatever was decoded
/// before that point comes from real bits, and is what brotli decodes; whatever came after is
/// discarded, and the verdict is brotli's: more input needed.
/// </para>
/// </remarks>
internal struct BitReader
{
    public ulong Bits;
    public int Count;

    /// <summary>The next byte to load, from the start of the input.</summary>
    public int Next;

    /// <summary>The end of the input.</summary>
    public int End;

    /// <summary>Where the reader started.</summary>
    public int Start;

    /// <summary>A reader at byte <paramref name="start"/> of an input of <paramref name="end"/> bytes.</summary>
    public BitReader(int start, int end)
    {
        Bits = 0;
        Count = 0;
        Next = start;
        Start = start;
        End = end;
    }

    /// <summary>Whether some of the zeros past <see cref="End"/> have been consumed.</summary>
    public readonly bool Overrun
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Next > End && (long)(Next - End) * 8 > Count;
    }

    /// <summary>The bits consumed since <see cref="Start"/>.</summary>
    public readonly long Position => ((long)(Next - Start) * 8) - Count;

    /// <summary>The bits consumed since the start of the input.</summary>
    public readonly long AbsolutePosition => ((long)Next * 8) - Count;

    /// <summary>Tops the accumulator up to at least 56 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Refill(ReadOnlySpan<byte> input)
    {
        if (End - Next >= 8)
        {
            RefillFast(input);
        }
        else
        {
            RefillCareful(input);
        }
    }

    /// <summary>Tops the accumulator up to at least 56 bits; eight bytes must remain at <see cref="Next"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RefillFast(ReadOnlySpan<byte> input)
    {
        Bits |= BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(Next, 8)) << Count;
        Next += (63 - Count) >> 3;
        Count |= 56;
    }

    /// <summary>Tops the accumulator up to at least 56 bits, byte by byte, with zeros past <see cref="End"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void RefillCareful(ReadOnlySpan<byte> input)
    {
        while (Count <= 56)
        {
            if (Next < End)
            {
                Bits |= (ulong)input[Next] << Count;
            }

            Next++;
            Count += 8;
        }
    }

    /// <summary>Reads <paramref name="n"/> bits, 0 to 56.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read(ReadOnlySpan<byte> input, int n)
    {
        if (Count < n)
        {
            Refill(input);
        }

        uint value = (uint)(Bits & ((1UL << n) - 1));
        Bits >>= n;
        Count -= n;
        return value;
    }

    /// <summary>Drops the bits up to the next byte boundary and returns them, 0 when they are all zero.</summary>
    public uint JumpToByteBoundary()
    {
        int pad = Count & 7;
        uint bits = (uint)(Bits & ((1UL << pad) - 1));
        Bits >>= pad;
        Count -= pad;
        return bits;
    }

    /// <summary>
    /// The position of the next byte, once at a byte boundary: drops the whole bytes in the
    /// accumulator and points <see cref="Next"/> at the first of them, to read bytes directly.
    /// </summary>
    public void Unload()
    {
        Next -= Count >> 3;
        Bits = 0;
        Count = 0;
    }

    /// <summary>
    /// Decodes a symbol of the code whose table starts at <paramref name="tree"/> in
    /// <paramref name="tables"/>; the accumulator must hold at least 15 bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeSymbol(ReadOnlySpan<uint> tables, int tree)
    {
        uint entry = tables[tree + (int)(Bits & 0xFF)];
        int length = (int)(entry & 0xFF);
        if (length > Huffman.RootBits)
        {
            Bits >>= Huffman.RootBits;
            Count -= Huffman.RootBits;
            entry = tables[tree + (int)(entry >> 16) + (int)(Bits & ((1UL << (length - Huffman.RootBits)) - 1))];
            length = (int)(entry & 0xFF);
        }

        Bits >>= length;
        Count -= length;
        return (int)(entry >> 16);
    }

    /// <summary>Decodes a symbol, refilling first when the accumulator holds fewer than 15 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSymbol(ReadOnlySpan<byte> input, ReadOnlySpan<uint> tables, int tree)
    {
        if (Count < Huffman.MaxCodeLength)
        {
            Refill(input);
        }

        return DecodeSymbol(tables, tree);
    }
}
