using System;
using System.Runtime.CompilerServices;

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

    public void FindLongestMatch(byte* data, nuint ringBufferMask, int* distanceCache, nuint curIx, nuint maxLength, nuint maxBackward, nuint dictionaryDistance, nuint maxDistance, HasherSearchResult* result)
    {
        ushort* num = _num;
        uint* buckets = _buckets;
        nuint curIxMasked = curIx & ringBufferMask;
        nuint minScore = result->Score;
        nuint bestScore = result->Score;
        nuint bestLen = result->Len;
        result->Len = 0;
        result->LenCodeDelta = 0;

        // The distance cache first.
        for (nuint i = 0; i < (nuint)_numLastDistancesToCheck; ++i)
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
            if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || data[curIxMasked + bestLen] != data[prevIx + bestLen])
            {
                continue;
            }

            nuint len = FindMatchLength.WithLimit(data + prevIx, data + curIxMasked, maxLength);
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
                        result->Len = bestLen;
                        result->Distance = backward;
                        result->Score = bestScore;
                    }
                }
            }
        }

        {
            uint key = HashBytes(data + curIxMasked, _hashShift);
            uint* bucket = buckets + ((nuint)key << _blockBits);
            nuint count = num[key];
            nuint down = count > _blockSize ? count - _blockSize : 0;
            for (nuint i = count; i > down;)
            {
                nuint prevIx = bucket[--i & _blockMask];
                nuint backward = curIx - prevIx;
                if (backward > maxBackward)
                {
                    break;
                }

                prevIx &= ringBufferMask;
                if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || data[curIxMasked + bestLen] != data[prevIx + bestLen])
                {
                    continue;
                }

                nuint len = FindMatchLength.WithLimit(data + prevIx, data + curIxMasked, maxLength);
                if (len >= 4)
                {
                    nuint score = HasherCommon.BackwardReferenceScore(len, backward);
                    if (bestScore < score)
                    {
                        bestScore = score;
                        bestLen = len;
                        result->Len = bestLen;
                        result->Distance = backward;
                        result->Score = bestScore;
                    }
                }
            }

            bucket[count & _blockMask] = (uint)curIx;
            num[key] = (ushort)(count + 1);
        }

        if (minScore == result->Score)
        {
            HasherCommon.SearchInStaticDictionary(ref _dictNumLookups, ref _dictNumMatches, data + curIxMasked, maxLength, dictionaryDistance, maxDistance, result, shallow: false);
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

    public void FindLongestMatch(byte* data, nuint ringBufferMask, int* distanceCache, nuint curIx, nuint maxLength, nuint maxBackward, nuint dictionaryDistance, nuint maxDistance, HasherSearchResult* result)
    {
        ushort* num = _num;
        uint* buckets = _buckets;
        nuint curIxMasked = curIx & ringBufferMask;
        nuint minScore = result->Score;
        nuint bestScore = result->Score;
        nuint bestLen = result->Len;
        result->Len = 0;
        result->LenCodeDelta = 0;

        for (nuint i = 0; i < (nuint)_numLastDistancesToCheck; ++i)
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
            if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || data[curIxMasked + bestLen] != data[prevIx + bestLen])
            {
                continue;
            }

            nuint len = FindMatchLength.WithLimit(data + prevIx, data + curIxMasked, maxLength);
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
                        result->Len = bestLen;
                        result->Distance = backward;
                        result->Score = bestScore;
                    }
                }
            }
        }

        {
            nuint key = HashBytes(data + curIxMasked, _hashMul);
            uint* bucket = buckets + (key << _blockBits);
            nuint count = num[key];
            nuint down = count > _blockSize ? count - _blockSize : 0;
            uint first4 = Unsafe.ReadUnaligned<uint>(data + curIxMasked);
            nuint maxLengthM4 = maxLength - 4;
            for (nuint i = count; i > down;)
            {
                nuint prevIx = bucket[--i & _blockMask];
                nuint backward = curIx - prevIx;
                if (backward > maxBackward)
                {
                    break;
                }

                prevIx &= ringBufferMask;
                if (curIxMasked + bestLen > ringBufferMask || prevIx + bestLen > ringBufferMask || data[curIxMasked + bestLen] != data[prevIx + bestLen])
                {
                    continue;
                }

                if (first4 != Unsafe.ReadUnaligned<uint>(data + prevIx))
                {
                    continue;
                }

                nuint len = FindMatchLength.WithLimit(data + prevIx + 4, data + curIxMasked + 4, maxLengthM4) + 4;
                nuint score = HasherCommon.BackwardReferenceScore(len, backward);
                if (bestScore < score)
                {
                    bestScore = score;
                    bestLen = len;
                    result->Len = bestLen;
                    result->Distance = backward;
                    result->Score = bestScore;
                }
            }

            bucket[count & _blockMask] = (uint)curIx;
            num[key] = (ushort)(count + 1);
        }

        if (minScore == result->Score)
        {
            HasherCommon.SearchInStaticDictionary(ref _dictNumLookups, ref _dictNumMatches, data + curIxMasked, maxLength, dictionaryDistance, maxDistance, result, shallow: false);
        }
    }
}
