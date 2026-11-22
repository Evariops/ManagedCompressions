using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using static ManagedCompressions.Lz4.Internal.Lz4Common;

namespace ManagedCompressions.Lz4.Internal;

/// <summary>
/// lz4's high compression, <c>LZ4_compress_HC</c> without a dictionary, levels 2 to 12: the
/// two-table search of level 2, the hash chains of levels 3 to 9, and the optimal parser of levels
/// 10 to 12. It writes the bytes lz4 1.10.0 writes, at every level.
/// </summary>
/// <remarks>
/// <para>
/// lz4 clears its 256 KiB of tables before every block (<c>LZ4_compress_HC_extStateHC</c>). This one
/// keeps them, and numbers each block's positions from 64 KiB past where the last block ended, as lz4
/// itself does when a state is reused without being cleared: every entry an earlier block left lies
/// out of reach of the current block, and every chain link to it is clamped to 64 KiB, which is what a
/// cleared table gives. The tables are cleared only when the numbering nears 1 GiB.
/// </para>
/// <para>
/// A position is an index into the block, its number that index plus the block's first number. The
/// tables are spans of their constant lengths, so that a hash or a 16-bit chain index needs no check.
/// </para>
/// </remarks>
internal sealed class Lz4HighEncoder
{
    private const int HashLog = 15;
    private const int HashTableSize = 1 << HashLog;
    private const int ChainTableSize = 1 << 16;
    private const int MidHashLog = HashLog - 1;
    private const int MidHashTableSize = 1 << MidHashLog;

    /// <summary>The longest match whose length still fits in the token.</summary>
    private const int OptimalMatchLength = MatchLengthMask - 1 + MinMatch;

    private const int OptimalNum = 1 << 12;
    private const int TrailingLiterals = 3;
    private const uint ResetAbove = 1u << 30;

    private enum Strategy
    {
        Mid,
        HashChain,
        Optimal,
    }

    private readonly uint[] _hashArray = new uint[HashTableSize];
    private readonly ushort[] _chainArray = new ushort[ChainTableSize];
    private readonly OptimalEntry[] _opt = [];

    private readonly Strategy _strategy;
    private readonly int _nbSearches;
    private readonly int _targetLength;
    private readonly bool _fullUpdate;

    /// <summary>The number of the position after the last block's last.</summary>
    private uint _nextBase;

    // The block being compressed: the number of its first position, and of the first not yet in the chains.
    private uint _prefixIndex;
    private uint _nextToUpdate;

