using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace ManagedCompressions.Lz4.Internal;

/// <summary>
/// The decoder of the LZ4 block format: a fast loop while both ends have room for its wide copies,
/// then a careful loop for the last bytes.
/// </summary>
/// <remarks>
/// <para>
/// The fast loop decodes a sequence at a time while the source has 32 bytes left and the output 64.
/// A sequence of fewer than 15 literals and a match of at most 18 bytes, nearly every one, takes
/// one test of the token, one copy of sixteen literals, and two of sixteen bytes for the match, or a
/// vector shuffled to the period when the match is closer than sixteen bytes. A longer run reads its
/// length and copies thirty-two bytes a step; one that would come too close to either end leaves
/// the fast loop where its sequence starts, and the careful loop, which copies exactly, takes it.
/// </para>
/// <para>
/// The fast loop runs twice: over the output's first 64 KiB, where an offset may reach before the
/// start and every one is checked; then past them, where no offset can, and only zero is refused.
/// </para>
/// <para>
/// Every read stays within the source and every write within the destination, whatever the source
/// holds. A block is refused, as -1, when a literal or a match runs past either end, when an offset
/// is zero or reaches before the start, or when the source ends anywhere but after a sequence's literals.
/// </para>
/// </remarks>
internal static unsafe class Lz4Decoder
{
    private const int MinMatch = 4;

    /// <summary>The source the fast loop keeps past a sequence's token: sixteen literals read at once, an offset, and more.</summary>
    private const int InputMargin = 32;

    /// <summary>The output the fast loop keeps past a sequence's start: 14 literals, a match of 18, and 32 written past them.</summary>
    private const int OutputMargin = 64;

    /// <summary>Past this much output, no offset reaches before the start.</summary>
    private const int MaxOffset = 65535;

    /// <summary>Decodes a whole block; the bytes written, or -1 when the block is corrupt or does not fit.</summary>
    public static nint Decode(byte* source, nint sourceLength, byte* destination, nint capacity)
    {
        if (capacity == 0)
        {
            // An empty block is a lone token of no literal.
            return sourceLength == 1 && *source == 0 ? 0 : -1;
        }

        if (sourceLength == 0)
        {
            return -1;
        }

        byte* ip = source;
        byte* iend = source + sourceLength;
        byte* op = destination;
        byte* oend = destination + capacity;

        if (sourceLength > InputMargin && capacity > OutputMargin)
        {
            byte* ipLimit = iend - InputMargin;
            byte* opLimit = oend - OutputMargin;
            byte* farStart = destination + MaxOffset + 1;
            if (!FastLoop<NearStart>(ref ip, ref op, ipLimit, farStart < opLimit ? farStart : opLimit, destination, iend, oend))
            {
                return -1;
            }

            if (op >= farStart && !FastLoop<FarFromStart>(ref ip, ref op, ipLimit, opLimit, destination, iend, oend))
            {
                return -1;
            }
        }

        return Careful(ip, iend, op, oend, destination);
    }

    private interface IOffsetCheck
    {
        /// <summary>Whether an offset may reach before the output's start.</summary>
        static abstract bool NearStart { get; }
    }

    private struct NearStart : IOffsetCheck
    {
        static bool IOffsetCheck.NearStart => true;
    }

    private struct FarFromStart : IOffsetCheck
    {
        static bool IOffsetCheck.NearStart => false;
    }

