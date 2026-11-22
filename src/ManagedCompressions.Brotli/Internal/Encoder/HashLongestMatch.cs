using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;

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
    public void Store(ReadOnlySpan<byte> data, nuint mask, nuint ix)
    {
        uint key = HashBytes(data, ix & mask, _hashShift);
        nuint minorIx = _num[(int)key] & _blockMask;
        nuint offset = minorIx + ((nuint)key << _blockBits);
        _buckets[offset] = (uint)ix;
        ++_num[(int)key];
    }

    // The two newest lines of the position's bucket, the first ones a search reads: at quality 9 the buckets
    // take 32 MiB, and a search reads them while the cache misses.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(ReadOnlySpan<byte> data, nuint mask, nuint ix)
    {
        // A position past the input, after a copy to its end: nothing ahead to fetch.
        if ((ix & mask) > (nuint)data.Length - 4)
        {
            return;
        }

        uint key = HashBytes(data, ix & mask, _hashShift);
        Span<uint> bucket = _buckets.AsSpan((int)((nuint)key << _blockBits), (int)_blockSize);
        nuint top = (nuint)(_num[(int)key] - 1);
        Volatile.Read(ref bucket[(int)(top & _blockMask)]);
        Volatile.Read(ref bucket[(int)((top - 16) & _blockMask)]);
        if (_blockBits >= 6)
        {
            Volatile.Read(ref bucket[(int)((top - 32) & _blockMask)]);
            Volatile.Read(ref bucket[(int)((top - 48) & _blockMask)]);
        }
    }

    public void StoreRange(ReadOnlySpan<byte> data, nuint mask, nuint ixStart, nuint ixEnd)
    {
        for (nuint i = ixStart; i < ixEnd; ++i)
        {
            Store(data, mask, i);
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

        // The distance cache first.
        nuint numLastDistancesToCheck = (nuint)_numLastDistancesToCheck;
        for (nuint i = 0; i < numLastDistancesToCheck; ++i)
        {
            nuint backward = (nuint)(nint)distanceCache[(int)i];
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
            if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || cur[(int)bestLen] != data[(int)(prevIx + bestLen)])
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
                if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || cur[(int)bestLen] != data[(int)(prevIx + bestLen)])
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
            // On a copy: the search is out of line, and the result must not be exposed by its address.
            HasherSearchResult dictionaryMatch = result;
            _dictNumLookups += 2;
            _dictNumMatches += HasherCommon.SearchInStaticDictionaryDeep(cur, maxLength, dictionaryDistance, maxDistance, ref dictionaryMatch);
            result = dictionaryMatch;
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
    public void Store(ReadOnlySpan<byte> data, nuint mask, nuint ix)
    {
        Span<ushort> num = _num;
        nuint key = HashBytes(data, ix & mask, _hashMul);
        nuint minorIx = num[(int)key] & _blockMask;
        nuint offset = minorIx + (key << _blockBits);
        ++num[(int)key];
        _buckets[offset] = (uint)ix;
    }

    // As H5's.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(ReadOnlySpan<byte> data, nuint mask, nuint ix)
    {
        // A position past the input, after a copy to its end: nothing ahead to fetch.
        if ((ix & mask) > (nuint)data.Length - 8)
        {
            return;
        }

        nuint key = HashBytes(data, ix & mask, _hashMul);
        Span<uint> bucket = _buckets.AsSpan((int)((nuint)key << _blockBits), (int)_blockSize);
        nuint top = (nuint)(_num[(int)key] - 1);
        Volatile.Read(ref bucket[(int)(top & _blockMask)]);
        Volatile.Read(ref bucket[(int)((top - 16) & _blockMask)]);
        if (_blockBits >= 6)
        {
            Volatile.Read(ref bucket[(int)((top - 32) & _blockMask)]);
            Volatile.Read(ref bucket[(int)((top - 48) & _blockMask)]);
        }
    }

    public void StoreRange(ReadOnlySpan<byte> data, nuint mask, nuint ixStart, nuint ixEnd)
    {
        for (nuint i = ixStart; i < ixEnd; ++i)
        {
            Store(data, mask, i);
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

        nuint numLastDistancesToCheck = (nuint)_numLastDistancesToCheck;
        for (nuint i = 0; i < numLastDistancesToCheck; ++i)
        {
            nuint backward = (nuint)(nint)distanceCache[(int)i];
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
            if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || cur[(int)bestLen] != data[(int)(prevIx + bestLen)])
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
                if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || cur[(int)bestLen] != data[(int)(prevIx + bestLen)])
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
            // On a copy: the search is out of line, and the result must not be exposed by its address.
            HasherSearchResult dictionaryMatch = result;
            _dictNumLookups += 2;
            _dictNumMatches += HasherCommon.SearchInStaticDictionaryDeep(cur, maxLength, dictionaryDistance, maxDistance, ref dictionaryMatch);
            result = dictionaryMatch;
        }
    }
}
