using System;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>The parameters of a <see cref="HashForgetfulChain{TParams}"/>, brotli's template parameters.</summary>
internal interface IForgetfulChainParams
{
    static abstract int NumBanks { get; }

    static abstract int BankBits { get; }

    static abstract int NumLastDistancesToCheck { get; }
}

/// <summary>Qualities 5 and 6's hasher for windows of 64 KiB and less.</summary>
internal struct H40 : IForgetfulChainParams
{
    public static int NumBanks => 1;

    public static int BankBits => 16;

    public static int NumLastDistancesToCheck => 4;
}

/// <summary>Qualities 7 and 8's hasher for windows of 64 KiB and less.</summary>
internal struct H41 : IForgetfulChainParams
{
    public static int NumBanks => 1;

    public static int BankBits => 16;

    public static int NumLastDistancesToCheck => 10;
}

/// <summary>Quality 9's hasher for windows of 64 KiB and less.</summary>
internal struct H42 : IForgetfulChainParams
{
    public static int NumBanks => 512;

    public static int BankBits => 9;

    public static int NumLastDistancesToCheck => 16;
}

/// <summary>
/// brotli's <c>HashForgetfulChain</c> (<c>c/enc/hash_forgetful_chain_inc.h</c>): chains of positions
/// by hash, their nodes in banks whose oldest are overwritten, and a byte of each position's hash.
/// </summary>
/// <remarks>
/// Chains end at a delta past the window, which the 64 KiB windows of these hashers always reach: the
/// nodes past it, and the banks' nodes never written, are not read.
/// </remarks>
internal unsafe struct HashForgetfulChain<TParams> : IHasher
    where TParams : struct, IForgetfulChainParams
{
    private const int BucketBits = 15;
    private const int BucketSize = 1 << BucketBits;

    private static int BankSize => 1 << TParams.BankBits;

    private uint* _addr;
    private ushort* _head;
    private byte* _tinyHash;

    /// <summary>The banks: a delta and a next node, 16 bits each, a node.</summary>
    private uint* _banks;
    private ushort* _freeSlotIdx;
    private nuint _maxHops;
    private nuint _dictNumLookups;
    private nuint _dictNumMatches;

    public static int HashTypeLength => 4;

    public static int StoreLookahead => 4;

    public void Initialize(HasherMemory memory, int quality)
    {
        byte* extra = memory.Get(0, (nuint)((sizeof(uint) * BucketSize) + (sizeof(ushort) * BucketSize) + 65536));
        _addr = (uint*)extra;
        _head = (ushort*)(_addr + BucketSize);
        _tinyHash = (byte*)(_head + BucketSize);
        _banks = (uint*)memory.Get(1, (nuint)(sizeof(uint) * BankSize * TParams.NumBanks));
        _freeSlotIdx = (ushort*)memory.Get(2, (nuint)(sizeof(ushort) * TParams.NumBanks));
        _maxHops = (nuint)((quality > 6 ? 7u : 8u) << (quality - 4));
        _dictNumLookups = 0;
        _dictNumMatches = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint HashBytes(byte* data) => (Unsafe.ReadUnaligned<uint>(data) * HasherCommon.HashMul32) >> (32 - BucketBits);

    public void Prepare(bool oneShot, nuint inputSize, byte* data)
    {
        nuint partialPrepareThreshold = BucketSize >> 6;
        if (oneShot && inputSize <= partialPrepareThreshold)
        {
            for (nuint i = 0; i < inputSize; ++i)
            {
                nuint bucket = HashBytes(data + i);
                _addr[bucket] = 0xCCCCCCCC;
                _head[bucket] = 0xCCCC;
            }
        }
        else
        {
            // Every chain then ends at its first node: no position reaches 3 GiB + 64 MiB.
            new Span<uint>(_addr, BucketSize).Fill(0xCCCCCCCC);
            new Span<ushort>(_head, BucketSize).Clear();
        }

        new Span<byte>(_tinyHash, 65536).Clear();
        new Span<ushort>(_freeSlotIdx, TParams.NumBanks).Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(byte* data, nuint mask, nuint ix)
    {
        nuint key = HashBytes(data + (ix & mask));
        nuint bank = key & (nuint)(TParams.NumBanks - 1);
        nuint idx = (nuint)(_freeSlotIdx[bank]++ & (BankSize - 1));
        nuint delta = ix - _addr[key];
        _tinyHash[(ushort)ix] = (byte)key;
        if (delta > 0xFFFF)
        {
            delta = 0xFFFF;
        }

        _banks[(bank << TParams.BankBits) + idx] = (uint)delta | ((uint)_head[key] << 16);
        _addr[key] = (uint)ix;
        _head[key] = (ushort)idx;
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

    public void PrepareDistanceCache(int* distanceCache) => HasherCommon.PrepareDistanceCache(distanceCache, TParams.NumLastDistancesToCheck);

    public void FindLongestMatch(byte* data, nuint ringBufferMask, int* distanceCache, nuint curIx, nuint maxLength, nuint maxBackward, nuint dictionaryDistance, nuint maxDistance, HasherSearchResult* result)
    {
        nuint curIxMasked = curIx & ringBufferMask;
        nuint minScore = result->Score;
        nuint bestScore = result->Score;
        nuint bestLen = result->Len;
        nuint key = HashBytes(data + curIxMasked);
        byte tinyHash = (byte)key;
        result->Len = 0;
        result->LenCodeDelta = 0;

        for (nuint i = 0; i < (nuint)TParams.NumLastDistancesToCheck; ++i)
        {
            nuint backward = (nuint)(nint)distanceCache[i];
            nuint prevIx = curIx - backward;

            // Two-byte matches at the last distance.
            if (i > 0 && _tinyHash[(ushort)prevIx] != tinyHash)
            {
                continue;
            }

            if (prevIx >= curIx || backward > maxBackward)
            {
                continue;
            }

            prevIx &= ringBufferMask;
            nuint len = FindMatchLength.WithLimit(data + prevIx, data + curIxMasked, maxLength);
            if (len >= 2)
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
            nuint bank = key & (nuint)(TParams.NumBanks - 1);
            uint* slots = _banks + (bank << TParams.BankBits);
            nuint backward = 0;
            nuint hops = _maxHops;
            nuint delta = curIx - _addr[key];
            nuint slot = _head[key];
            while (hops-- != 0)
            {
                nuint last = slot;
                backward += delta;
                if (backward > maxBackward)
                {
                    break;
                }

                nuint prevIx = (curIx - backward) & ringBufferMask;
                uint node = slots[last];
                slot = node >> 16;
                delta = node & 0xFFFF;
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

            Store(data, ringBufferMask, curIx);
        }

        if (result->Score == minScore)
        {
            HasherCommon.SearchInStaticDictionary(ref _dictNumLookups, ref _dictNumMatches, data + curIxMasked, maxLength, dictionaryDistance, maxDistance, result, shallow: false);
        }
    }
}
