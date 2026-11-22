using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>The parameters of a <see cref="HashLongestMatchQuickly{TParams}"/>, brotli's template parameters.</summary>
internal interface IQuicklyParams
{
    static abstract int BucketBits { get; }

    static abstract int BucketSweepBits { get; }

    static abstract int HashLen { get; }

    static abstract bool UseDictionary { get; }
}

/// <summary>Quality 2's hasher.</summary>
internal struct H2 : IQuicklyParams
{
    public static int BucketBits => 16;

    public static int BucketSweepBits => 0;

    public static int HashLen => 5;

    public static bool UseDictionary => true;
}

/// <summary>Quality 3's hasher.</summary>
internal struct H3 : IQuicklyParams
{
    public static int BucketBits => 16;

    public static int BucketSweepBits => 1;

    public static int HashLen => 5;

    public static bool UseDictionary => false;
}

/// <summary>Quality 4's hasher.</summary>
internal struct H4 : IQuicklyParams
{
    public static int BucketBits => 17;

    public static int BucketSweepBits => 2;

    public static int HashLen => 5;

    public static bool UseDictionary => true;
}

/// <summary>Quality 4's hasher for inputs of 1 MiB and more.</summary>
internal struct H54 : IQuicklyParams
{
    public static int BucketBits => 20;

    public static int BucketSweepBits => 2;

    public static int HashLen => 7;

    public static bool UseDictionary => false;
}