    public Lz4HighEncoder(int level)
    {
        level = Math.Clamp(level, 2, 12);
        (_strategy, _nbSearches, _targetLength) = level switch
        {
            2 => (Strategy.Mid, 2, 16),
            3 => (Strategy.HashChain, 4, 16),
            4 => (Strategy.HashChain, 8, 16),
            5 => (Strategy.HashChain, 16, 16),
            6 => (Strategy.HashChain, 32, 16),
            7 => (Strategy.HashChain, 64, 16),
            8 => (Strategy.HashChain, 128, 16),
            9 => (Strategy.HashChain, 256, 16),
            10 => (Strategy.Optimal, 96, 64),
            11 => (Strategy.Optimal, 512, 128),
            _ => (Strategy.Optimal, 16384, OptimalNum),
        };
        _fullUpdate = level >= 12;
        if (_strategy == Strategy.Optimal)
        {
            _opt = new OptimalEntry[OptimalNum + TrailingLiterals];
        }
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/>; the bytes written, or 0
    /// when they do not fit.
    /// </summary>
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int length = source.Length;
        if ((uint)length > MaxInputSize)
        {
            return 0;
        }

        if (_nextBase > ResetAbove)
        {
            Array.Clear(_hashArray);
            Array.Clear(_chainArray);
            _nextBase = 0;
        }

        uint start = _nextBase + (64 * 1024);
        _prefixIndex = start;
        _nextToUpdate = start;
        _nextBase = start + (uint)length;

        bool limited = destination.Length < CompressBound(length);
        return (_strategy, limited) switch
        {
            (Strategy.Mid, false) => CompressMid<Unlimited>(source, destination),
            (Strategy.Mid, true) => CompressMid<Limited>(source, destination),
            // Pattern analysis from level 9, as lz4 decides it: more than 128 attempts.
            (Strategy.HashChain, false) => _nbSearches > 128
                ? CompressHashChain<Yes, Unlimited>(source, destination)
                : CompressHashChain<No, Unlimited>(source, destination),
            (Strategy.HashChain, true) => _nbSearches > 128
                ? CompressHashChain<Yes, Limited>(source, destination)
                : CompressHashChain<No, Limited>(source, destination),
            (Strategy.Optimal, false) => CompressOptimal<Unlimited>(source, destination),
            _ => CompressOptimal<Limited>(source, destination),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashPtr(ReadOnlySpan<byte> src, int p) => (Read32(src, p) * 2654435761u) >> ((MinMatch * 8) - HashLog);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MidHash4(ReadOnlySpan<byte> src, int p) => (Read32(src, p) * 2654435761u) >> (32 - MidHashLog);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MidHash8(ReadOnlySpan<byte> src, int p) => (uint)(((Read64(src, p) << 8) * 58295818150454627UL) >> (64 - MidHashLog));

    /// <summary>The bytes before <paramref name="ip"/> and <paramref name="match"/> they have in common, as a negative number: <c>LZ4HC_countBack</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountBack(ReadOnlySpan<byte> src, int ip, int match, int iMin, int mMin)
    {
        int back = 0;
        int min = Math.Max(iMin - ip, mMin - match);
        while (back - min > 3)
        {
            uint v = Read32(src, ip + back - 4) ^ Read32(src, match + back - 4);
            if (v != 0)
            {
                return back - (BitOperations.LeadingZeroCount(v) >> 3);
            }

            back -= 4;
        }

        while (back > min && src[ip + back - 1] == src[match + back - 1])
        {
            back--;
        }

        return back;
    }

    /// <summary>The bytes from <paramref name="ip"/> that repeat a pattern of 1, 2 or 4 bytes: <c>LZ4HC_countPattern</c>.</summary>
    private static uint CountPattern(ReadOnlySpan<byte> src, int ip, int iEnd, uint pattern32)
    {
        int iStart = ip;
        ulong pattern = pattern32 + ((ulong)pattern32 << 32);
        while (ip < iEnd - 7)
        {
            ulong diff = Read64(src, ip) ^ pattern;
            if (diff == 0)
            {
                ip += 8;
                continue;
            }

            ip += BitOperations.TrailingZeroCount(diff) >> 3;
            return (uint)(ip - iStart);
        }

        ulong patternByte = pattern;
        while (ip < iEnd && src[ip] == (byte)patternByte)
        {
            ip++;
            patternByte >>= 8;
        }

        return (uint)(ip - iStart);
    }

    /// <summary>The bytes before <paramref name="ip"/> that repeat the pattern, back to <paramref name="iLow"/>: <c>LZ4HC_reverseCountPattern</c>.</summary>
    private static uint ReverseCountPattern(ReadOnlySpan<byte> src, int ip, int iLow, uint pattern)
    {
        int iStart = ip;
        while (ip >= iLow + 4)
        {
            if (Read32(src, ip - 4) != pattern)
            {
                break;
            }

            ip -= 4;
        }

        // The pattern's bytes from its last, which comes just before ip; a mismatch comes within four.
        int shift = 24;
        while (ip > iLow)
        {
            if (src[ip - 1] != (byte)(pattern >> shift))
            {
                break;
            }

            ip--;
            shift -= 8;
        }

        return (uint)(iStart - ip);
    }

    /// <summary>Whether reading four bytes at <paramref name="matchIndex"/> stays before the dictionary's end: <c>LZ4HC_protectDictEnd</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ProtectDictEnd(uint dictLimit, uint matchIndex) => (dictLimit - 1) - matchIndex >= 3;

    /// <summary>Links every position up to <paramref name="ip"/>, excluded, into the hash chains: <c>LZ4HC_Insert</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Insert(ReadOnlySpan<byte> src, int ip)
    {
        Span<uint> hashTable = _hashArray.AsSpan(0, HashTableSize);
        Span<ushort> chainTable = _chainArray.AsSpan(0, ChainTableSize);
        uint prefixIdx = _prefixIndex;
        uint target = (uint)ip + prefixIdx;
        uint idx = _nextToUpdate;
        while (idx < target)
        {
            uint h = HashPtr(src, (int)(idx - prefixIdx));
            uint delta = idx - hashTable[(int)h];
            if (delta > MaxDistance)
            {
                delta = MaxDistance;
            }

            chainTable[(ushort)idx] = (ushort)delta;
            hashTable[(int)h] = idx;
            idx++;
        }

        _nextToUpdate = target;
    }

    private enum RepeatState
    {
        Untested,
        Not,
        Confirmed,
    }

    private interface IFlag
    {
        static abstract bool On { get; }

        /// <summary>For the pattern analysis: inlined into the search loop rather than called.</summary>
        static abstract bool Inline { get; }
    }

    private struct Yes : IFlag
    {
        public static bool On => true;

        public static bool Inline => false;
    }

    /// <summary>The pattern analysis inlined: where the search is a method of its own, with registers to spare.</summary>
    private struct YesInline : IFlag
    {
        public static bool On => true;

        public static bool Inline => true;
    }

    private struct No : IFlag
    {
        public static bool On => false;

        public static bool Inline => false;
    }

    /// <summary>What the analysis of a repeated pattern reads and changes, apart from the search's registers.</summary>
    private struct PatternState
    {
        public RepeatState Repeat;
        public nuint SourcePatternLength;
        public uint MatchIndex;
        public int Longest;
        public int Offset;
    }

    /// <summary>
    /// The longest match at <paramref name="ip"/> longer than <paramref name="longest"/>, which may
    /// start up to <paramref name="iLowLimit"/> before it: <c>LZ4HC_InsertAndGetWiderMatch</c>, without
    /// a dictionary and favoring the ratio. <typeparamref name="TPattern"/> is its pattern analysis
    /// (level 9 and the optimal parser), <typeparamref name="TSwap"/> its chain swap (the optimal
    /// parser): levels 3 to 8 take neither, and a loop of a few instructions a candidate.
    /// </summary>
    /// <remarks>
    /// Nothing is called from the loop but the analysis of a repeated pattern, which is rare: a call
    /// would keep the loop's values in the few registers a call preserves, and the rest in memory.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Match InsertAndGetWiderMatch<TPattern, TSwap>(ReadOnlySpan<byte> src, int ip, int iLowLimit, int iHighLimit, int longest, int maxNbAttempts)
        where TPattern : struct, IFlag
        where TSwap : struct, IFlag
    {
        Span<ushort> chainTable = _chainArray.AsSpan(0, ChainTableSize);
        Span<uint> hashTable = _hashArray.AsSpan(0, HashTableSize);
        uint prefixIdx = _prefixIndex;
        uint ipIndex = (uint)ip + prefixIdx;
        bool withinStartDistance = prefixIdx + (MaxDistance + 1) > ipIndex;
        uint lowestMatchIndex = withinStartDistance ? prefixIdx : ipIndex - MaxDistance;
        int lookBackLength = ip - iLowLimit;
        int nbAttempts = maxNbAttempts;
        uint matchChainPos = 0;
        uint pattern = Read32(src, ip);
        int offset = 0;
        int sBack = 0;
        PatternState state = default;

        Insert(src, ip);
        uint matchIndex = hashTable[(int)HashPtr(src, ip)];

        // The two bytes a candidate must share at the end of the longest match so far, read once a
        // longest match rather than once a candidate.
        ushort probe = Read16(src, iLowLimit + longest - 1);
        int probeDelta = longest - 1 - lookBackLength;

        while (matchIndex >= lowestMatchIndex && nbAttempts > 0)
        {
            int matchLength = 0;
            nbAttempts--;
            int matchPtr = (int)(matchIndex - prefixIdx);
            if (Read16(src, matchPtr + probeDelta) == probe && Read32(src, matchPtr) == pattern)
            {
                int back = lookBackLength != 0 ? CountBack(src, ip, matchPtr, iLowLimit, 0) : 0;
                matchLength = MinMatch + (int)CountInline(src, ip + MinMatch, matchPtr + MinMatch, iHighLimit);
                matchLength -= back;
                if (matchLength > longest)
                {
                    longest = matchLength;
                    offset = (int)(ipIndex - matchIndex);
                    sBack = back;
                    probe = Read16(src, iLowLimit + longest - 1);
                    probeDelta = longest - 1 - lookBackLength;
                }
            }

            if (TSwap.On && matchLength == longest && matchIndex + (uint)longest <= ipIndex)
            {
                // A better match: follow the chain of whichever of its positions reaches furthest back.
                const int KTrigger = 4;
                uint distanceToNextMatch = 1;
                int end = longest - MinMatch + 1;
                int step = 1;
                int accel = 1 << KTrigger;
                for (int pos = 0; pos < end; pos += step)
                {
                    uint candidateDist = chainTable[(ushort)(matchIndex + (uint)pos)];
                    step = accel++ >> KTrigger;
                    if (candidateDist > distanceToNextMatch)
                    {
                        distanceToNextMatch = candidateDist;
                        matchChainPos = (uint)pos;
                        accel = 1 << KTrigger;
                    }
                }

                if (distanceToNextMatch > 1)
                {
                    if (distanceToNextMatch > matchIndex)
                    {
                        break;
                    }

                    matchIndex -= distanceToNextMatch;
                    continue;
                }
            }

            if (TPattern.On && chainTable[(ushort)matchIndex] == 1 && (!TSwap.On || matchChainPos == 0))
            {
                state.MatchIndex = matchIndex;
                state.Longest = longest;
                state.Offset = offset;
                int action = TPattern.Inline
                    ? AnalyzePatternInline(ref state, src, ip, iHighLimit, prefixIdx, lowestMatchIndex, lookBackLength, ipIndex, pattern, chainTable)
                    : AnalyzePattern(ref state, src, ip, iHighLimit, prefixIdx, lowestMatchIndex, lookBackLength, ipIndex, pattern, chainTable);
                if (action != 0)
                {
                    matchIndex = state.MatchIndex;
                    if (state.Longest != longest)
                    {
                        longest = state.Longest;
                        offset = state.Offset;
                        probe = Read16(src, iLowLimit + longest - 1);
                        probeDelta = longest - 1 - lookBackLength;
                    }

                    if (action == 2)
                    {
                        break;
                    }

                    continue;
                }
            }

            // Follow the current chain.
            matchIndex -= chainTable[(ushort)(matchIndex + (TSwap.On ? matchChainPos : 0))];
        }

        return new Match(longest, offset, sBack);
    }

    /// <summary>
    /// The candidate is one position past another of the same hash: maybe a repeated pattern, which
    /// the chain would walk a position at a time. 0 when it is not one, and the chain is followed; 1
    /// when the search continues at <see cref="PatternState.MatchIndex"/>; 2 when it stops.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AnalyzePattern(ref PatternState state, ReadOnlySpan<byte> src, int ip, int iHighLimit, uint prefixIdx, uint lowestMatchIndex, int lookBackLength, uint ipIndex, uint pattern, Span<ushort> chainTable) =>
        AnalyzePatternInline(ref state, src, ip, iHighLimit, prefixIdx, lowestMatchIndex, lookBackLength, ipIndex, pattern, chainTable);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AnalyzePatternInline(ref PatternState state, ReadOnlySpan<byte> src, int ip, int iHighLimit, uint prefixIdx, uint lowestMatchIndex, int lookBackLength, uint ipIndex, uint pattern, Span<ushort> chainTable)
    {
        uint matchIndex = state.MatchIndex;
        uint matchCandidateIdx = matchIndex - 1;
        if (state.Repeat == RepeatState.Untested)
        {
            if (((pattern & 0xFFFF) == (pattern >> 16)) & ((pattern & 0xFF) == (pattern >> 24)))
            {
                state.Repeat = RepeatState.Confirmed;
                state.SourcePatternLength = CountPattern(src, ip + sizeof(uint), iHighLimit, pattern) + sizeof(uint);
            }
            else
            {
                state.Repeat = RepeatState.Not;
            }
        }

        if (state.Repeat != RepeatState.Confirmed || matchCandidateIdx < lowestMatchIndex || !ProtectDictEnd(prefixIdx, matchCandidateIdx))
        {
            return 0;
        }

        int matchPtr = (int)(matchCandidateIdx - prefixIdx);
        if (Read32(src, matchPtr) != pattern)
        {
            return 0;
        }

        nuint srcPatternLength = state.SourcePatternLength;
        nuint forwardPatternLength = CountPattern(src, matchPtr + sizeof(uint), iHighLimit, pattern) + sizeof(uint);
        nuint backLength = ReverseCountPattern(src, matchPtr, 0, pattern);

        // No further back than the lowest index.
        backLength = matchCandidateIdx - Math.Max(matchCandidateIdx - (uint)backLength, lowestMatchIndex);
        nuint currentSegmentLength = backLength + forwardPatternLength;
        if (currentSegmentLength >= srcPatternLength && forwardPatternLength <= srcPatternLength)
        {
            // The best position: the whole pattern, maybe followed by more.
            uint newMatchIndex = matchCandidateIdx + (uint)forwardPatternLength - (uint)srcPatternLength;
            state.MatchIndex = ProtectDictEnd(prefixIdx, newMatchIndex) ? newMatchIndex : prefixIdx;
            return 1;
        }

        // The farthest position in the current segment.
        uint farthest = matchCandidateIdx - (uint)backLength;
        if (!ProtectDictEnd(prefixIdx, farthest))
        {
            state.MatchIndex = prefixIdx;
            return 1;
        }

        state.MatchIndex = farthest;
        if (lookBackLength == 0)
        {
            nuint maxML = Math.Min(currentSegmentLength, srcPatternLength);
            if ((nuint)state.Longest < maxML)
            {
                if ((nuint)ip + prefixIdx - farthest > MaxDistance)
                {
                    return 2;
                }

                state.Longest = (int)maxML;
                state.Offset = (int)(ipIndex - farthest);
            }

            uint distToNextPattern = chainTable[(ushort)farthest];
            if (distToNextPattern > farthest)
            {
                return 2;
            }

            state.MatchIndex = farthest - distToNextPattern;
        }

        return 1;
    }

    /// <summary><see cref="Lz4Common.Count"/>, inlined whole: no call out of the search loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CountInline(ReadOnlySpan<byte> src, int pIn, int pMatch, int pInLimit)
    {
        int pStart = pIn;
        while (pIn < pInLimit - 7)
        {
            ulong diff = Read64(src, pMatch) ^ Read64(src, pIn);
            if (diff != 0)
            {
                return (uint)(pIn - pStart) + ((uint)BitOperations.TrailingZeroCount(diff) >> 3);
            }

            pIn += 8;
            pMatch += 8;
        }

        if (pIn < pInLimit - 3 && Read32(src, pMatch) == Read32(src, pIn))
        {
            pIn += 4;
            pMatch += 4;
        }

        if (pIn < pInLimit - 1 && Read16(src, pMatch) == Read16(src, pIn))
        {
            pIn += 2;
            pMatch += 2;
        }

        if (pIn < pInLimit && src[pMatch] == src[pIn])
        {
            pIn++;
        }

        return (uint)(pIn - pStart);
    }

    /// <summary>
    /// A sequence: the literals since <paramref name="anchor"/>, then a match at <paramref name="ip"/>;
    /// false when it does not fit in <paramref name="dst"/>: <c>LZ4HC_encodeSequence</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EncodeSequence<TLimit>(ReadOnlySpan<byte> src, Span<byte> dst, ref int ip, ref int op, ref int anchor, int matchLength, int offset)
        where TLimit : struct, ILimit
    {
        int token = op++;
        int length = ip - anchor;
        if (TLimit.Enabled && (long)op + (length / 255) + length + (2 + 1 + LastLiterals) > dst.Length)
        {
            return false;
        }

        if (length >= RunMask)
        {
            int len = length - RunMask;
            dst[token] = RunMask << 4;
            for (; len >= 255; len -= 255)
            {
                dst[op++] = 255;
            }

            dst[op++] = (byte)len;
        }
        else
        {
            dst[token] = (byte)(length << 4);
        }

        WildCopy8(dst, op, src, anchor, length);
        op += length;

        Write16(dst, op, (ushort)offset);
        op += 2;

        length = matchLength - MinMatch;
        if (TLimit.Enabled && (long)op + (length / 255) + (1 + LastLiterals) > dst.Length)
        {
            return false;
        }

        if (length >= MatchLengthMask)
        {
            dst[token] += MatchLengthMask;
            length -= MatchLengthMask;
            for (; length >= 510; length -= 510)
            {
                dst[op++] = 255;
                dst[op++] = 255;
            }

            if (length >= 255)
            {
                length -= 255;
                dst[op++] = 255;
            }

            dst[op++] = (byte)length;
        }
        else
        {
            dst[token] += (byte)length;
        }

        ip += matchLength;
        anchor = ip;
        return true;
    }

    /// <summary>The last literals, from <paramref name="anchor"/> to the source's end; the bytes written, or 0 when they do not fit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EncodeLastLiterals<TLimit>(ReadOnlySpan<byte> src, Span<byte> dst, int op, int anchor)
        where TLimit : struct, ILimit
    {
        int lastRunSize = src.Length - anchor;
        int llAdd = (lastRunSize + 255 - RunMask) / 255;
        long totalSize = 1L + llAdd + lastRunSize;
        if (TLimit.Enabled && op + totalSize > dst.Length)
        {
            return 0;
        }

        return WriteLastLiterals(dst, op, src, anchor, lastRunSize);
    }

    /// <summary>Level 2: a table of 8-byte hashes, then one of 4-byte hashes, no chain: <c>LZ4MID_compress</c>.</summary>
    private int CompressMid<TLimit>(ReadOnlySpan<byte> src, Span<byte> dst)
        where TLimit : struct, ILimit
    {
        Span<uint> hash4Table = _hashArray.AsSpan(0, MidHashTableSize);
        Span<uint> hash8Table = _hashArray.AsSpan(MidHashTableSize, MidHashTableSize);
        int srcSize = src.Length;
        int ip = 0;
        int anchor = 0;
        int mflimit = srcSize - MatchFindLimit;
        int matchlimit = srcSize - LastLiterals;
        int ilimit = srcSize - 8;
        int op = 0;
        uint prefixIdx = _prefixIndex;
        uint ilimitIdx = (uint)ilimit + prefixIdx;

        if (srcSize < MinLength)
        {
            goto LastLiteralsLabel;
        }

        while (ip <= mflimit)
        {
            uint ipIndex = (uint)ip + prefixIdx;
            uint matchLength = 0;
            uint matchDistance = 0;

            // A long match first.
            {
                uint h8 = MidHash8(src, ip);
                uint pos8 = hash8Table[(int)h8];
                hash8Table[(int)h8] = ipIndex;
                if (ipIndex - pos8 <= MaxDistance && pos8 >= prefixIdx)
                {
                    int matchPtr = (int)(pos8 - prefixIdx);
                    matchLength = Count(src, ip, matchPtr, matchlimit);
                    if (matchLength >= MinMatch)
                    {
                        matchDistance = ipIndex - pos8;
                        goto EncodeSequenceLabel;
                    }
                }
            }

            // Then a short one.
            {
                uint h4 = MidHash4(src, ip);
                uint pos4 = hash4Table[(int)h4];
                hash4Table[(int)h4] = ipIndex;
                if (ipIndex - pos4 <= MaxDistance && pos4 >= prefixIdx)
                {
                    int matchPtr = (int)(pos4 - prefixIdx);
                    matchLength = Count(src, ip, matchPtr, matchlimit);
                    if (matchLength >= MinMatch)
                    {
                        // A short match: one look at ip + 1 for a longer one.
                        uint h8 = MidHash8(src, ip + 1);
                        uint pos8 = hash8Table[(int)h8];
                        uint m2Distance = ipIndex + 1 - pos8;
                        matchDistance = ipIndex - pos4;
                        if (m2Distance <= MaxDistance && pos8 >= prefixIdx && ip < mflimit)
                        {
                            int m2Ptr = (int)(pos8 - prefixIdx);
                            uint ml2 = Count(src, ip + 1, m2Ptr, matchlimit);
                            if (ml2 > matchLength)
                            {
                                hash8Table[(int)h8] = ipIndex + 1;
                                ip++;
                                matchLength = ml2;
                                matchDistance = m2Distance;
                            }
                        }

                        goto EncodeSequenceLabel;
                    }
                }
            }

            // No match: faster over incompressible data.
            ip += 1 + ((ip - anchor) >> 9);
            continue;

        EncodeSequenceLabel:
            // Catch back.
            while ((ip > anchor) & ((uint)ip > matchDistance) && src[ip - 1] == src[ip - (int)matchDistance - 1])
            {
                ip--;
                matchLength++;
            }

            // The table filled with the beginning of the match.
            hash8Table[(int)MidHash8(src, ip + 1)] = ipIndex + 1;
            hash8Table[(int)MidHash8(src, ip + 2)] = ipIndex + 2;
            hash4Table[(int)MidHash4(src, ip + 1)] = ipIndex + 1;

            if (!EncodeSequence<TLimit>(src, dst, ref ip, ref op, ref anchor, (int)matchLength, (int)matchDistance))
            {
                return 0;
            }

            // And with its end.
            {
                uint endMatchIdx = (uint)ip + prefixIdx;
                uint posM2 = endMatchIdx - 2;
                if (posM2 < ilimitIdx)
                {
                    if (ip > 5)
                    {
                        hash8Table[(int)MidHash8(src, ip - 5)] = endMatchIdx - 5;
                    }

                    hash8Table[(int)MidHash8(src, ip - 3)] = endMatchIdx - 3;
                    hash8Table[(int)MidHash8(src, ip - 2)] = endMatchIdx - 2;
                    hash4Table[(int)MidHash4(src, ip - 2)] = endMatchIdx - 2;
                    hash4Table[(int)MidHash4(src, ip - 1)] = endMatchIdx - 1;
                }
            }
        }

    LastLiteralsLabel:
        return EncodeLastLiterals<TLimit>(src, dst, op, anchor);
    }

    /// <summary>Levels 3 to 9: hash chains, and up to three overlapping matches weighed: <c>LZ4HC_compress_hashChain</c>.</summary>
    private int CompressHashChain<TPattern, TLimit>(ReadOnlySpan<byte> src, Span<byte> dst)
        where TPattern : struct, IFlag
        where TLimit : struct, ILimit
    {
        int maxNbAttempts = _nbSearches;

        int inputSize = src.Length;
        int ip = 0;
        int anchor = 0;
        int mflimit = inputSize - MatchFindLimit;
        int matchlimit = inputSize - LastLiterals;
        int op = 0;

        int start0;
        int start2 = 0;
        int start3 = 0;
        Match m0, m1, m2, m3;

        if (inputSize < MinLength)
        {
            goto LastLiteralsLabel;
        }

        while (ip <= mflimit)
        {
            m1 = InsertAndGetWiderMatch<TPattern, No>(src, ip, ip, matchlimit, MinMatch - 1, maxNbAttempts);
            if (m1.Length < MinMatch)
            {
                ip++;
                continue;
            }

            // Saved, in case we would skip too much.
            start0 = ip;
            m0 = m1;

        Search2:
            if (ip + m1.Length <= mflimit)
            {
                start2 = ip + m1.Length - 2;
                m2 = InsertAndGetWiderMatch<TPattern, No>(src, start2, ip, matchlimit, m1.Length, maxNbAttempts);
                start2 += m2.Back;
            }
            else
            {
                m2 = default;
            }

            if (m2.Length <= m1.Length)
            {
                // No better match: ML1 encoded right away.
                if (!EncodeSequence<TLimit>(src, dst, ref ip, ref op, ref anchor, m1.Length, m1.Offset))
                {
                    return 0;
                }

                continue;
            }

            if (start0 < ip && start2 < ip + m0.Length)
            {
                // The first match was skipped at least once: ML1 squeezed between ML0 and ML2.
                ip = start0;
                m1 = m0;
            }

            if (start2 - ip < 3)
            {
                // The first match too small: removed.
                ip = start2;
                m1 = m2;
                goto Search2;
            }

        Search3:
            if (start2 - ip < OptimalMatchLength)
            {
                int newMl = m1.Length;
                if (newMl > OptimalMatchLength)
                {
                    newMl = OptimalMatchLength;
                }

                if (ip + newMl > start2 + m2.Length - MinMatch)
                {
                    newMl = start2 - ip + m2.Length - MinMatch;
                }

                int correction = newMl - (start2 - ip);
                if (correction > 0)
                {
                    start2 += correction;
                    m2.Length -= correction;
                }
            }

            if (start2 + m2.Length <= mflimit)
            {
                start3 = start2 + m2.Length - 3;
                m3 = InsertAndGetWiderMatch<TPattern, No>(src, start3, start2, matchlimit, m2.Length, maxNbAttempts);
                start3 += m3.Back;
            }
            else
            {
                m3 = default;
            }

            if (m3.Length <= m2.Length)
            {
                // No better match: ML1 and ML2 encoded.
                if (start2 < ip + m1.Length)
                {
                    m1.Length = start2 - ip;
                }

                if (!EncodeSequence<TLimit>(src, dst, ref ip, ref op, ref anchor, m1.Length, m1.Offset))
                {
                    return 0;
                }

                ip = start2;
                if (!EncodeSequence<TLimit>(src, dst, ref ip, ref op, ref anchor, m2.Length, m2.Offset))
                {
                    return 0;
                }

                continue;
            }

            if (start3 < ip + m1.Length + 3)
            {
                // Not enough room for match 2: removed.
                if (start3 >= ip + m1.Length)
                {
                    // Seq1 written right away, Seq2 removed, so Seq3 becomes Seq1.
                    if (start2 < ip + m1.Length)
                    {
                        int correction = ip + m1.Length - start2;
                        start2 += correction;
                        m2.Length -= correction;
                        if (m2.Length < MinMatch)
                        {
                            start2 = start3;
                            m2 = m3;
                        }
                    }

                    if (!EncodeSequence<TLimit>(src, dst, ref ip, ref op, ref anchor, m1.Length, m1.Offset))
                    {
                        return 0;
                    }

                    ip = start3;
                    m1 = m3;

                    start0 = start2;
                    m0 = m2;
                    goto Search2;
                }

                start2 = start3;
                m2 = m3;
                goto Search3;
            }

            // Three ascending matches: the first one written, its length decided.
            if (start2 < ip + m1.Length)
            {
                if (start2 - ip < OptimalMatchLength)
                {
                    if (m1.Length > OptimalMatchLength)
                    {
                        m1.Length = OptimalMatchLength;
                    }

                    if (ip + m1.Length > start2 + m2.Length - MinMatch)
                    {
                        m1.Length = start2 - ip + m2.Length - MinMatch;
                    }

                    int correction = m1.Length - (start2 - ip);
                    if (correction > 0)
                    {
                        start2 += correction;
                        m2.Length -= correction;
                    }
                }
                else
                {
                    m1.Length = start2 - ip;
                }
            }

            if (!EncodeSequence<TLimit>(src, dst, ref ip, ref op, ref anchor, m1.Length, m1.Offset))
            {
                return 0;
            }

            // ML2 becomes ML1, ML3 becomes ML2, and a new ML3 is searched.
            ip = start2;
            m1 = m2;
            start2 = start3;
            m2 = m3;
            goto Search3;
        }

    LastLiteralsLabel:
        return EncodeLastLiterals<TLimit>(src, dst, op, anchor);
    }

    /// <summary>The cost of a run of literals, in bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LiteralsPrice(int litlen)
    {
        int price = litlen;
        if (litlen >= RunMask)
        {
            price += 1 + ((litlen - RunMask) / 255);
        }

        return price;
    }

    /// <summary>The cost of a sequence, in bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SequencePrice(int litlen, int mlen)
    {
        int price = 1 + 2 + LiteralsPrice(litlen);
        if (mlen >= MatchLengthMask + MinMatch)
        {
            price += 1 + ((mlen - (MatchLengthMask + MinMatch)) / 255);
        }

        return price;
    }

    /// <summary>
    /// The optimal parser's search: a method of its own, called once a position, where the search
    /// loop has the registers to itself rather than share them with the parser's.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Match FindLongerMatch(ReadOnlySpan<byte> src, int ip, int iHighLimit, int minLen, int nbSearches)
    {
        Match md = InsertAndGetWiderMatch<Yes, Yes>(src, ip, ip, iHighLimit, minLen, nbSearches);
        return md.Length <= minLen ? default : md;
    }

    /// <summary>Levels 10 to 12: the cheapest parse of every stretch the matches cover: <c>LZ4HC_compress_optimal</c>.</summary>
    private int CompressOptimal<TLimit>(ReadOnlySpan<byte> src, Span<byte> dst)
        where TLimit : struct, ILimit
    {
        OptimalEntry[] opt = _opt;
        int nbSearches = _nbSearches;
        int sufficientLen = Math.Min(_targetLength, OptimalNum - 1);
        bool fullUpdate = _fullUpdate;

        int srcSize = src.Length;
        int ip = 0;
        int anchor = 0;
        int mflimit = srcSize - MatchFindLimit;
        int matchlimit = srcSize - LastLiterals;
        int op = 0;

        // lz4 has no such guard here, and reads through a null source when it is empty. The bytes are
        // the same: a shorter input never enters the loop.
        if (srcSize < MinLength)
        {
            return EncodeLastLiterals<TLimit>(src, dst, op, anchor);
        }

        while (ip <= mflimit)
        {
            int llen = ip - anchor;
            int bestMlen;
            int bestOff;
            int cur;
            int lastMatchPos;

            Match firstMatch = FindLongerMatch(src, ip, matchlimit, MinMatch - 1, nbSearches);
            if (firstMatch.Length == 0)
            {
                ip++;
                continue;
            }

            if (firstMatch.Length > sufficientLen)
            {
                // Good enough: encoded right away.
                if (!EncodeSequence<TLimit>(src, dst, ref ip, ref op, ref anchor, firstMatch.Length, firstMatch.Offset))
                {
                    return 0;
                }

                continue;
            }

            // The prices of the first positions, literals.
            for (int rPos = 0; rPos < MinMatch; rPos++)
            {
                opt[rPos] = new OptimalEntry(LiteralsPrice(llen + rPos), 0, 1, llen + rPos);
            }

            // The prices with the first match.
            {
                int matchML = firstMatch.Length;
                int offset = firstMatch.Offset;
                for (int mlen = MinMatch; mlen <= matchML; mlen++)
                {
                    opt[mlen] = new OptimalEntry(SequencePrice(llen, mlen), offset, mlen, llen);
                }
            }

            lastMatchPos = firstMatch.Length;
            for (int addLit = 1; addLit <= TrailingLiterals; addLit++)
            {
                opt[lastMatchPos + addLit] = new OptimalEntry(opt[lastMatchPos].Price + LiteralsPrice(addLit), 0, 1, addLit);
            }

            // The positions further on.
            for (cur = 1; cur < lastMatchPos; cur++)
            {
                int curPtr = ip + cur;
                if (curPtr > mflimit)
                {
                    break;
                }

                if (fullUpdate)
                {
                    // No use searching where the next position costs as much or less, unless the
                    // cost rises sharply after it.
                    if (opt[cur + 1].Price <= opt[cur].Price && opt[cur + MinMatch].Price < opt[cur].Price + 3)
                    {
                        continue;
                    }
                }
                else if (opt[cur + 1].Price <= opt[cur].Price)
                {
                    continue;
                }

                Match newMatch = fullUpdate
                    ? FindLongerMatch(src, curPtr, matchlimit, MinMatch - 1, nbSearches)
                    : FindLongerMatch(src, curPtr, matchlimit, lastMatchPos - cur, nbSearches);
                if (newMatch.Length == 0)
                {
                    continue;
                }

                if (newMatch.Length > sufficientLen || newMatch.Length + cur >= OptimalNum)
                {
                    // Encoded right away.
                    bestMlen = newMatch.Length;
                    bestOff = newMatch.Offset;
                    lastMatchPos = cur + 1;
                    goto Encode;
                }

                // Before the match: the prices with literals first.
                {
                    int baseLitlen = opt[cur].Litlen;
                    for (int litlen = 1; litlen < MinMatch; litlen++)
                    {
                        int price = opt[cur].Price - LiteralsPrice(baseLitlen) + LiteralsPrice(baseLitlen + litlen);
                        int pos = cur + litlen;
                        if (price < opt[pos].Price)
                        {
                            opt[pos] = new OptimalEntry(price, 0, 1, baseLitlen + litlen);
                        }
                    }
                }

                // The prices with the match at cur.
                {
                    int matchML = newMatch.Length;
                    for (int ml = MinMatch; ml <= matchML; ml++)
                    {
                        int pos = cur + ml;
                        int offset = newMatch.Offset;
                        int price;
                        int ll;
                        if (opt[cur].Mlen == 1)
                        {
                            ll = opt[cur].Litlen;
                            price = (cur > ll ? opt[cur - ll].Price : 0) + SequencePrice(ll, ml);
                        }
                        else
                        {
                            ll = 0;
                            price = opt[cur].Price + SequencePrice(0, ml);
                        }

                        if (pos > lastMatchPos + TrailingLiterals || price <= opt[pos].Price)
                        {
                            if (ml == matchML && lastMatchPos < pos)
                            {
                                lastMatchPos = pos;
                            }

                            opt[pos] = new OptimalEntry(price, offset, ml, ll);
                        }
                    }
                }

                // The positions after, literals.
                for (int addLit = 1; addLit <= TrailingLiterals; addLit++)
                {
                    opt[lastMatchPos + addLit] = new OptimalEntry(opt[lastMatchPos].Price + LiteralsPrice(addLit), 0, 1, addLit);
                }
            }

            bestMlen = opt[lastMatchPos].Mlen;
            bestOff = opt[lastMatchPos].Off;
            cur = lastMatchPos - bestMlen;

        Encode:
            // Backwards, from the end to the beginning: the shortest path.
            {
                int candidatePos = cur;
                int selectedMatchLength = bestMlen;
                int selectedOffset = bestOff;
                while (true)
                {
                    int nextMatchLength = opt[candidatePos].Mlen;
                    int nextOffset = opt[candidatePos].Off;
                    opt[candidatePos].Mlen = selectedMatchLength;
                    opt[candidatePos].Off = selectedOffset;
                    selectedMatchLength = nextMatchLength;
                    selectedOffset = nextOffset;
                    if (nextMatchLength > candidatePos)
                    {
                        break;
                    }

                    candidatePos -= nextMatchLength;
                }
            }

            // Every sequence recorded, in order.
            {
                int rPos = 0;
                while (rPos < lastMatchPos)
                {
                    int ml = opt[rPos].Mlen;
                    int offset = opt[rPos].Off;
                    if (ml == 1)
                    {
                        ip++;
                        rPos++;
                        continue;
                    }

                    rPos += ml;
                    if (!EncodeSequence<TLimit>(src, dst, ref ip, ref op, ref anchor, ml, offset))
                    {
                        return 0;
                    }
                }
            }
        }

        return EncodeLastLiterals<TLimit>(src, dst, op, anchor);
    }

    private struct Match(int length, int offset, int back)
    {
        public int Length = length;
        public int Offset = offset;

        /// <summary>How far before the searched position the match starts, as a negative number.</summary>
        public int Back = back;
    }

    private struct OptimalEntry(int price, int off, int mlen, int litlen)
    {
        public int Price = price;
        public int Off = off;
        public int Mlen = mlen;
        public int Litlen = litlen;
    }
}
