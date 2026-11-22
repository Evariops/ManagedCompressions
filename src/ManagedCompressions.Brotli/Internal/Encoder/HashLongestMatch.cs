using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's <c>HashLongestMatch</c>, H5 (<c>c/enc/hash_longest_match_inc.h</c>): buckets of the last
/// positions of each 4-byte hash, in rings of <c>1 &lt;&lt; block_bits</c>, and the distance cache's
/// candidates.
/// </summary>
internal struct HashLongestMatch : IHasher
{
    private nuint _bucketSize;
    private nuint _blockSize;
    private int _hashShift;
    private uint _blockMask;
    private int _blockBits;
    private int _numLastDistancesToCheck;
    private ushort[] _num;
    private uint[] _buckets;
    private nuint _dictNumLookups;
    private nuint _dictNumMatches;

    /// <summary>Whether <see cref="Prefetch"/> hints anything for the input being prepared: see <see cref="HasherCommon.PrefetchMinInput"/>.</summary>
    private bool _prefetch;

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
        _num = memory.Get<ushort>(0, _bucketSize);
        _buckets = memory.Get<uint>(1, _bucketSize * _blockSize);
        _dictNumLookups = 0;
        _dictNumMatches = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashBytes(ReadOnlySpan<byte> data, nuint at, int shift) => (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)at, 4)) * HasherCommon.HashMul32) >> shift;

    public void Prepare(bool oneShot, nuint inputSize, ReadOnlySpan<byte> data)
    {
        _prefetch = Internal.Prefetch.IsSupported && (!oneShot || inputSize >= HasherCommon.PrefetchMinInput);
        Span<ushort> num = _num;
        nuint partialPrepareThreshold = _bucketSize >> 6;
        if (oneShot && inputSize <= partialPrepareThreshold)
        {
            for (nuint i = 0; i < inputSize; ++i)
            {
                num[(int)(HashBytes(data, i, _hashShift))] = 0;
            }
        }
        else
        {
            num.Slice(0, (int)_bucketSize).Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ReadOnlySpan<byte> data, nuint mask, nuint ix) =>
        HasherCommon.StoreInBucket(_num, _buckets, (int)HashBytes(data, ix & mask, _hashShift), _blockMask, _blockBits, (uint)ix);

    // The two newest lines of the position's bucket, the first ones a search reads: at quality 9 the buckets
    // take 32 MiB, and a search reads them while the cache misses.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(ReadOnlySpan<byte> data, nuint mask, nuint ix)
    {
        // Nothing to fetch without the instruction, nor past the input, after a copy to its end.
        if (!_prefetch || (ix & mask) > (nuint)data.Length - 4)
        {
            return;
        }

        uint key = HashBytes(data, ix & mask, _hashShift);
        nuint bucket = (nuint)key << _blockBits;
        nuint top = (nuint)(_num[(int)key] - 1);
        Internal.Prefetch.Element(_buckets, (int)(bucket + (top & _blockMask)));
        Internal.Prefetch.Element(_buckets, (int)(bucket + ((top - 16) & _blockMask)));
        if (_blockBits >= 6)
        {
            Internal.Prefetch.Element(_buckets, (int)(bucket + ((top - 32) & _blockMask)));
            Internal.Prefetch.Element(_buckets, (int)(bucket + ((top - 48) & _blockMask)));
        }
    }

    public void StoreRange(ReadOnlySpan<byte> data, nuint mask, nuint ixStart, nuint ixEnd)
    {
        // The fields in locals, which the loop keeps in registers.
        ushort[] num = _num;
        uint[] buckets = _buckets;
        int shift = _hashShift;
        uint blockMask = _blockMask;
        int blockBits = _blockBits;
        for (nuint i = ixStart; i < ixEnd; ++i)
        {
            HasherCommon.StoreInBucket(num, buckets, (int)HashBytes(data, i & mask, shift), blockMask, blockBits, (uint)i);
        }
    }

    public void StitchToPreviousBlock(nuint numBytes, nuint position, ReadOnlySpan<byte> ringBuffer, nuint ringBufferMask)
    {
        if (numBytes >= (nuint)HashTypeLength - 1 && position >= 3)
        {
            Store(ringBuffer, ringBufferMask, position - 3);
            Store(ringBuffer, ringBufferMask, position - 2);
            Store(ringBuffer, ringBufferMask, position - 1);
        }
    }

    public void PrepareDistanceCache(Span<int> distanceCache) => HasherCommon.PrepareDistanceCache(distanceCache, _numLastDistancesToCheck);

    // The best match is kept in locals and stored in the result once: a store through the result's pointer
    // could alias this hasher's fields and the data, so the JIT would reload them for every candidate.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void FindLongestMatch(ReadOnlySpan<byte> data, nuint ringBufferMask, ReadOnlySpan<int> distanceCache, nuint curIx, nuint maxLength, nuint maxBackward, nuint dictionaryDistance, nuint maxDistance, ref HasherSearchResult result)
    {
        Span<ushort> num = _num;
        nuint blockMask = _blockMask;
        nuint curIxMasked = curIx & ringBufferMask;
        ReadOnlySpan<byte> cur = data.Slice((int)curIxMasked);
        nuint minScore = result.Score;
        nuint bestScore = minScore;
        nuint bestLen = result.Len;
        nuint foundLen = 0;
        nuint foundDistance = result.Distance;
        // The next position is nearly always the next one searched, after a literal or to weigh a lazy match.
        Prefetch(data, ringBufferMask, curIx + 1);

        // The byte past the best match, which a longer candidate must match first: it changes only with the best,
        // and is 256, which no byte equals, while the best reaches the end of the ring buffer.
        int curAtBest = curIxMasked + bestLen > ringBufferMask ? 256 : cur[(int)bestLen];

        // The distance cache first.
        ReadOnlySpan<int> lastDistances = distanceCache.Slice(0, _numLastDistancesToCheck);
        for (int i = 0; i < lastDistances.Length; ++i)
        {
            nuint backward = (nuint)(nint)lastDistances[i];
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
            if (prevIx + bestLen > ringBufferMask || curAtBest != data[(int)(prevIx + bestLen)])
            {
                continue;
            }

            nuint len = FindMatchLength.WithLimit(data.Slice((int)prevIx), cur, maxLength);
            if (len >= 3 || (len == 2 && i < 2))
            {
                nuint score = HasherCommon.BackwardReferenceScoreUsingLastDistance(len);
                if (bestScore < score)
                {
                    if (i != 0)
                    {
                        score -= HasherCommon.BackwardReferencePenaltyUsingLastDistance((nuint)i);
                    }

                    if (bestScore < score)
                    {
                        bestScore = score;
                        bestLen = len;
                        curAtBest = curIxMasked + bestLen > ringBufferMask ? 256 : cur[(int)bestLen];
                        foundLen = len;
                        foundDistance = backward;
                    }
                }
            }
        }

        {
            uint key = HashBytes(data, curIxMasked, _hashShift);
            Span<uint> bucket = _buckets.AsSpan((int)((nuint)key << _blockBits), (int)_blockSize);
            nuint count = num[(int)key];
            nuint down = count > _blockSize ? count - _blockSize : 0;
            for (nuint i = count; i > down;)
            {
                nuint prevIx = bucket[(int)(--i & blockMask)];
                nuint backward = curIx - prevIx;
                if (backward > maxBackward)
                {
                    break;
                }

                prevIx &= ringBufferMask;
                if (prevIx + bestLen > ringBufferMask || curAtBest != data[(int)(prevIx + bestLen)])
                {
                    continue;
                }

                nuint len = FindMatchLength.WithLimit(data.Slice((int)prevIx), cur, maxLength);
                if (len >= 4)
                {
                    nuint score = HasherCommon.BackwardReferenceScore(len, backward);
                    if (bestScore < score)
                    {
                        bestScore = score;
                        bestLen = len;
                        curAtBest = curIxMasked + bestLen > ringBufferMask ? 256 : cur[(int)bestLen];
                        foundLen = len;
                        foundDistance = backward;
                    }
                }
            }

            bucket[(int)(count & blockMask)] = (uint)curIx;
            num[(int)key] = (ushort)(count + 1);
        }

        result.Len = foundLen;
        result.LenCodeDelta = 0;
        result.Distance = foundDistance;
        result.Score = bestScore;
        if (minScore == bestScore && _dictNumMatches >= (_dictNumLookups >> 7))
        {
            // Into the result itself: a copy of it, just written field by field, would be read whole before
            // the stores reach the cache.
            _dictNumLookups += 2;
            _dictNumMatches += HasherCommon.SearchInStaticDictionaryDeep(cur, maxLength, dictionaryDistance, maxDistance, ref result);
        }
    }
}

