using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's <c>HashLongestMatch</c>, H5 (<c>c/enc/hash_longest_match_inc.h</c>): buckets of the last
/// positions of each 4-byte hash, in rings of <c>1 &lt;&lt; block_bits</c>, and the distance cache's
/// candidates.
/// </summary>
internal unsafe struct HashLongestMatch : IHasher
{
    private nuint _bucketSize;
    private nuint _blockSize;
    private int _hashShift;
    private uint _blockMask;
    private int _blockBits;
    private int _numLastDistancesToCheck;
    private ushort* _num;
    private uint* _buckets;
    private nuint _dictNumLookups;
    private nuint _dictNumMatches;

    public static int HashTypeLength => 4;

    public static int StoreLookahead => 4;

    public void Initialize(HasherMemory memory, int bucketBits, int blockBits, int numLastDistancesToCheck)
    {
        _hashShift = 32 - bucketBits;
        _bucketSize = (nuint)1 << bucketBits;
        _blockSize = (nuint)1 << blockBits;
        _blockMask = (uint)(_blockSize - 1);
        _blockBits = blockBits;
        _numLastDistancesToCheck = numLastDistancesToCheck;
        _num = (ushort*)memory.Get(0, sizeof(ushort) * _bucketSize);
        _buckets = (uint*)memory.Get(1, sizeof(uint) * _bucketSize * _blockSize);
        _dictNumLookups = 0;
        _dictNumMatches = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashBytes(byte* data, int shift) => (Unsafe.ReadUnaligned<uint>(data) * HasherCommon.HashMul32) >> shift;

    public void Prepare(bool oneShot, nuint inputSize, byte* data)
    {
        ushort* num = _num;
        nuint partialPrepareThreshold = _bucketSize >> 6;
        if (oneShot && inputSize <= partialPrepareThreshold)
        {
            for (nuint i = 0; i < inputSize; ++i)
            {
                num[HashBytes(data + i, _hashShift)] = 0;
            }
        }
        else
        {
            new Span<ushort>(num, (int)_bucketSize).Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(byte* data, nuint mask, nuint ix)
    {
        uint key = HashBytes(data + (ix & mask), _hashShift);
        nuint minorIx = _num[key] & _blockMask;
        nuint offset = minorIx + ((nuint)key << _blockBits);
        _buckets[offset] = (uint)ix;
        ++_num[key];
    }

    // The two newest lines of the position's bucket, the first ones a search reads: at quality 9 the buckets
    // take 32 MiB, and a search reads them while the cache misses.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(byte* data, nuint mask, nuint ix)
    {
        if (Sse.IsSupported)
        {
            uint key = HashBytes(data + (ix & mask), _hashShift);
            uint* bucket = _buckets + ((nuint)key << _blockBits);
            nuint top = (nuint)(_num[key] - 1);
            Sse.Prefetch0(bucket + (top & _blockMask));
            Sse.Prefetch0(bucket + ((top - 16) & _blockMask));
            if (_blockBits >= 6)
            {
                Sse.Prefetch0(bucket + ((top - 32) & _blockMask));
                Sse.Prefetch0(bucket + ((top - 48) & _blockMask));
            }
        }
    }

    public void StoreRange(byte* data, nuint mask, nuint ixStart, nuint ixEnd)
    {
        for (nuint i = ixStart; i < ixEnd; ++i)
        {
            Store(data, mask, i);
        }
    }

    public void StitchToPreviousBlock(nuint numBytes, nuint position, byte* ringBuffer, nuint ringBufferMask)
    {
        if (numBytes >= (nuint)HashTypeLength - 1 && position >= 3)
        {
            Store(ringBuffer, ringBufferMask, position - 3);
            Store(ringBuffer, ringBufferMask, position - 2);
            Store(ringBuffer, ringBufferMask, position - 1);
        }
    }

    public void PrepareDistanceCache(int* distanceCache) => HasherCommon.PrepareDistanceCache(distanceCache, _numLastDistancesToCheck);

    // The best match is kept in locals and stored in the result once: a store through the result's pointer
    // could alias this hasher's fields and the data, so the JIT would reload them for every candidate.
    public void FindLongestMatch(byte* data, nuint ringBufferMask, int* distanceCache, nuint curIx, nuint maxLength, nuint maxBackward, nuint dictionaryDistance, nuint maxDistance, ref HasherSearchResult result)
    {
        ushort* num = _num;
        nuint blockMask = _blockMask;
        nuint curIxMasked = curIx & ringBufferMask;
        byte* cur = data + curIxMasked;
        nuint minScore = result.Score;
        nuint bestScore = minScore;
        nuint bestLen = result.Len;
        nuint foundLen = 0;
        nuint foundDistance = result.Distance;
        // The next position is nearly always the next one searched, after a literal or to weigh a lazy match.
        Prefetch(data, ringBufferMask, curIx + 1);

        // The distance cache first.
        nuint numLastDistancesToCheck = (nuint)_numLastDistancesToCheck;
        for (nuint i = 0; i < numLastDistancesToCheck; ++i)
        {
            nuint backward = (nuint)(nint)distanceCache[i];
            nuint prevIx = curIx - backward;
            if (prevIx >= curIx)
            {
                continue;
            }

            if (backward > maxBackward)
            {
                continue;
            }

            prevIx &= ringBufferMask;
            if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || cur[bestLen] != data[prevIx + bestLen])
            {
                continue;
            }

            nuint len = FindMatchLength.WithLimit(data + prevIx, cur, maxLength);
            if (len >= 3 || (len == 2 && i < 2))
            {
                nuint score = HasherCommon.BackwardReferenceScoreUsingLastDistance(len);
                if (bestScore < score)
                {
                    if (i != 0)
                    {
                        score -= HasherCommon.BackwardReferencePenaltyUsingLastDistance(i);
                    }

                    if (bestScore < score)
                    {
                        bestScore = score;
                        bestLen = len;
                        foundLen = len;
                        foundDistance = backward;
                    }
                }
            }
        }

        {
            uint key = HashBytes(cur, _hashShift);
            uint* bucket = _buckets + ((nuint)key << _blockBits);
            nuint count = num[key];
            nuint down = count > _blockSize ? count - _blockSize : 0;
            for (nuint i = count; i > down;)
            {
                nuint prevIx = bucket[--i & blockMask];
                nuint backward = curIx - prevIx;
                if (backward > maxBackward)
                {
                    break;
                }

                prevIx &= ringBufferMask;
                if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || cur[bestLen] != data[prevIx + bestLen])
                {
                    continue;
                }

                nuint len = FindMatchLength.WithLimit(data + prevIx, cur, maxLength);
                if (len >= 4)
                {
                    nuint score = HasherCommon.BackwardReferenceScore(len, backward);
                    if (bestScore < score)
                    {
                        bestScore = score;
                        bestLen = len;
                        foundLen = len;
                        foundDistance = backward;
                    }
                }
            }

            bucket[count & blockMask] = (uint)curIx;
            num[key] = (ushort)(count + 1);
        }

        result.Len = foundLen;
        result.LenCodeDelta = 0;
        result.Distance = foundDistance;
        result.Score = bestScore;
        if (minScore == bestScore && _dictNumMatches >= (_dictNumLookups >> 7))
        {
            // On a copy: the search is out of line, and the result must not be exposed by its address.
            HasherSearchResult dictionaryMatch = result;
            _dictNumLookups += 2;
            _dictNumMatches += HasherCommon.SearchInStaticDictionaryDeep(cur, maxLength, dictionaryDistance, maxDistance, &dictionaryMatch);
            result = dictionaryMatch;
        }
    }
}

/// <summary>
/// brotli's <c>HashLongestMatch64</c>, H6 (<c>c/enc/hash_longest_match64_inc.h</c>): H5 over 5-byte
/// hashes, for inputs of 1 MiB and more.
/// </summary>
internal unsafe struct HashLongestMatch64 : IHasher
{
    private nuint _bucketSize;
    private nuint _blockSize;
    private ulong _hashMul;
    private uint _blockMask;
    private int _blockBits;
    private int _numLastDistancesToCheck;
    private ushort* _num;
    private uint* _buckets;
    private nuint _dictNumLookups;
    private nuint _dictNumMatches;

