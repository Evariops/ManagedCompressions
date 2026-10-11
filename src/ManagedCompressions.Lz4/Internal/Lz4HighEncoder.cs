using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ManagedCompressions.Lz4.Internal.Lz4Common;

namespace ManagedCompressions.Lz4.Internal;

/// <summary>
/// lz4's high compression, <c>LZ4_compress_HC</c> without a dictionary, levels 2 to 12: the
/// two-table search of level 2, the hash chains of levels 3 to 9, and the optimal parser of levels
/// 10 to 12. It writes the bytes lz4 1.10.0 writes, at every level.
/// </summary>
/// <remarks>
/// lz4 clears its 256 KiB of tables before every block (<c>LZ4_compress_HC_extStateHC</c>). This one
/// keeps them, and numbers each block's positions from 64 KiB past where the last block ended, as lz4
/// itself does when a state is reused without being cleared: every entry an earlier block left lies
/// out of reach of the current block, and every chain link to it is clamped to 64 KiB, which is what a
/// cleared table gives. The tables are cleared only when the numbering nears 1 GiB.
/// </remarks>
internal sealed unsafe class Lz4HighEncoder
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

    private readonly uint[] _hashArray = GC.AllocateArray<uint>(HashTableSize, pinned: true);
    private readonly ushort[] _chainArray = GC.AllocateArray<ushort>(ChainTableSize, pinned: true);
    private readonly OptimalEntry[]? _optArray;
    private readonly uint* _hashTable;
    private readonly ushort* _chainTable;
    private readonly OptimalEntry* _opt;

    private readonly Strategy _strategy;
    private readonly int _nbSearches;
    private readonly int _targetLength;
    private readonly bool _fullUpdate;

    /// <summary>The number of the position after the last block's last.</summary>
    private uint _nextBase;

    // The block being compressed: its first position, and its number.
    private byte* _prefixStart;
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
        _hashTable = (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_hashArray));
        _chainTable = (ushort*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_chainArray));
        if (_strategy == Strategy.Optimal)
        {
            _optArray = GC.AllocateArray<OptimalEntry>(OptimalNum + TrailingLiterals, pinned: true);
            _opt = (OptimalEntry*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_optArray));
        }
    }

    /// <summary>
    /// Compresses <paramref name="length"/> bytes into <paramref name="destination"/>, which holds
    /// <paramref name="capacity"/> bytes; the bytes written, or 0 when they do not fit.
    /// </summary>
    public int Compress(byte* source, int length, byte* destination, int capacity)
    {
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
        _prefixStart = source;
        _prefixIndex = start;
        _nextToUpdate = start;
        _nextBase = start + (uint)length;

        bool limited = capacity < CompressBound(length);
        int result = (_strategy, limited) switch
        {
            (Strategy.Mid, false) => CompressMid<Unlimited>(source, length, destination, capacity),
            (Strategy.Mid, true) => CompressMid<Limited>(source, length, destination, capacity),
            // Pattern analysis from level 9, as lz4 decides it: more than 128 attempts.
            (Strategy.HashChain, false) => _nbSearches > 128
                ? CompressHashChain<Yes, Unlimited>(source, length, destination, capacity)
                : CompressHashChain<No, Unlimited>(source, length, destination, capacity),
            (Strategy.HashChain, true) => _nbSearches > 128
                ? CompressHashChain<Yes, Limited>(source, length, destination, capacity)
                : CompressHashChain<No, Limited>(source, length, destination, capacity),
            (Strategy.Optimal, false) => CompressOptimal<Unlimited>(source, length, destination, capacity),
            _ => CompressOptimal<Limited>(source, length, destination, capacity),
        };

        GC.KeepAlive(this);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashPtr(byte* p) => (Read32(p) * 2654435761u) >> ((MinMatch * 8) - HashLog);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MidHash4(byte* p) => (Read32(p) * 2654435761u) >> (32 - MidHashLog);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MidHash8(byte* p) => (uint)(((Read64(p) << 8) * 58295818150454627UL) >> (64 - MidHashLog));

    /// <summary>The bytes before <paramref name="ip"/> and <paramref name="match"/> they have in common, as a negative number: <c>LZ4HC_countBack</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountBack(byte* ip, byte* match, byte* iMin, byte* mMin)
    {
        int back = 0;
        int min = (int)Math.Max(iMin - ip, mMin - match);
        while (back - min > 3)
        {
            uint v = Read32(ip + back - 4) ^ Read32(match + back - 4);
            if (v != 0)
            {
                return back - (BitOperations.LeadingZeroCount(v) >> 3);
            }

            back -= 4;
        }

        while (back > min && ip[back - 1] == match[back - 1])
        {
            back--;
        }

        return back;
    }

    /// <summary>The bytes from <paramref name="ip"/> that repeat a pattern of 1, 2 or 4 bytes: <c>LZ4HC_countPattern</c>.</summary>
    private static uint CountPattern(byte* ip, byte* iEnd, uint pattern32)
    {
        byte* iStart = ip;
        ulong pattern = pattern32 + ((ulong)pattern32 << 32);
        while (ip < iEnd - 7)
        {
            ulong diff = Read64(ip) ^ pattern;
            if (diff == 0)
            {
                ip += 8;
                continue;
            }

            ip += BitOperations.TrailingZeroCount(diff) >> 3;
            return (uint)(ip - iStart);
        }

        ulong patternByte = pattern;
        while (ip < iEnd && *ip == (byte)patternByte)
        {
            ip++;
            patternByte >>= 8;
        }

        return (uint)(ip - iStart);
    }

    /// <summary>The bytes before <paramref name="ip"/> that repeat the pattern, back to <paramref name="iLow"/>: <c>LZ4HC_reverseCountPattern</c>.</summary>
    private static uint ReverseCountPattern(byte* ip, byte* iLow, uint pattern)
    {
        byte* iStart = ip;
        while (ip >= iLow + 4)
        {
            if (Read32(ip - 4) != pattern)
            {
                break;
            }

            ip -= 4;
        }

        byte* bytePtr = (byte*)&pattern + 3;
        while (ip > iLow)
        {
            if (ip[-1] != *bytePtr)
            {
                break;
            }

            ip--;
            bytePtr--;
        }

        return (uint)(iStart - ip);
    }

    /// <summary>Whether reading four bytes at <paramref name="matchIndex"/> stays before the dictionary's end: <c>LZ4HC_protectDictEnd</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ProtectDictEnd(uint dictLimit, uint matchIndex) => (dictLimit - 1) - matchIndex >= 3;

    /// <summary>Links every position up to <paramref name="ip"/>, excluded, into the hash chains: <c>LZ4HC_Insert</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Insert(byte* ip)
    {
        uint* hashTable = _hashTable;
        ushort* chainTable = _chainTable;
        byte* prefixPtr = _prefixStart;
        uint prefixIdx = _prefixIndex;
        uint target = (uint)(ip - prefixPtr) + prefixIdx;
        uint idx = _nextToUpdate;
        while (idx < target)
        {
            uint h = HashPtr(prefixPtr + idx - prefixIdx);
            uint delta = idx - hashTable[h];
            if (delta > MaxDistance)
            {
                delta = MaxDistance;
            }

            chainTable[(ushort)idx] = (ushort)delta;
            hashTable[h] = idx;
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
    private Match InsertAndGetWiderMatch<TPattern, TSwap>(byte* ip, byte* iLowLimit, byte* iHighLimit, int longest, int maxNbAttempts)
        where TPattern : struct, IFlag
        where TSwap : struct, IFlag
    {
        ushort* chainTable = _chainTable;
        uint* hashTable = _hashTable;
        byte* prefixPtr = _prefixStart;
        uint prefixIdx = _prefixIndex;
        byte* @base = prefixPtr - prefixIdx;
        uint ipIndex = (uint)(ip - prefixPtr) + prefixIdx;
        bool withinStartDistance = prefixIdx + (MaxDistance + 1) > ipIndex;
        uint lowestMatchIndex = withinStartDistance ? prefixIdx : ipIndex - MaxDistance;
        int lookBackLength = (int)(ip - iLowLimit);
        int nbAttempts = maxNbAttempts;
        uint matchChainPos = 0;
        uint pattern = Read32(ip);
        int offset = 0;
        int sBack = 0;
        PatternState state = default;

        Insert(ip);
        uint matchIndex = hashTable[HashPtr(ip)];

        // The two bytes a candidate must share at the end of the longest match so far, read once a
        // longest match rather than once a candidate.
        ushort probe = Read16(iLowLimit + longest - 1);
        nint probeDelta = longest - 1 - lookBackLength;

        while (matchIndex >= lowestMatchIndex && nbAttempts > 0)
        {
            int matchLength = 0;
            nbAttempts--;
            byte* matchPtr = @base + matchIndex;
            if (Read16(matchPtr + probeDelta) == probe && Read32(matchPtr) == pattern)
            {
                int back = lookBackLength != 0 ? CountBack(ip, matchPtr, iLowLimit, prefixPtr) : 0;
                matchLength = MinMatch + (int)CountInline(ip + MinMatch, matchPtr + MinMatch, iHighLimit);
                matchLength -= back;
                if (matchLength > longest)
                {
                    longest = matchLength;
                    offset = (int)(ipIndex - matchIndex);
                    sBack = back;
                    probe = Read16(iLowLimit + longest - 1);
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
                    ? AnalyzePatternInline(ref state, ip, iHighLimit, prefixPtr, prefixIdx, lowestMatchIndex, lookBackLength, ipIndex, pattern, chainTable)
                    : AnalyzePattern(ref state, ip, iHighLimit, prefixPtr, prefixIdx, lowestMatchIndex, lookBackLength, ipIndex, pattern, chainTable);
                if (action != 0)
                {
                    matchIndex = state.MatchIndex;
                    if (state.Longest != longest)
                    {
                        longest = state.Longest;
                        offset = state.Offset;
                        probe = Read16(iLowLimit + longest - 1);
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
    private static int AnalyzePattern(ref PatternState state, byte* ip, byte* iHighLimit, byte* prefixPtr, uint prefixIdx, uint lowestMatchIndex, int lookBackLength, uint ipIndex, uint pattern, ushort* chainTable) =>
        AnalyzePatternInline(ref state, ip, iHighLimit, prefixPtr, prefixIdx, lowestMatchIndex, lookBackLength, ipIndex, pattern, chainTable);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AnalyzePatternInline(ref PatternState state, byte* ip, byte* iHighLimit, byte* prefixPtr, uint prefixIdx, uint lowestMatchIndex, int lookBackLength, uint ipIndex, uint pattern, ushort* chainTable)
    {
        uint matchIndex = state.MatchIndex;
        uint matchCandidateIdx = matchIndex - 1;
        if (state.Repeat == RepeatState.Untested)
        {
            if (((pattern & 0xFFFF) == (pattern >> 16)) & ((pattern & 0xFF) == (pattern >> 24)))
            {
                state.Repeat = RepeatState.Confirmed;
                state.SourcePatternLength = CountPattern(ip + sizeof(uint), iHighLimit, pattern) + sizeof(uint);
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

        byte* matchPtr = prefixPtr + (matchCandidateIdx - prefixIdx);
        if (Read32(matchPtr) != pattern)
        {
            return 0;
        }

        nuint srcPatternLength = state.SourcePatternLength;
        nuint forwardPatternLength = CountPattern(matchPtr + sizeof(uint), iHighLimit, pattern) + sizeof(uint);
        nuint backLength = ReverseCountPattern(matchPtr, prefixPtr, pattern);

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
                if ((nuint)(ip - prefixPtr) + prefixIdx - farthest > MaxDistance)
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
    private static uint CountInline(byte* pIn, byte* pMatch, byte* pInLimit)
    {
        byte* pStart = pIn;
        while (pIn < pInLimit - 7)
        {
            ulong diff = Read64(pMatch) ^ Read64(pIn);
            if (diff != 0)
            {
                return (uint)(pIn - pStart) + ((uint)BitOperations.TrailingZeroCount(diff) >> 3);
            }

            pIn += 8;
            pMatch += 8;
        }

        if (pIn < pInLimit - 3 && Read32(pMatch) == Read32(pIn))
        {
            pIn += 4;
            pMatch += 4;
        }

        if (pIn < pInLimit - 1 && Read16(pMatch) == Read16(pIn))
        {
            pIn += 2;
            pMatch += 2;
        }

        if (pIn < pInLimit && *pMatch == *pIn)
        {
            pIn++;
        }

        return (uint)(pIn - pStart);
    }

    /// <summary>
    /// A sequence: the literals since <paramref name="anchor"/>, then a match at <paramref name="ip"/>;
    /// false when it does not fit before <paramref name="oend"/>: <c>LZ4HC_encodeSequence</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EncodeSequence<TLimit>(ref byte* ip, ref byte* op, ref byte* anchor, int matchLength, int offset, byte* oend)
        where TLimit : struct, ILimit
    {
        byte* token = op++;
        nuint length = (nuint)(ip - anchor);
        if (TLimit.Enabled && op + (length / 255) + length + (2 + 1 + LastLiterals) > oend)
        {
            return false;
        }

        if (length >= RunMask)
        {
            nuint len = length - RunMask;
            *token = RunMask << 4;
            for (; len >= 255; len -= 255)
            {
                *op++ = 255;
            }

            *op++ = (byte)len;
        }
        else
        {
            *token = (byte)(length << 4);
        }

        WildCopy8(op, anchor, op + length);
        op += length;

        Write16(op, (ushort)offset);
        op += 2;

        length = (nuint)matchLength - MinMatch;
        if (TLimit.Enabled && op + (length / 255) + (1 + LastLiterals) > oend)
        {
            return false;
        }

        if (length >= MatchLengthMask)
        {
            *token += MatchLengthMask;
            length -= MatchLengthMask;
            for (; length >= 510; length -= 510)
            {
                *op++ = 255;
                *op++ = 255;
            }

            if (length >= 255)
            {
                length -= 255;
                *op++ = 255;
            }

            *op++ = (byte)length;
        }
        else
        {
            *token += (byte)length;
        }

        ip += matchLength;
        anchor = ip;
        return true;
    }

    /// <summary>The last literals, or 0 when they do not fit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EncodeLastLiterals<TLimit>(byte* op, byte* oend, byte* anchor, byte* iend, byte* dest)
        where TLimit : struct, ILimit
    {
        nuint lastRunSize = (nuint)(iend - anchor);
        nuint llAdd = (lastRunSize + 255 - RunMask) / 255;
        nuint totalSize = 1 + llAdd + lastRunSize;
        if (TLimit.Enabled && op + totalSize > oend)
        {
            return 0;
        }

        op = WriteLastLiterals(op, anchor, lastRunSize);
        return (int)(op - dest);
    }

    /// <summary>Level 2: a table of 8-byte hashes, then one of 4-byte hashes, no chain: <c>LZ4MID_compress</c>.</summary>
    private int CompressMid<TLimit>(byte* src, int srcSize, byte* dst, int maxOutputSize)
        where TLimit : struct, ILimit
    {
        uint* hash4Table = _hashTable;
        uint* hash8Table = _hashTable + MidHashTableSize;
        byte* ip = src;
        byte* anchor = ip;
        byte* iend = ip + srcSize;
        byte* mflimit = iend - MatchFindLimit;
        byte* matchlimit = iend - LastLiterals;
        byte* ilimit = iend - 8;
        byte* op = dst;
        byte* oend = op + maxOutputSize;
        byte* prefixPtr = _prefixStart;
        uint prefixIdx = _prefixIndex;
        uint ilimitIdx = (uint)(ilimit - prefixPtr) + prefixIdx;

        if (srcSize < MinLength)
        {
            goto LastLiteralsLabel;
        }

        while (ip <= mflimit)
        {
            uint ipIndex = (uint)(ip - prefixPtr) + prefixIdx;
            uint matchLength = 0;
            uint matchDistance = 0;

            // A long match first.
            {
                uint h8 = MidHash8(ip);
                uint pos8 = hash8Table[h8];
                hash8Table[h8] = ipIndex;
                if (ipIndex - pos8 <= MaxDistance && pos8 >= prefixIdx)
                {
                    byte* matchPtr = prefixPtr + pos8 - prefixIdx;
                    matchLength = Count(ip, matchPtr, matchlimit);
                    if (matchLength >= MinMatch)
                    {
                        matchDistance = ipIndex - pos8;
                        goto EncodeSequenceLabel;
                    }
                }
            }

            // Then a short one.
            {
                uint h4 = MidHash4(ip);
                uint pos4 = hash4Table[h4];
                hash4Table[h4] = ipIndex;
                if (ipIndex - pos4 <= MaxDistance && pos4 >= prefixIdx)
                {
                    byte* matchPtr = prefixPtr + (pos4 - prefixIdx);
                    matchLength = Count(ip, matchPtr, matchlimit);
                    if (matchLength >= MinMatch)
                    {
                        // A short match: one look at ip + 1 for a longer one.
                        uint h8 = MidHash8(ip + 1);
                        uint pos8 = hash8Table[h8];
                        uint m2Distance = ipIndex + 1 - pos8;
                        matchDistance = ipIndex - pos4;
                        if (m2Distance <= MaxDistance && pos8 >= prefixIdx && ip < mflimit)
                        {
                            byte* m2Ptr = prefixPtr + (pos8 - prefixIdx);
                            uint ml2 = Count(ip + 1, m2Ptr, matchlimit);
                            if (ml2 > matchLength)
                            {
                                hash8Table[h8] = ipIndex + 1;
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
            while ((ip > anchor) & ((uint)(ip - prefixPtr) > matchDistance) && ip[-1] == ip[-(int)matchDistance - 1])
            {
                ip--;
                matchLength++;
            }

            // The table filled with the beginning of the match.
            hash8Table[MidHash8(ip + 1)] = ipIndex + 1;
            hash8Table[MidHash8(ip + 2)] = ipIndex + 2;
            hash4Table[MidHash4(ip + 1)] = ipIndex + 1;

            if (!EncodeSequence<TLimit>(ref ip, ref op, ref anchor, (int)matchLength, (int)matchDistance, oend))
            {
                return 0;
            }

            // And with its end.
            {
                uint endMatchIdx = (uint)(ip - prefixPtr) + prefixIdx;
                uint posM2 = endMatchIdx - 2;
                if (posM2 < ilimitIdx)
                {
                    if (ip - prefixPtr > 5)
                    {
                        hash8Table[MidHash8(ip - 5)] = endMatchIdx - 5;
                    }

                    hash8Table[MidHash8(ip - 3)] = endMatchIdx - 3;
                    hash8Table[MidHash8(ip - 2)] = endMatchIdx - 2;
                    hash4Table[MidHash4(ip - 2)] = endMatchIdx - 2;
                    hash4Table[MidHash4(ip - 1)] = endMatchIdx - 1;
                }
            }
        }

    LastLiteralsLabel:
        return EncodeLastLiterals<TLimit>(op, oend, anchor, iend, dst);
    }

    /// <summary>Levels 3 to 9: hash chains, and up to three overlapping matches weighed: <c>LZ4HC_compress_hashChain</c>.</summary>
    private int CompressHashChain<TPattern, TLimit>(byte* source, int inputSize, byte* dest, int maxOutputSize)
        where TPattern : struct, IFlag
        where TLimit : struct, ILimit
    {
        int maxNbAttempts = _nbSearches;

        byte* ip = source;
        byte* anchor = ip;
        byte* iend = ip + inputSize;
        byte* mflimit = iend - MatchFindLimit;
        byte* matchlimit = iend - LastLiterals;
        byte* op = dest;
        byte* oend = op + maxOutputSize;

        byte* start0;
        byte* start2 = null;
        byte* start3 = null;
        Match m0, m1, m2, m3;

        if (inputSize < MinLength)
        {
            goto LastLiteralsLabel;
        }

        while (ip <= mflimit)
        {
            m1 = InsertAndGetWiderMatch<TPattern, No>(ip, ip, matchlimit, MinMatch - 1, maxNbAttempts);
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
                m2 = InsertAndGetWiderMatch<TPattern, No>(start2, ip, matchlimit, m1.Length, maxNbAttempts);
                start2 += m2.Back;
            }
            else
            {
                m2 = default;
            }

            if (m2.Length <= m1.Length)
            {
                // No better match: ML1 encoded right away.
                if (!EncodeSequence<TLimit>(ref ip, ref op, ref anchor, m1.Length, m1.Offset, oend))
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
                    newMl = (int)(start2 - ip) + m2.Length - MinMatch;
                }

                int correction = newMl - (int)(start2 - ip);
                if (correction > 0)
                {
                    start2 += correction;
                    m2.Length -= correction;
                }
            }

            if (start2 + m2.Length <= mflimit)
            {
                start3 = start2 + m2.Length - 3;
                m3 = InsertAndGetWiderMatch<TPattern, No>(start3, start2, matchlimit, m2.Length, maxNbAttempts);
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
                    m1.Length = (int)(start2 - ip);
                }

                if (!EncodeSequence<TLimit>(ref ip, ref op, ref anchor, m1.Length, m1.Offset, oend))
                {
                    return 0;
                }

                ip = start2;
                if (!EncodeSequence<TLimit>(ref ip, ref op, ref anchor, m2.Length, m2.Offset, oend))
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
                        int correction = (int)(ip + m1.Length - start2);
                        start2 += correction;
                        m2.Length -= correction;
                        if (m2.Length < MinMatch)
                        {
                            start2 = start3;
                            m2 = m3;
                        }
                    }

                    if (!EncodeSequence<TLimit>(ref ip, ref op, ref anchor, m1.Length, m1.Offset, oend))
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
                        m1.Length = (int)(start2 - ip) + m2.Length - MinMatch;
                    }

                    int correction = m1.Length - (int)(start2 - ip);
                    if (correction > 0)
                    {
                        start2 += correction;
                        m2.Length -= correction;
                    }
                }
                else
                {
                    m1.Length = (int)(start2 - ip);
                }
            }

            if (!EncodeSequence<TLimit>(ref ip, ref op, ref anchor, m1.Length, m1.Offset, oend))
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
        return EncodeLastLiterals<TLimit>(op, oend, anchor, iend, dest);
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
    private Match FindLongerMatch(byte* ip, byte* iHighLimit, int minLen, int nbSearches)
    {
        Match md = InsertAndGetWiderMatch<Yes, Yes>(ip, ip, iHighLimit, minLen, nbSearches);
        return md.Length <= minLen ? default : md;
    }

    /// <summary>Levels 10 to 12: the cheapest parse of every stretch the matches cover: <c>LZ4HC_compress_optimal</c>.</summary>
    private int CompressOptimal<TLimit>(byte* source, int srcSize, byte* dst, int dstCapacity)
        where TLimit : struct, ILimit
    {
        OptimalEntry* opt = _opt;
        int nbSearches = _nbSearches;
        int sufficientLen = Math.Min(_targetLength, OptimalNum - 1);
        bool fullUpdate = _fullUpdate;

        byte* ip = source;
        byte* anchor = ip;
        byte* iend = ip + srcSize;
        byte* mflimit = iend - MatchFindLimit;
        byte* matchlimit = iend - LastLiterals;
        byte* op = dst;
        byte* oend = op + dstCapacity;

        // lz4 has no such guard here, and reads through a null source when it is empty. The bytes are
        // the same: a shorter input never enters the loop.
        if (srcSize < MinLength)
        {
            return EncodeLastLiterals<TLimit>(op, oend, anchor, iend, dst);
        }

        while (ip <= mflimit)
        {
            int llen = (int)(ip - anchor);
            int bestMlen;
            int bestOff;
            int cur;
            int lastMatchPos;

            Match firstMatch = FindLongerMatch(ip, matchlimit, MinMatch - 1, nbSearches);
            if (firstMatch.Length == 0)
            {
                ip++;
                continue;
            }

            if (firstMatch.Length > sufficientLen)
            {
                // Good enough: encoded right away.
                if (!EncodeSequence<TLimit>(ref ip, ref op, ref anchor, firstMatch.Length, firstMatch.Offset, oend))
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
                byte* curPtr = ip + cur;
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
                    ? FindLongerMatch(curPtr, matchlimit, MinMatch - 1, nbSearches)
                    : FindLongerMatch(curPtr, matchlimit, lastMatchPos - cur, nbSearches);
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
                    if (!EncodeSequence<TLimit>(ref ip, ref op, ref anchor, ml, offset, oend))
                    {
                        return 0;
                    }
                }
            }
        }

        return EncodeLastLiterals<TLimit>(op, oend, anchor, iend, dst);
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
