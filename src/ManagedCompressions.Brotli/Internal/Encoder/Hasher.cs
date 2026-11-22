using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>brotli's <c>HasherSearchResult</c>: the best match so far.</summary>
internal struct HasherSearchResult
{
    public nuint Len;
    public nuint Distance;
    public nuint Score;
    public int LenCodeDelta;
}

/// <summary>
/// A hasher of <c>c/enc/hash.h</c>, as a struct, so that the backward reference search is compiled for
/// each one, as brotli's templates are.
/// </summary>
internal interface IHasher
{
    /// <summary>The bytes a hash reads: the search stops that many bytes before the end.</summary>
    static abstract int HashTypeLength { get; }

    /// <summary>The bytes past a position a store reads.</summary>
    static abstract int StoreLookahead { get; }

    /// <summary>Clears the tables, or only the entries of the input's positions when it is small and whole.</summary>
    void Prepare(bool oneShot, nuint inputSize, ReadOnlySpan<byte> data);

    void Store(ReadOnlySpan<byte> data, nuint mask, nuint ix);

    void StoreRange(ReadOnlySpan<byte> data, nuint mask, nuint ixStart, nuint ixEnd);

    /// <summary>
    /// Fetches into the cache what the search at <paramref name="ix"/> reads first, ahead of it: no effect on
    /// the output, and nothing for the hashers whose tables stay in the cache.
    /// </summary>
    void Prefetch(ReadOnlySpan<byte> data, nuint mask, nuint ix);

    /// <summary>Stores the last three positions of the previous block, which needed the bytes of this one.</summary>
    void StitchToPreviousBlock(nuint numBytes, nuint position, ReadOnlySpan<byte> ringBuffer, nuint ringBufferMask);

    void PrepareDistanceCache(Span<int> distanceCache);

    void FindLongestMatch(ReadOnlySpan<byte> data, nuint ringBufferMask, ReadOnlySpan<int> distanceCache, nuint curIx, nuint maxLength, nuint maxBackward, nuint dictionaryDistance, nuint maxDistance, ref HasherSearchResult result);
}

/// <summary>The common part of brotli's hashers: scores, hashes, and the static dictionary's lookup.</summary>
internal static class HasherCommon
{
    public const uint HashMul32 = 0x1E35A7BD;
    public const ulong HashMul64 = 0x1FE35A7BD3579BD3;

    /// <summary>
    /// The one-shot input below which the hashers do not prefetch: the table lines its positions reach stay in
    /// the caches, and the hints only cost their instructions (7 to 8 % at qualities 7 and 9 on small records).
    /// </summary>
    public const nuint PrefetchMinInput = 1 << 16;

    /// <summary>
    /// Appends <paramref name="ix"/> to the bucket of <paramref name="key"/>: a ring of <c>1 &lt;&lt; blockBits</c>
    /// positions in <paramref name="buckets"/>, whose count <paramref name="num"/> keeps (H5's and H6's).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreInBucket(ushort[] num, uint[] buckets, int key, uint blockMask, int blockBits, uint ix)
    {
        ref ushort count = ref num[key];
        buckets[(int)((count & blockMask) + ((uint)key << blockBits))] = ix;
        count++;
    }
    public const int LiteralByteScore = 135;
    public const int DistanceBitPenalty = 30;