    public static int HashTypeLength => 8;

    public static int StoreLookahead => 8;

    public void Initialize(HasherMemory memory, int bucketBits, int blockBits, int numLastDistancesToCheck)
    {
        _hashMul = HasherCommon.HashMul64 << (64 - (5 * 8));
        _bucketSize = (nuint)1 << bucketBits;
        _blockBits = blockBits;
        _blockSize = (nuint)1 << blockBits;
        _blockMask = (uint)(_blockSize - 1);
        _numLastDistancesToCheck = numLastDistancesToCheck;
        _num = (ushort*)memory.Get(0, sizeof(ushort) * _bucketSize);
        _buckets = (uint*)memory.Get(1, sizeof(uint) * _bucketSize * _blockSize);
        _dictNumLookups = 0;
        _dictNumMatches = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint HashBytes(byte* data, ulong hashMul) => (nuint)((Unsafe.ReadUnaligned<ulong>(data) * hashMul) >> (64 - 15));

    public void Prepare(bool oneShot, nuint inputSize, byte* data)
    {
        ushort* num = _num;
        nuint partialPrepareThreshold = _bucketSize >> 6;
        if (oneShot && inputSize <= partialPrepareThreshold)
        {
            for (nuint i = 0; i < inputSize; ++i)
            {
                num[HashBytes(data + i, _hashMul)] = 0;
            }
        }
        else
        {
            new Span<ushort>(num, (int)_bucketSize).Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(byte* data, nuint mask, nuint ix)
    {
        ushort* num = _num;
        nuint key = HashBytes(data + (ix & mask), _hashMul);
        nuint minorIx = num[key] & _blockMask;
        nuint offset = minorIx + (key << _blockBits);
        ++num[key];
        _buckets[offset] = (uint)ix;
    }

    // As H5's.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(byte* data, nuint mask, nuint ix)
    {
        if (Sse.IsSupported)
        {
            nuint key = HashBytes(data + (ix & mask), _hashMul);
            uint* bucket = _buckets + (key << _blockBits);
            nuint top = (nuint)(_num[key] - 1);
            Sse.Prefetch0(bucket + (top & _blockMask));
            Sse.Prefetch0(bucket + ((top - 16) & _blockMask));
            if (_blockBits >= 6)
            {
                Sse.Prefetch0(bucket + ((top - 32) & _blockMask));
                Sse.Prefetch0(bucket + ((top - 48) & _blockMask));
            }
        }
    }

    public void StoreRange(byte* data, nuint mask, nuint ixStart, nuint ixEnd)
    {
        for (nuint i = ixStart; i < ixEnd; ++i)
        {
            Store(data, mask, i);
        }
    }

    public void StitchToPreviousBlock(nuint numBytes, nuint position, byte* ringBuffer, nuint ringBufferMask)
    {
        if (numBytes >= (nuint)HashTypeLength - 1 && position >= 3)
        {
            Store(ringBuffer, ringBufferMask, position - 3);
            Store(ringBuffer, ringBufferMask, position - 2);
            Store(ringBuffer, ringBufferMask, position - 1);
        }
    }

    public void PrepareDistanceCache(int* distanceCache) => HasherCommon.PrepareDistanceCache(distanceCache, _numLastDistancesToCheck);

    // As H5's: the best match in locals, stored in the result once.
    public void FindLongestMatch(byte* data, nuint ringBufferMask, int* distanceCache, nuint curIx, nuint maxLength, nuint maxBackward, nuint dictionaryDistance, nuint maxDistance, ref HasherSearchResult result)
    {
        ushort* num = _num;
        nuint blockMask = _blockMask;
        nuint curIxMasked = curIx & ringBufferMask;
        byte* cur = data + curIxMasked;
        nuint minScore = result.Score;
        nuint bestScore = minScore;
        nuint bestLen = result.Len;
        nuint foundLen = 0;
        nuint foundDistance = result.Distance;
        Prefetch(data, ringBufferMask, curIx + 1);

        nuint numLastDistancesToCheck = (nuint)_numLastDistancesToCheck;
        for (nuint i = 0; i < numLastDistancesToCheck; ++i)
        {
            nuint backward = (nuint)(nint)distanceCache[i];
            nuint prevIx = curIx - backward;
            if (prevIx >= curIx)
            {
                continue;
            }

            if (backward > maxBackward)
            {
                continue;
            }

            prevIx &= ringBufferMask;
            if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || cur[bestLen] != data[prevIx + bestLen])
            {
                continue;
            }

            nuint len = FindMatchLength.WithLimit(data + prevIx, cur, maxLength);
            if (len >= 3 || (len == 2 && i < 2))
            {
                nuint score = HasherCommon.BackwardReferenceScoreUsingLastDistance(len);
                if (bestScore < score)
                {
                    if (i != 0)
                    {
                        score -= HasherCommon.BackwardReferencePenaltyUsingLastDistance(i);
                    }

                    if (bestScore < score)
                    {
                        bestScore = score;
                        bestLen = len;
                        foundLen = len;
                        foundDistance = backward;
                    }
                }
            }
        }

        {
            nuint key = HashBytes(cur, _hashMul);
            uint* bucket = _buckets + (key << _blockBits);
            nuint count = num[key];
            nuint down = count > _blockSize ? count - _blockSize : 0;
            uint first4 = Unsafe.ReadUnaligned<uint>(cur);
            nuint maxLengthM4 = maxLength - 4;
            for (nuint i = count; i > down;)
            {
                nuint prevIx = bucket[--i & blockMask];
                nuint backward = curIx - prevIx;
                if (backward > maxBackward)
                {
                    break;
                }

                prevIx &= ringBufferMask;
                if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || cur[bestLen] != data[prevIx + bestLen])
                {
                    continue;
                }

                if (first4 != Unsafe.ReadUnaligned<uint>(data + prevIx))
                {
                    continue;
                }

                nuint len = FindMatchLength.WithLimit(data + prevIx + 4, cur + 4, maxLengthM4) + 4;
                nuint score = HasherCommon.BackwardReferenceScore(len, backward);
                if (bestScore < score)
                {
                    bestScore = score;
                    bestLen = len;
                    foundLen = len;
                    foundDistance = backward;
                }
            }

            bucket[count & blockMask] = (uint)curIx;
            num[key] = (ushort)(count + 1);
        }

        result.Len = foundLen;
        result.LenCodeDelta = 0;
        result.Distance = foundDistance;
        result.Score = bestScore;
        if (minScore == bestScore && _dictNumMatches >= (_dictNumLookups >> 7))
        {
            // On a copy: the search is out of line, and the result must not be exposed by its address.
            HasherSearchResult dictionaryMatch = result;
            _dictNumLookups += 2;
            _dictNumMatches += HasherCommon.SearchInStaticDictionaryDeep(cur, maxLength, dictionaryDistance, maxDistance, &dictionaryMatch);
            result = dictionaryMatch;
        }
    }
}