    /// <summary>
    /// Sequences while the source has more than <see cref="InputMargin"/> bytes and the output reaches
    /// no further than <paramref name="opStop"/>. Leaves <paramref name="ipRef"/> and
    /// <paramref name="opRef"/> at the start of the next sequence; false when the block is corrupt.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FastLoop<TCheck>(ref byte* ipRef, ref byte* opRef, byte* ipLimit, byte* opStop, byte* destination, byte* iend, byte* oend)
        where TCheck : struct, IOffsetCheck
    {
        byte* ip = ipRef;
        byte* op = opRef;
        while (ip <= ipLimit && op <= opStop)
        {
            // The next token's address is the one chain from sequence to sequence: the token's load,
            // a shift, one add to ip + 3, which does not wait on the load. afterToken is used twice,
            // so the JIT keeps it rather than folding the 3 into an add after the shift.
            byte* sequence = ip;
            byte* sequenceOutput = op;
            byte* afterToken = ip + 3;
            nuint token = *ip;
            nuint literals = token >> 4;
            nuint length = token & 15;
            nuint offset;
            byte* match;
            if (literals == 15)
            {
                goto LongLiterals;
            }

            Copy16(op, afterToken - 2);
            ip = afterToken + literals;
            op += literals;

        Offset:
            offset = Unsafe.ReadUnaligned<ushort>(ip - 2);
            match = op - offset;
            if (length == 15)
            {
                goto LongMatch;
            }

            length += MinMatch;
            if (offset >= 8 && (!TCheck.NearStart || match >= destination))
            {
                // Three words, each read after the one before it is written.
                Unsafe.WriteUnaligned(op, Unsafe.ReadUnaligned<ulong>(match));
                Unsafe.WriteUnaligned(op + 8, Unsafe.ReadUnaligned<ulong>(match + 8));
                Unsafe.WriteUnaligned(op + 16, Unsafe.ReadUnaligned<ushort>(match + 16));
                op += length;
                continue;
            }

            if (TCheck.NearStart ? offset - 1 >= (nuint)(op - destination) : offset == 0)
            {
                return false;
            }

            RepeatPattern(op, match, op + length, offset);
            op += length;
            continue;

        LongLiterals:
            ip = sequence + 1;
            if (!ReadLength(ref ip, iend - 15, ref literals))
            {
                return false;
            }

            // Room for the wild copy past the literals, then the offset, at both ends.
            if (literals + InputMargin > (nuint)(iend - ip) || literals + InputMargin > (nuint)(oend - op))
            {
                ip = sequence;
                break;
            }

            WildCopy32(op, ip, op + literals);
            ip += literals + 2;
            op += literals;
            goto Offset;

        LongMatch:
            if (!ReadLength(ref ip, iend - 4, ref length))
            {
                return false;
            }

            length += MinMatch;
            if (length + 32 > (nuint)(oend - op))
            {
                // Up to the end: the careful loop takes the sequence again, literals included.
                ip = sequence;
                op = sequenceOutput;
                break;
            }

            if (TCheck.NearStart ? offset - 1 >= (nuint)(op - destination) : offset == 0)
            {
                return false;
            }

            if (offset >= 16)
            {
                WildCopy32(op, match, op + length);
            }
            else
            {
                RepeatPattern(op, match, op + length, offset);
            }

            op += length;
        }

        ipRef = ip;
        opRef = op;
        return true;
    }

    /// <summary>The careful loop: sequences copied exactly, every length checked against both ends.</summary>
    private static nint Careful(byte* ip, byte* iend, byte* op, byte* oend, byte* destination)
    {
        while (true)
        {
            if (ip >= iend)
            {
                return -1;
            }

            uint token = *ip++;
            nuint length = token >> 4;
            if (length == 15 && !ReadLength(ref ip, iend, ref length))
            {
                return -1;
            }

            if (length > (nuint)(iend - ip) || length > (nuint)(oend - op))
            {
                return -1;
            }

            if (length <= 16 && (nuint)(iend - ip) >= 16 && (nuint)(oend - op) >= 16)
            {
                Copy16(op, ip);
            }
            else
            {
                Buffer.MemoryCopy(ip, op, length, length);
            }

            ip += length;
            op += length;
            if (ip == iend)
            {
                // The last sequence: literals and no match.
                return (nint)(op - destination);
            }

            if (iend - ip < 2)
            {
                return -1;
            }

            nuint offset = Unsafe.ReadUnaligned<ushort>(ip);
            ip += 2;
            length = token & 15;
            if (length == 15 && !ReadLength(ref ip, iend, ref length))
            {
                return -1;
            }

            length += MinMatch;
            if (offset - 1 >= (nuint)(op - destination) || length > (nuint)(oend - op))
            {
                return -1;
            }

            byte* match = op - offset;
            byte* end = op + length;
            if (offset >= 8 && (nuint)(oend - end) >= 8)
            {
                do
                {
                    Unsafe.WriteUnaligned(op, Unsafe.ReadUnaligned<ulong>(match));
                    op += 8;
                    match += 8;
                }
                while (op < end);
            }
            else
            {
                while (op < end)
                {
                    *op++ = *match++;
                }
            }

            op = end;
        }
    }

