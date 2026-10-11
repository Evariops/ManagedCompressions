using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ManagedCompressions.Lz4.Internal.Lz4Common;

namespace ManagedCompressions.Lz4.Internal;

/// <summary>
/// lz4's fast compressor, <c>LZ4_compress_fast</c> without a dictionary: one hash table of the last
/// position of each hash, a step that grows the longer nothing matches, and every match taken
/// greedily. It writes the bytes lz4 1.10.0 writes, at every acceleration.
/// </summary>
/// <remarks>
/// <para>
/// lz4 clears its 16 KiB table before every block. This one keeps its table from block to block and
/// numbers positions on from where the last block ended: an entry left by an earlier block lies
/// below the current block's first position, and reads as that first position, which is what a
/// cleared entry reads as in lz4. Only when the numbering nears 2 GiB is the table cleared.
/// </para>
/// <para>
/// Inputs under 64 KiB plus 11 bytes hash four bytes to 13 bits, as lz4's 16-bit table does; larger
/// ones hash five bytes to 12 bits, as its 32-bit table does. Both share the one table of 8192
/// entries, since any entry from another block reads as the block's start.
/// </para>
/// </remarks>
internal sealed unsafe class Lz4FastEncoder
{
    private const int SkipTrigger = 6;
    private const int Limit64K = (64 * 1024) + (MatchFindLimit - 1);
    /// <summary>lz4's 16-bit table: 13 bits of a four-byte hash (LZ4_HASHLOG + 1).</summary>
    private const int SmallHashLog = 13;

    /// <summary>lz4's 32-bit table on a 64-bit machine: 12 bits of a five-byte hash (LZ4_HASHLOG).</summary>
    private const int LargeHashLog = 12;

    private const int TableSize = 1 << (SmallHashLog > LargeHashLog ? SmallHashLog : LargeHashLog);

    /// <summary>Where the numbering restarts from zero, the table cleared.</summary>
    private const uint ResetAbove = 1u << 30;

    /// <summary>
    /// Below this, a block keeps the tagged table rather than clearing one: 16 KiB cleared costs more
    /// than such a block takes to compress, where for a larger one the clamp of every entry costs more.
    /// </summary>
    private const int ClearAbove = 4096;

    /// <summary>The table blocks of every size share, its entries tagged by the numbering.</summary>
    private readonly uint[] _table = GC.AllocateArray<uint>(TableSize, pinned: true);

    /// <summary>lz4's own 16-bit table, cleared before each block between <see cref="ClearAbove"/> and 64 KiB.</summary>
    private readonly ushort[] _table16 = GC.AllocateArray<ushort>(TableSize, pinned: true);

    /// <summary>
    /// The search's cold values, behind a pointer the JIT cannot promote to registers: a stackalloc
    /// would do the same, but keeps the method from tiering, and from its profile.
    /// </summary>
    private readonly Cold[] _cold = GC.AllocateArray<Cold>(1, pinned: true);

    /// <summary>The number of the next block's first position.</summary>
    private uint _nextIndex;

    /// <summary>
    /// Compresses <paramref name="length"/> bytes into <paramref name="destination"/>, which holds
    /// <paramref name="capacity"/> bytes; the bytes written, or 0 when they do not fit.
    /// </summary>
    public int Compress(byte* source, int length, byte* destination, int capacity, int acceleration)
    {
        if ((uint)length > MaxInputSize)
        {
            return 0;
        }

        if (length == 0)
        {
            if (capacity <= 0)
            {
                return 0;
            }

            *destination = 0;
            return 1;
        }

        bool limited = capacity < CompressBound(length);
        if (length >= ClearAbove)
        {
            // A cleared table numbered from the block's start, lz4's own: no entry to clamp.
            if (length < Limit64K)
            {
                fixed (ushort* table = _table16)
                {
                    Unsafe.InitBlockUnaligned(table, 0, (1 << SmallHashLog) * sizeof(ushort));
                    return limited
                        ? Compress<Small16, Limited>(ColdPointer, (uint*)table, 0, source, length, destination, capacity, acceleration)
                        : Compress<Small16, Unlimited>(ColdPointer, (uint*)table, 0, source, length, destination, capacity, acceleration);
                }
            }

            fixed (uint* table = _table)
            {
                Unsafe.InitBlockUnaligned(table, 0, (1 << LargeHashLog) * sizeof(uint));

                // Its entries lie below the block's length: a tagged block after it starts above.
                _nextIndex = Math.Max(_nextIndex, (uint)length);
                return limited
                    ? Compress<Large32, Limited>(ColdPointer, table, 0, source, length, destination, capacity, acceleration)
                    : Compress<Large32, Unlimited>(ColdPointer, table, 0, source, length, destination, capacity, acceleration);
            }
        }

        if (_nextIndex > ResetAbove)
        {
            Array.Clear(_table);
            _nextIndex = 0;
        }

        uint startIndex = _nextIndex;
        _nextIndex += (uint)length;
        fixed (uint* table = _table)
        {
            return limited
                ? Compress<SmallTagged, Limited>(ColdPointer, table, startIndex, source, length, destination, capacity, acceleration)
                : Compress<SmallTagged, Unlimited>(ColdPointer, table, startIndex, source, length, destination, capacity, acceleration);
        }
    }

