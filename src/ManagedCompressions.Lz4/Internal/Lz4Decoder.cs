using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
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
/// Every read and write goes through a span, bounds-checked, whatever the source holds. A block is
/// refused, as -1, when a literal or a match runs past either end, when an offset is zero or reaches
/// before the start, or when the source ends anywhere but after a sequence's literals.
/// </para>
/// </remarks>
internal static class Lz4Decoder
{
    private const int MinMatch = 4;

    /// <summary>The source the fast loop keeps past a sequence's token: sixteen literals read at once, an offset, and more.</summary>
    private const int InputMargin = 32;

    /// <summary>The output the fast loop keeps past a sequence's start: 14 literals, a match of 18, and 32 written past them.</summary>
    private const int OutputMargin = 64;

    /// <summary>Past this much output, no offset reaches before the start.</summary>
    private const int MaxOffset = 65535;

    /// <summary>Decodes a whole block; the bytes written, or -1 when the block is corrupt or does not fit.</summary>
    public static int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            // An empty block is a lone token of no literal.
            return source.Length == 1 && source[0] == 0 ? 0 : -1;
        }

        if (source.IsEmpty)
        {
            return -1;
        }

        int ip = 0;
        int op = 0;
        if (source.Length > InputMargin && destination.Length > OutputMargin)
        {
            int ipLimit = source.Length - InputMargin;
            int opLimit = destination.Length - OutputMargin;
            const int FarStart = MaxOffset + 1;
            if (!FastLoop<NearStart>(source, destination, ref ip, ref op, ipLimit, Math.Min(FarStart, opLimit)))
            {
                return -1;
            }

            if (op >= FarStart && !FastLoop<FarFromStart>(source, destination, ref ip, ref op, ipLimit, opLimit))
            {
                return -1;
            }
        }

        return Careful(source, destination, ip, op);
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
    /// <remarks>
    /// A method of its own, compiled with an inlining budget of its own: inlined into <see cref="Decode"/>, twice,
    /// the loops ran out of it, and called their reads and copies.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool FastLoop<TCheck>(ReadOnlySpan<byte> src, Span<byte> dst, ref int ipRef, ref int opRef, int ipLimit, int opStop)
        where TCheck : struct, IOffsetCheck
    {
        int ip = ipRef;
        int op = opRef;
        while (ip <= ipLimit && op <= opStop)
        {
            // The next token's position is the one chain from sequence to sequence: the token's load,
            // a shift, one add to ip + 3, which does not wait on the load.
            int sequence = ip;
            int sequenceOutput = op;

            // The sequence's 32 bytes of input and 32 of output, both within the margins: the token, up to
            // 14 literals copied 16 at once and the offset are read at short offsets into the one, and the
            // literals and a short match written into the other, with no check of their own.
            ReadOnlySpan<byte> input = src.Slice(ip, InputMargin);
            nuint token = input[0];
            nuint literals = token >> 4;
            nuint length = token & 15;
            nuint offset;
            int match;
            if (literals == 15)
            {
                goto LongLiterals;
            }

            {
                int literalCount = (int)literals;
                Span<byte> output = dst.Slice(op, 32);
                Vector128.Create(input.Slice(1, 16)).CopyTo(output);
                offset = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(1 + literalCount, 2));
                ip += 3 + literalCount;
                op += literalCount;
                match = op - (int)offset;
                if (length != 15 && offset >= 8 && (!TCheck.NearStart || match >= 0))
                {
                    // Three words, each read after the one before it is written.
                    ReadOnlySpan<byte> from = dst.Slice(match, 18);
                    Span<byte> to = output.Slice(literalCount, 18);
                    BinaryPrimitives.WriteUInt64LittleEndian(to, BinaryPrimitives.ReadUInt64LittleEndian(from));
                    BinaryPrimitives.WriteUInt64LittleEndian(to.Slice(8), BinaryPrimitives.ReadUInt64LittleEndian(from.Slice(8)));
                    BinaryPrimitives.WriteUInt16LittleEndian(to.Slice(16), BinaryPrimitives.ReadUInt16LittleEndian(from.Slice(16)));
                    op += (int)length + MinMatch;
                    continue;
                }

                goto Match;
            }

        Offset:
            offset = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(ip - 2, 2));
            match = op - (int)offset;

        Match:
            if (length == 15)
            {
                goto LongMatch;
            }

            length += MinMatch;
            if (offset >= 8 && (!TCheck.NearStart || match >= 0))
            {
                // Three words, each read after the one before it is written.
                Span<byte> words = dst.Slice(op, 18);
                ReadOnlySpan<byte> from = dst.Slice(match, 18);
                BinaryPrimitives.WriteUInt64LittleEndian(words, BinaryPrimitives.ReadUInt64LittleEndian(from));
                BinaryPrimitives.WriteUInt64LittleEndian(words.Slice(8), BinaryPrimitives.ReadUInt64LittleEndian(from.Slice(8)));
                BinaryPrimitives.WriteUInt16LittleEndian(words.Slice(16), BinaryPrimitives.ReadUInt16LittleEndian(from.Slice(16)));
                op += (int)length;
                continue;
            }

            if (TCheck.NearStart ? offset - 1 >= (nuint)op : offset == 0)
            {
                return false;
            }

            RepeatPattern(dst, op, match, op + (int)length, (int)offset);
            op += (int)length;
            continue;

        LongLiterals:
            ip = sequence + 1;
            if (!ReadLength(src, ref ip, src.Length - 15, ref literals))
            {
                return false;
            }

            // Room for the wild copy past the literals, then the offset, at both ends.
            if (literals + InputMargin > (nuint)(src.Length - ip) || literals + InputMargin > (nuint)(dst.Length - op))
            {
                ip = sequence;
                break;
            }

            WildCopy32(dst, op, src, ip, op + (int)literals);
            ip += (int)literals + 2;
            op += (int)literals;
            goto Offset;

        LongMatch:
            if (!ReadLength(src, ref ip, src.Length - 4, ref length))
            {
                return false;
            }

            length += MinMatch;
            if (length + 32 > (nuint)(dst.Length - op))
            {
                // Up to the end: the careful loop takes the sequence again, literals included.
                ip = sequence;
                op = sequenceOutput;
                break;
            }

            if (TCheck.NearStart ? offset - 1 >= (nuint)op : offset == 0)
            {
                return false;
            }

            if (offset >= 16)
            {
                WildCopy32(dst, op, dst, match, op + (int)length);
            }
            else
            {
                RepeatPattern(dst, op, match, op + (int)length, (int)offset);
            }

            op += (int)length;
        }

        ipRef = ip;
        opRef = op;
        return true;
    }

    /// <summary>The careful loop: sequences copied exactly, every length checked against both ends.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Careful(ReadOnlySpan<byte> src, Span<byte> dst, int ip, int op)
    {
        while (true)
        {
            if (ip >= src.Length)
            {
                return -1;
            }

            uint token = src[ip++];
            nuint length = token >> 4;
            if (length == 15 && !ReadLength(src, ref ip, src.Length, ref length))
            {
                return -1;
            }

            if (length > (nuint)(src.Length - ip) || length > (nuint)(dst.Length - op))
            {
                return -1;
            }

            if (length <= 16 && src.Length - ip >= 16 && dst.Length - op >= 16)
            {
                Copy16(dst, op, src, ip);
            }
            else
            {
                src.Slice(ip, (int)length).CopyTo(dst.Slice(op));
            }

            ip += (int)length;
            op += (int)length;
            if (ip == src.Length)
            {
                // The last sequence: literals and no match.
                return op;
            }

            if (src.Length - ip < 2)
            {
                return -1;
            }

            nuint offset = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(ip, 2));
            ip += 2;
            length = token & 15;
            if (length == 15 && !ReadLength(src, ref ip, src.Length, ref length))
            {
                return -1;
            }

            length += MinMatch;
            if (offset - 1 >= (nuint)op || length > (nuint)(dst.Length - op))
            {
                return -1;
            }

            int match = op - (int)offset;
            int end = op + (int)length;
            if (offset >= 8 && dst.Length - end >= 8)
            {
                do
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(op, 8), BinaryPrimitives.ReadUInt64LittleEndian(dst.Slice(match, 8)));
                    op += 8;
                    match += 8;
                }
                while (op < end);
            }
            else
            {
                while (op < end)
                {
                    dst[op++] = dst[match++];
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
    private static bool ReadLength(ReadOnlySpan<byte> src, ref int ip, int limit, ref nuint length)
    {
        int p = ip;
        nuint s;
        do
        {
            if (p >= limit)
            {
                return false;
            }

            s = src[p++];
            length += s;
        }
        while (s == 255);

        ip = p;

        // A length past half the address space is no block's.
        return (nint)length >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy16(Span<byte> destination, int to, ReadOnlySpan<byte> source, int from) =>
        Vector128.Create(source.Slice(from, 16)).CopyTo(destination.Slice(to, 16));

    /// <summary>Copies from <paramref name="from"/> until <paramref name="end"/>, thirty-two bytes a step, up to 31 bytes past it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WildCopy32(Span<byte> destination, int to, ReadOnlySpan<byte> source, int from, int end)
    {
        do
        {
            // Each half read after the one before it is written: a source sixteen bytes behind
            // reads what the step itself wrote.
            Copy16(destination, to, source, from);
            Copy16(destination, to + 16, source, from + 16);
            to += 32;
            from += 32;
        }
        while (to < end);
    }

    /// <summary>
    /// A match closer than sixteen bytes, a pattern of <paramref name="offset"/> bytes repeated until
    /// <paramref name="end"/>, up to sixteen bytes past it: the pattern read once and spread to a
    /// vector, stored as many times as needed, each store advancing a whole number of periods.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RepeatPattern(Span<byte> dst, int op, int match, int end, int offset)
    {
        if (AdvSimd.Arm64.IsSupported || Ssse3.IsSupported)
        {
            Vector128<byte> indexes = Vector128.Create(PatternIndexes.Slice(offset * 16, 16));
            Vector128<byte> source = Vector128.Create((ReadOnlySpan<byte>)dst.Slice(match, 16));
            Vector128<byte> pattern = AdvSimd.Arm64.IsSupported
                ? AdvSimd.Arm64.VectorTableLookup(source, indexes)
                : Ssse3.Shuffle(source, indexes);
            int step = PatternSteps[offset];
            do
            {
                pattern.CopyTo(dst.Slice(op, 16));
                op += step;
            }
            while (op < end);

            return;
        }

        while (op < end)
        {
            dst[op++] = dst[match++];
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