    /// <summary>
    /// An extended length: bytes of 255 continue it, the first byte below 255 ends it. False when it
    /// reads at or past <paramref name="limit"/>, or when it would overflow.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ReadLength(ref byte* ip, byte* limit, ref nuint length)
    {
        byte* p = ip;
        nuint s;
        do
        {
            if (p >= limit)
            {
                return false;
            }

            s = *p++;
            length += s;
        }
        while (s == 255);

        ip = p;

        // A length past half the address space is no block's.
        return (nint)length >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy16(byte* destination, byte* source) => Vector128.Store(Vector128.Load(source), destination);

    /// <summary>Copies from <paramref name="source"/> until <paramref name="end"/>, thirty-two bytes a step, up to 31 bytes past it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WildCopy32(byte* destination, byte* source, byte* end)
    {
        do
        {
            // Each half read after the one before it is written: a source sixteen bytes behind
            // reads what the step itself wrote.
            Copy16(destination, source);
            Copy16(destination + 16, source + 16);
            destination += 32;
            source += 32;
        }
        while (destination < end);
    }

    /// <summary>
    /// A match closer than sixteen bytes, a pattern of <paramref name="offset"/> bytes repeated until
    /// <paramref name="end"/>, up to sixteen bytes past it: the pattern read once and spread to a
    /// vector, stored as many times as needed, each store advancing a whole number of periods.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RepeatPattern(byte* op, byte* match, byte* end, nuint offset)
    {
        if (AdvSimd.Arm64.IsSupported || Ssse3.IsSupported)
        {
            Vector128<byte> indexes = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(PatternIndexes), offset * 16);
            Vector128<byte> source = Vector128.Load(match);
            Vector128<byte> pattern = AdvSimd.Arm64.IsSupported
                ? AdvSimd.Arm64.VectorTableLookup(source, indexes)
                : Ssse3.Shuffle(source, indexes);
            nuint step = Unsafe.Add(ref MemoryMarshal.GetReference(PatternSteps), offset);
            do
            {
                Vector128.Store(pattern, op);
                op += step;
            }
            while (op < end);

            return;
        }

        while (op < end)
        {
            *op++ = *match++;
        }
    }

    /// <summary>For each offset below 16, the indexes that spread its pattern over sixteen bytes: i % offset.</summary>
    private static ReadOnlySpan<byte> PatternIndexes =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
        0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0,
        0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3,
        0, 1, 2, 3, 4, 0, 1, 2, 3, 4, 0, 1, 2, 3, 4, 0,
        0, 1, 2, 3, 4, 5, 0, 1, 2, 3, 4, 5, 0, 1, 2, 3,
        0, 1, 2, 3, 4, 5, 6, 0, 1, 2, 3, 4, 5, 6, 0, 1,
        0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 0, 1, 2, 3, 4, 5, 6,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 1, 2, 3, 4, 5,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 0, 1, 2, 3, 4,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 1, 2, 3,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0, 1, 2,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 0, 1,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 0,
    ];

    /// <summary>For each offset below 16, the most whole periods that fit in sixteen bytes.</summary>
    private static ReadOnlySpan<byte> PatternSteps => [16, 16, 16, 15, 16, 15, 12, 14, 16, 9, 10, 11, 12, 13, 14, 15];
}