/// <summary>
/// brotli's <c>HashLongestMatchQuickly</c> (<c>c/enc/hash_longest_match_quickly_inc.h</c>): a table of
/// the last position of each hash, swept over a few neighbouring buckets.
/// </summary>
internal struct HashLongestMatchQuickly<TParams> : IHasher
    where TParams : struct, IQuicklyParams
{
    private static int BucketSize => 1 << TParams.BucketBits;

    private static uint BucketMask => (uint)BucketSize - 1;

    private static int BucketSweep => 1 << TParams.BucketSweepBits;

    private static uint BucketSweepMask => ((uint)BucketSweep - 1) << 3;

    private uint[] _buckets;
    private nuint _dictNumLookups;
    private nuint _dictNumMatches;

    public static int HashTypeLength => 8;

    public static int StoreLookahead => 8;

    /// <summary>brotli's <c>Initialize</c>, and <c>HasherSetup</c>'s counters.</summary>
    public void Initialize(HasherMemory memory)
    {
        _buckets = memory.Get<uint>(0, (nuint)BucketSize);
        _dictNumLookups = 0;
        _dictNumMatches = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashBytes(ReadOnlySpan<byte> data, nuint at)
    {
        ulong h = (BinaryPrimitives.ReadUInt64LittleEndian(data.Slice((int)at, 8)) << (64 - (8 * TParams.HashLen))) * HasherCommon.HashMul64;
        return (uint)(h >> (64 - TParams.BucketBits));
    }

    public void Prepare(bool oneShot, nuint inputSize, ReadOnlySpan<byte> data)
    {
        Span<uint> buckets = _buckets;
        nuint partialPrepareThreshold = (nuint)BucketSize >> 5;
        if (oneShot && inputSize <= partialPrepareThreshold)
        {
            for (nuint i = 0; i < inputSize; ++i)
            {
                uint key = HashBytes(data, i);
                if (BucketSweep == 1)
                {
                    buckets[(int)key] = 0;
                }
                else
                {
                    for (uint j = 0; j < BucketSweep; ++j)
                    {
                        buckets[(int)((key + (j << 3)) & BucketMask)] = 0;
                    }
                }
            }
        }
        else
        {
            buckets.Slice(0, BucketSize).Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ReadOnlySpan<byte> data, nuint mask, nuint ix)
    {
        uint key = HashBytes(data, ix & mask);
        if (BucketSweep == 1)
        {
            _buckets[(int)key] = (uint)ix;
        }
        else
        {
            uint off = (uint)ix & BucketSweepMask;
            _buckets[(key + off) & BucketMask] = (uint)ix;
        }
    }

    // The lines of the position's slots, the first ones a search reads, for the large tables: 512 KiB for H4,
    // which shares the L2 cache with the window and the commands, 4 MiB for H54.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(ReadOnlySpan<byte> data, nuint mask, nuint ix)
    {
        // The large tables only, and not past the input, after a copy to its end: nothing ahead to fetch there.
        if (Internal.Prefetch.IsSupported && TParams.BucketBits >= 17 && (ix & mask) <= (nuint)data.Length - 8)
        {
            uint key = HashBytes(data, ix & mask);
            Internal.Prefetch.Element(_buckets, (int)key);
            if (BucketSweep > 1)
            {
                Internal.Prefetch.Element(_buckets, (int)((key + ((uint)(BucketSweep - 1) << 3)) & BucketMask));
            }
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

    public void PrepareDistanceCache(Span<int> distanceCache)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FindLongestMatch(ReadOnlySpan<byte> data, nuint ringBufferMask, ReadOnlySpan<int> distanceCache, nuint curIx, nuint maxLength, nuint maxBackward, nuint dictionaryDistance, nuint maxDistance, ref HasherSearchResult result)
    {
        Span<uint> buckets = _buckets;
        nuint bestLenIn = result.Len;
        nuint curIxMasked = curIx & ringBufferMask;
        Prefetch(data, ringBufferMask, curIx + 1);
        int compareChar = data[(int)(curIxMasked + bestLenIn)];
        nuint key = HashBytes(data, curIxMasked);
        nuint keyOut = 0;
        nuint minScore = result.Score;
        nuint bestScore = result.Score;
        nuint bestLen = bestLenIn;
        nuint cachedBackward = (nuint)(nint)distanceCache[0];
        nuint prevIx = curIx - cachedBackward;
        result.LenCodeDelta = 0;
        if (prevIx < curIx)
        {
            prevIx &= (uint)ringBufferMask;
            if (compareChar == data[(int)(prevIx + bestLen)])
            {
                nuint len = FindMatchLength.WithLimit(data, prevIx, curIxMasked, maxLength);
                if (len >= 4)
                {
                    nuint score = HasherCommon.BackwardReferenceScoreUsingLastDistance(len);
                    if (bestScore < score)
                    {
                        result.Len = len;
                        result.Distance = cachedBackward;
                        result.Score = score;
                        if (BucketSweep == 1)
                        {
                            buckets[(int)key] = (uint)curIx;
                            return;
                        }

                        bestLen = len;
                        bestScore = score;
                        compareChar = data[(int)(curIxMasked + len)];
                    }
                }
            }
        }

        if (BucketSweep == 1)
        {
            prevIx = buckets[(int)key];
            buckets[(int)key] = (uint)curIx;
            nuint backward = curIx - prevIx;
            prevIx &= (uint)ringBufferMask;
            if (compareChar != data[(int)(prevIx + bestLenIn)])
            {
                return;
            }

            if (backward == 0 || backward > maxBackward)
            {
                return;
            }

            nuint len = FindMatchLength.WithLimit(data, prevIx, curIxMasked, maxLength);
            if (len >= 4)
            {
                nuint score = HasherCommon.BackwardReferenceScore(len, backward);
                if (bestScore < score)
                {
                    result.Len = len;
                    result.Distance = backward;
                    result.Score = score;
                    return;
                }
            }
        }
        else
        {
            keyOut = (key + ((curIx & BucketSweepMask) >> 3 << 3)) & BucketMask;
            for (int i = 0; i < BucketSweep; ++i)
            {
                prevIx = buckets[(int)((key + ((nuint)i << 3)) & BucketMask)];
                nuint backward = curIx - prevIx;
                prevIx &= (uint)ringBufferMask;
                if (compareChar != data[(int)(prevIx + bestLen)])
                {
                    continue;
                }

                if (backward == 0 || backward > maxBackward)
                {
                    continue;
                }

                nuint len = FindMatchLength.WithLimit(data, prevIx, curIxMasked, maxLength);
                if (len >= 4)
                {
                    nuint score = HasherCommon.BackwardReferenceScore(len, backward);
                    if (bestScore < score)
                    {
                        bestLen = len;
                        result.Len = len;
                        compareChar = data[(int)(curIxMasked + len)];
                        bestScore = score;
                        result.Score = score;
                        result.Distance = backward;
                    }
                }
            }
        }

        if (TParams.UseDictionary && minScore == result.Score)
        {
            HasherCommon.SearchInStaticDictionary(ref _dictNumLookups, ref _dictNumMatches, data.Slice((int)curIxMasked), maxLength, dictionaryDistance, maxDistance, ref result, shallow: true);
        }

        if (BucketSweep != 1)
        {
            buckets[(int)keyOut] = (uint)curIx;
        }
    }
}