/// <summary>
/// brotli's <c>HashLongestMatch64</c>, H6 (<c>c/enc/hash_longest_match64_inc.h</c>): H5 over 5-byte
/// hashes, for inputs of 1 MiB and more.
/// </summary>
internal struct HashLongestMatch64 : IHasher
{
    private nuint _bucketSize;
    private nuint _blockSize;
    private ulong _hashMul;
    private uint _blockMask;
    private int _blockBits;
    private int _numLastDistancesToCheck;
    private ushort[] _num;
    private uint[] _buckets;
    private nuint _dictNumLookups;
    private nuint _dictNumMatches;

    /// <summary>Whether <see cref="Prefetch"/> hints anything for the input being prepared: see <see cref="HasherCommon.PrefetchMinInput"/>.</summary>
    private bool _prefetch;

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
        _num = memory.Get<ushort>(0, _bucketSize);
        _buckets = memory.Get<uint>(1, _bucketSize * _blockSize);
        _dictNumLookups = 0;
        _dictNumMatches = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint HashBytes(ReadOnlySpan<byte> data, nuint at, ulong hashMul) => (nuint)((BinaryPrimitives.ReadUInt64LittleEndian(data.Slice((int)at, 8)) * hashMul) >> (64 - 15));

    public void Prepare(bool oneShot, nuint inputSize, ReadOnlySpan<byte> data)
    {
        _prefetch = Internal.Prefetch.IsSupported && (!oneShot || inputSize >= HasherCommon.PrefetchMinInput);
        Span<ushort> num = _num;
        nuint partialPrepareThreshold = _bucketSize >> 6;
        if (oneShot && inputSize <= partialPrepareThreshold)
        {
            for (nuint i = 0; i < inputSize; ++i)
            {
                num[(int)(HashBytes(data, i, _hashMul))] = 0;
            }
        }
        else
        {
            num.Slice(0, (int)_bucketSize).Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ReadOnlySpan<byte> data, nuint mask, nuint ix) =>
        HasherCommon.StoreInBucket(_num, _buckets, (int)HashBytes(data, ix & mask, _hashMul), _blockMask, _blockBits, (uint)ix);

    // As H5's.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(ReadOnlySpan<byte> data, nuint mask, nuint ix)
    {
        // Nothing to fetch without the instruction, nor past the input, after a copy to its end.
        if (!_prefetch || (ix & mask) > (nuint)data.Length - 8)
        {
            return;
        }

        nuint key = HashBytes(data, ix & mask, _hashMul);
        nuint bucket = (nuint)key << _blockBits;
        nuint top = (nuint)(_num[(int)key] - 1);
        Internal.Prefetch.Element(_buckets, (int)(bucket + (top & _blockMask)));
        Internal.Prefetch.Element(_buckets, (int)(bucket + ((top - 16) & _blockMask)));
        if (_blockBits >= 6)
        {
            Internal.Prefetch.Element(_buckets, (int)(bucket + ((top - 32) & _blockMask)));
            Internal.Prefetch.Element(_buckets, (int)(bucket + ((top - 48) & _blockMask)));
        }
    }

    public void StoreRange(ReadOnlySpan<byte> data, nuint mask, nuint ixStart, nuint ixEnd)
    {
        // As H5's.
        ushort[] num = _num;
        uint[] buckets = _buckets;
        ulong hashMul = _hashMul;
        uint blockMask = _blockMask;
        int blockBits = _blockBits;
        for (nuint i = ixStart; i < ixEnd; ++i)
        {
            HasherCommon.StoreInBucket(num, buckets, (int)HashBytes(data, i & mask, hashMul), blockMask, blockBits, (uint)i);
        }
    }

    public void StitchToPreviousBlock(nuint numBytes, nuint position, ReadOnlySpan<byte> ringBuffer, nuint ringBufferMask)
    {
        if (numBytes >= (nuint)HashTypeLength - 1 && position >= 3)
        {
            Store(ringBuffer, ringBufferMask, position - 3);
            Store(ringBuffer, ringBufferMask, position - 2);
            Store(ringBuffer, ringBufferMask, position - 1);
        }
    }

    public void PrepareDistanceCache(Span<int> distanceCache) => HasherCommon.PrepareDistanceCache(distanceCache, _numLastDistancesToCheck);

    // As H5's: the best match in locals, stored in the result once.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void FindLongestMatch(ReadOnlySpan<byte> data, nuint ringBufferMask, ReadOnlySpan<int> distanceCache, nuint curIx, nuint maxLength, nuint maxBackward, nuint dictionaryDistance, nuint maxDistance, ref HasherSearchResult result)
    {
        Span<ushort> num = _num;
        nuint blockMask = _blockMask;
        nuint curIxMasked = curIx & ringBufferMask;
        ReadOnlySpan<byte> cur = data.Slice((int)curIxMasked);
        nuint minScore = result.Score;
        nuint bestScore = minScore;
        nuint bestLen = result.Len;
        nuint foundLen = 0;
        nuint foundDistance = result.Distance;
        Prefetch(data, ringBufferMask, curIx + 1);
        int curAtBest = curIxMasked + bestLen > ringBufferMask ? 256 : cur[(int)bestLen];

        ReadOnlySpan<int> lastDistances = distanceCache.Slice(0, _numLastDistancesToCheck);
        for (int i = 0; i < lastDistances.Length; ++i)
        {
            nuint backward = (nuint)(nint)lastDistances[i];
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
            if (prevIx + bestLen > ringBufferMask || curAtBest != data[(int)(prevIx + bestLen)])
            {
                continue;
            }

            nuint len = FindMatchLength.WithLimit(data.Slice((int)prevIx), cur, maxLength);
            if (len >= 3 || (len == 2 && i < 2))
            {
                nuint score = HasherCommon.BackwardReferenceScoreUsingLastDistance(len);
                if (bestScore < score)
                {
                    if (i != 0)
                    {
                        score -= HasherCommon.BackwardReferencePenaltyUsingLastDistance((nuint)i);
                    }

                    if (bestScore < score)
                    {
                        bestScore = score;
                        bestLen = len;
                        curAtBest = curIxMasked + bestLen > ringBufferMask ? 256 : cur[(int)bestLen];
                        foundLen = len;
                        foundDistance = backward;
                    }
                }
            }
        }

        {
            nuint key = HashBytes(data, curIxMasked, _hashMul);
            Span<uint> bucket = _buckets.AsSpan((int)(key << _blockBits), (int)_blockSize);
            nuint count = num[(int)key];
            nuint down = count > _blockSize ? count - _blockSize : 0;
            uint first4 = BinaryPrimitives.ReadUInt32LittleEndian(cur);
            nuint maxLengthM4 = maxLength - 4;
            for (nuint i = count; i > down;)
            {
                nuint prevIx = bucket[(int)(--i & blockMask)];
                nuint backward = curIx - prevIx;
                if (backward > maxBackward)
                {
                    break;
                }

                prevIx &= ringBufferMask;
                if (prevIx + bestLen > ringBufferMask || curAtBest != data[(int)(prevIx + bestLen)])
                {
                    continue;
                }

                if (first4 != BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)prevIx, 4)))
                {
                    continue;
                }

                nuint len = FindMatchLength.WithLimit(data.Slice((int)prevIx + 4), cur.Slice(4), maxLengthM4) + 4;
                nuint score = HasherCommon.BackwardReferenceScore(len, backward);
                if (bestScore < score)
                {
                    bestScore = score;
                    bestLen = len;
                    curAtBest = curIxMasked + bestLen > ringBufferMask ? 256 : cur[(int)bestLen];
                    foundLen = len;
                    foundDistance = backward;
                }
            }

            bucket[(int)(count & blockMask)] = (uint)curIx;
            num[(int)key] = (ushort)(count + 1);
        }

        result.Len = foundLen;
        result.LenCodeDelta = 0;
        result.Distance = foundDistance;
        result.Score = bestScore;
        if (minScore == bestScore && _dictNumMatches >= (_dictNumLookups >> 7))
        {
            // Into the result itself: a copy of it, just written field by field, would be read whole before
            // the stores reach the cache.
            _dictNumLookups += 2;
            _dictNumMatches += HasherCommon.SearchInStaticDictionaryDeep(cur, maxLength, dictionaryDistance, maxDistance, ref result);
        }
    }
}