    /// <summary>brotli's <c>BROTLI_SCORE_BASE</c>, which depends on the size of <c>size_t</c>.</summary>
    public static nuint ScoreBase
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (nuint)(DistanceBitPenalty * 8 * IntPtr.Size);
    }

    private const int CutoffTransformsCount = 10;
    private const ulong CutoffTransforms = 0x071B520ADA2D3200;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint BackwardReferenceScore(nuint copyLength, nuint backwardReferenceOffset) =>
        ScoreBase + (LiteralByteScore * copyLength) - (nuint)(DistanceBitPenalty * FastLog.Log2FloorNonZero(backwardReferenceOffset));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint BackwardReferenceScoreUsingLastDistance(nuint copyLength) =>
        (LiteralByteScore * copyLength) + ScoreBase + 15;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint BackwardReferencePenaltyUsingLastDistance(nuint distanceShortCode) =>
        39 + ((nuint)(0x1CA10 >> (int)(distanceShortCode & 0xE)) & 0xE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash14(ReadOnlySpan<byte> data) => (BinaryPrimitives.ReadUInt32LittleEndian(data) * HashMul32) >> (32 - 14);

    /// <summary>brotli's <c>PrepareDistanceCache</c>: the last distance and the one before, plus or minus 1 to 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrepareDistanceCache(Span<int> distanceCache, int numDistances)
    {
        distanceCache = distanceCache.Slice(0, 16);
        if (numDistances > 4)
        {
            int lastDistance = distanceCache[0];
            distanceCache[4] = lastDistance - 1;
            distanceCache[5] = lastDistance + 1;
            distanceCache[6] = lastDistance - 2;
            distanceCache[7] = lastDistance + 2;
            distanceCache[8] = lastDistance - 3;
            distanceCache[9] = lastDistance + 3;
            if (numDistances > 10)
            {
                int nextLastDistance = distanceCache[1];
                distanceCache[10] = nextLastDistance - 1;
                distanceCache[11] = nextLastDistance + 1;
                distanceCache[12] = nextLastDistance - 2;
                distanceCache[13] = nextLastDistance + 2;
                distanceCache[14] = nextLastDistance - 3;
                distanceCache[15] = nextLastDistance + 3;
            }
        }
    }

    /// <summary>
    /// brotli's <c>TestStaticDictionaryItem</c>: the word's match, when it scores at least
    /// <paramref name="bestScore"/>. It is out of line, so it takes and returns values: a reference to the
    /// caller's result would keep that result in memory.
    /// </summary>
    private static bool TestStaticDictionaryItem(nuint len, nuint wordIdx, ReadOnlySpan<byte> data, nuint maxLength, nuint maxBackward, nuint maxDistance, nuint bestScore, out HasherSearchResult match)
    {
        match = default;
        nuint offset = (nuint)StaticDictionary.OffsetsByLength[(int)len] + (len * wordIdx);
        if (len > maxLength)
        {
            return false;
        }

        nuint matchLen = FindMatchLength.WithLimit(data, StaticDictionary.Data.Slice((int)offset), len);
        if (matchLen + CutoffTransformsCount <= len || matchLen == 0)
        {
            return false;
        }

        nuint cut = len - matchLen;
        nuint transformId = (cut << 2) + (nuint)((CutoffTransforms >> (int)(cut * 6)) & 0x3F);
        nuint backward = maxBackward + 1 + wordIdx + (transformId << StaticDictionary.SizeBitsByLength[(int)len]);
        if (backward > maxDistance)
        {
            return false;
        }

        nuint score = BackwardReferenceScore(matchLen, backward);
        if (score < bestScore)
        {
            return false;
        }

        match.Len = matchLen;
        match.LenCodeDelta = (int)len - (int)matchLen;
        match.Distance = backward;
        match.Score = score;
        return true;
    }

    /// <summary>
    /// brotli's <c>SearchInStaticDictionary</c>: the words of the slots of the position's hash, while
    /// the lookups match often enough.
    /// </summary>
    public static void SearchInStaticDictionary(ref nuint numLookups, ref nuint numMatches, ReadOnlySpan<byte> data, nuint maxLength, nuint maxBackward, nuint maxDistance, ref HasherSearchResult result, bool shallow)
    {
        if (numMatches < (numLookups >> 7))
        {
            return;
        }

        nuint key = (nuint)Hash14(data) << 1;
        ReadOnlySpan<byte> lengths = DictionaryHash.Lengths;
        ReadOnlySpan<ushort> words = DictionaryHash.Words;
        for (int i = 0; i < (shallow ? 1 : 2); ++i, ++key)
        {
            numLookups++;
            byte length = lengths[(int)key];
            if (length != 0 && TestStaticDictionaryItem(length, words[(int)key], data, maxLength, maxBackward, maxDistance, result.Score, out HasherSearchResult match))
            {
                result = match;
                numMatches++;
            }
        }
    }

    /// <summary>
    /// <see cref="SearchInStaticDictionary"/>'s two lookups, once the caller has checked that they match often
    /// enough and counted them: the matches found. Out of line: inlined into the match search, itself inlined
    /// twice into <see cref="BackwardReferences"/>' loop, it left the JIT too many locals to keep the
    /// candidates' loop in registers. The counters stay with the caller, as values, so that its hasher is not
    /// exposed by a reference to them.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint SearchInStaticDictionaryDeep(ReadOnlySpan<byte> data, nuint maxLength, nuint maxBackward, nuint maxDistance, ref HasherSearchResult result)
    {
        nuint key = (nuint)Hash14(data) << 1;
        ReadOnlySpan<byte> lengths = DictionaryHash.Lengths;
        ReadOnlySpan<ushort> words = DictionaryHash.Words;
        nuint matches = 0;
        for (int i = 0; i < 2; ++i, ++key)
        {
            byte length = lengths[(int)key];
            if (length != 0 && TestStaticDictionaryItem(length, words[(int)key], data, maxLength, maxBackward, maxDistance, result.Score, out HasherSearchResult match))
            {
                result = match;
                matches++;
            }
        }

        return matches;
    }
}

/// <summary>A hasher's arrays: grown when needed, kept from one stream to the next.</summary>
internal sealed class HasherMemory
{
    private readonly Array?[] _arrays = new Array?[5];

    /// <summary>The array <paramref name="index"/>, of at least <paramref name="count"/> elements of <typeparamref name="T"/>.</summary>
    public T[] Get<T>(int index, nuint count)
        where T : unmanaged
    {
        if (_arrays[index] is not T[] array || (nuint)array.Length < count)
        {
            array = new T[checked((int)count)];
            _arrays[index] = array;
        }

        return array;
    }
}