    /// <summary>
    /// What the search loop does not touch, in memory the JIT does not promote (stack allocated): in
    /// registers, these pushed the loop's own out.
    /// </summary>
    private Cold* ColdPointer => (Cold*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_cold));

    private struct Cold
    {
        public byte* Anchor;
        public byte* Op;
        public byte* Source;
        public byte* End;
        public byte* OutputLimit;
        public int Acceleration;
    }

    /// <summary>
    /// <c>LZ4_compress_generic_validated</c>, for a single block without a dictionary. The bytes read
    /// at a position serve both its hash and the compare with its candidate, as clang compiles lz4's
    /// loop: one load a position.
    /// </summary>
    private static int Compress<TTable, TLimit>(Cold* cold, uint* table, uint startIndex, byte* source, int inputSize, byte* dest, int capacity, int acceleration)
        where TTable : struct, ITable
        where TLimit : struct, ILimit
    {
        cold->Anchor = source;
        cold->Op = dest;
        cold->Source = source;
        cold->End = source + inputSize;
        cold->OutputLimit = dest + capacity;
        cold->Acceleration = acceleration;

        byte* ip = source;
        byte* @base = source - startIndex;
        byte* mflimitPlusOne = source + inputSize - MatchFindLimit + 1;
        ulong seq;

        if (inputSize < MinLength)
        {
            goto LastLiteralsLabel;
        }

        TTable.Put(table, TTable.Hash(Read64(ip)), startIndex);
        ip++;
        seq = Read64(ip);

        while (true)
        {
            byte* match;

            // Find a match, the step growing by one every 64 misses (times the acceleration). The
            // table is read for a position before it is written with it, and written before the
            // next position is read, as lz4 does.
            {
                nuint step = 1;
                nuint searchMatchNb = (nuint)cold->Acceleration << SkipTrigger;
                while (true)
                {
                    // lz4 leaves without testing a position whose next would pass the limit.
                    if (ip + step > mflimitPlusOne)
                    {
                        goto LastLiteralsLabel;
                    }

                    nuint h = TTable.Hash(seq);
                    uint matchIndex = TTable.Get(table, h);
                    uint current = (uint)(ip - @base);
                    TTable.Put(table, h, current);
                    if (TTable.Tagged)
                    {
                        // An entry from an earlier block reads as this block's first position, as a
                        // cleared entry does in lz4.
                        matchIndex = matchIndex < startIndex ? startIndex : matchIndex;
                    }

                    match = @base + matchIndex;
                    if ((!TTable.CheckDistance || current - matchIndex < MaxDistance + 1) && Read32(match) == (uint)seq)
                    {
                        break;
                    }

                    ip += step;
                    step = searchMatchNb >> SkipTrigger;
                    searchMatchNb++;
                    seq = Read64(ip);
                }
            }

            byte* anchor = cold->Anchor;
            byte* op = cold->Op;
            byte* token;

            // Catch up: extend the match backwards over the literals.
            {
                byte* lowLimit = cold->Source;
                if (match > lowLimit && ip[-1] == match[-1])
                {
                    do
                    {
                        ip--;
                        match--;
                    }
                    while (ip > anchor && match > lowLimit && ip[-1] == match[-1]);
                }
            }

            // The literals.
            {
                uint litLength = (uint)(ip - anchor);
                token = op++;
                if (TLimit.Enabled && op + litLength + (2 + 1 + LastLiterals) + (litLength / 255) > cold->OutputLimit)
                {
                    return 0;
                }

                if (litLength >= RunMask)
                {
                    uint len = litLength - RunMask;
                    *token = RunMask << 4;
                    for (; len >= 255; len -= 255)
                    {
                        *op++ = 255;
                    }

                    *op++ = (byte)len;
                }
                else
                {
                    *token = (byte)(litLength << 4);
                }

                WildCopy8(op, anchor, op + litLength);
                op += litLength;
            }

        NextMatch:
            Write16(op, (ushort)(ip - match));
            op += 2;

            {
                uint matchCode = Count(ip + MinMatch, match + MinMatch, cold->End - LastLiterals);
                ip += matchCode + MinMatch;
                if (TLimit.Enabled && op + (1 + LastLiterals) + ((matchCode + 240) / 255) > cold->OutputLimit)
                {
                    return 0;
                }

                if (matchCode >= MatchLengthMask)
                {
                    *token += MatchLengthMask;
                    matchCode -= MatchLengthMask;
                    Write32(op, 0xFFFFFFFF);
                    while (matchCode >= 4 * 255)
                    {
                        op += 4;
                        Write32(op, 0xFFFFFFFF);
                        matchCode -= 4 * 255;
                    }

                    op += matchCode / 255;
                    *op++ = (byte)(matchCode % 255);
                }
                else
                {
                    *token += (byte)matchCode;
                }
            }

            cold->Anchor = ip;
            cold->Op = op;
            if (ip >= mflimitPlusOne)
            {
                break;
            }

            // Fill the table with a position inside the match.
            TTable.Put(table, TTable.Hash(Read64(ip - 2)), (uint)(ip - 2 - @base));

            // A match right away, with no literal between.
            {
                ulong next = Read64(ip);
                nuint h = TTable.Hash(next);
                uint current = (uint)(ip - @base);
                uint matchIndex = TTable.Get(table, h);
                if (TTable.Tagged)
                {
                    matchIndex = matchIndex < startIndex ? startIndex : matchIndex;
                }

                match = @base + matchIndex;
                TTable.Put(table, h, current);
                if ((!TTable.CheckDistance || current - matchIndex < MaxDistance + 1) && Read32(match) == (uint)next)
                {
                    token = op++;
                    *token = 0;
                    goto NextMatch;
                }
            }

            seq = Read64(++ip);
        }

    LastLiteralsLabel:
        {
            byte* anchor = cold->Anchor;
            byte* op = cold->Op;
            nuint lastRun = (nuint)(cold->End - anchor);
            if (TLimit.Enabled && op + lastRun + 1 + ((lastRun + 255 - RunMask) / 255) > cold->OutputLimit)
            {
                return 0;
            }

            op = WriteLastLiterals(op, anchor, lastRun);
            return (int)(op - dest);
        }
    }

    private interface ITable
    {
        /// <summary>Whether entries from earlier blocks remain, to be read as the block's start.</summary>
        static abstract bool Tagged { get; }

        /// <summary>Whether a candidate may be further than 64 KiB.</summary>
        static abstract bool CheckDistance { get; }

        /// <summary>The hash, computed in 64 bits so that no zero extension follows it.</summary>
        static abstract nuint Hash(ulong sequence);

        static abstract uint Get(uint* table, nuint hash);

        static abstract void Put(uint* table, nuint hash, uint index);
    }

    /// <summary>lz4's 16-bit table: four bytes hashed to 13 bits, every position within 64 KiB, cleared before the block.</summary>
    private struct Small16 : ITable
    {
        public static bool Tagged => false;

        public static bool CheckDistance => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint Hash(ulong sequence) => (nuint)((((ulong)(uint)sequence * 2654435761UL) >> (32 - SmallHashLog)) & ((1 << SmallHashLog) - 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Get(uint* table, nuint hash) => ((ushort*)table)[hash];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Put(uint* table, nuint hash, uint index) => ((ushort*)table)[hash] = (ushort)index;
    }

    /// <summary>The same hash into the tagged 32-bit table, for blocks too small to clear one.</summary>
    private struct SmallTagged : ITable
    {
        public static bool Tagged => true;

        public static bool CheckDistance => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint Hash(ulong sequence) => (nuint)((((ulong)(uint)sequence * 2654435761UL) >> (32 - SmallHashLog)) & ((1 << SmallHashLog) - 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Get(uint* table, nuint hash) => table[hash];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Put(uint* table, nuint hash, uint index) => table[hash] = index;
    }

    /// <summary>lz4's 32-bit table on a 64-bit machine: five bytes hashed to 12 bits, cleared before the block.</summary>
    private struct Large32 : ITable
    {
        public static bool Tagged => false;

        public static bool CheckDistance => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint Hash(ulong sequence) => (nuint)(((sequence << 24) * 889523592379UL) >> (64 - LargeHashLog));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Get(uint* table, nuint hash) => table[hash];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Put(uint* table, nuint hash, uint index) => table[hash] = index;
    }
}

/// <summary>Whether an encoder checks the room left in its destination: lz4's <c>limitedOutput</c>.</summary>
internal interface ILimit
{
    static abstract bool Enabled { get; }
}

/// <summary>The destination holds the bound: nothing to check.</summary>
internal struct Unlimited : ILimit
{
    public static bool Enabled => false;
}

/// <summary>The destination may be smaller than the bound: every write checked, 0 when it does not fit.</summary>
internal struct Limited : ILimit
{
    public static bool Enabled => true;
}
