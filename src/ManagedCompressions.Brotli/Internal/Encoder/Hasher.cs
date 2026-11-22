using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
internal unsafe interface IHasher
{
    /// <summary>The bytes a hash reads: the search stops that many bytes before the end.</summary>
    static abstract int HashTypeLength { get; }

    /// <summary>The bytes past a position a store reads.</summary>
    static abstract int StoreLookahead { get; }

    /// <summary>Clears the tables, or only the entries of the input's positions when it is small and whole.</summary>
    void Prepare(bool oneShot, nuint inputSize, byte* data);

    void Store(byte* data, nuint mask, nuint ix);

    void StoreRange(byte* data, nuint mask, nuint ixStart, nuint ixEnd);

    /// <summary>Stores the last three positions of the previous block, which needed the bytes of this one.</summary>
    void StitchToPreviousBlock(nuint numBytes, nuint position, byte* ringBuffer, nuint ringBufferMask);

    void PrepareDistanceCache(int* distanceCache);

    void FindLongestMatch(byte* data, nuint ringBufferMask, int* distanceCache, nuint curIx, nuint maxLength, nuint maxBackward, nuint dictionaryDistance, nuint maxDistance, HasherSearchResult* result);
}

/// <summary>The common part of brotli's hashers: scores, hashes, and the static dictionary's lookup.</summary>
internal static unsafe class HasherCommon
{
    public const uint HashMul32 = 0x1E35A7BD;
    public const ulong HashMul64 = 0x1FE35A7BD3579BD3;
    public const int LiteralByteScore = 135;
    public const int DistanceBitPenalty = 30;

    /// <summary>brotli's <c>BROTLI_SCORE_BASE</c>, which depends on the size of <c>size_t</c>.</summary>
    public static nuint ScoreBase
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (nuint)(DistanceBitPenalty * 8 * sizeof(nuint));
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
    public static uint Hash14(byte* data) => (Unsafe.ReadUnaligned<uint>(data) * HashMul32) >> (32 - 14);

    /// <summary>brotli's <c>PrepareDistanceCache</c>: the last distance and the one before, plus or minus 1 to 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrepareDistanceCache(int* distanceCache, int numDistances)
    {
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

    private static byte* DictionaryData => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(StaticDictionary.Data));

    /// <summary>brotli's <c>TestStaticDictionaryItem</c>.</summary>
    private static bool TestStaticDictionaryItem(nuint len, nuint wordIdx, byte* data, nuint maxLength, nuint maxBackward, nuint maxDistance, HasherSearchResult* result)
    {
        nuint offset = (nuint)StaticDictionary.OffsetsByLength[(int)len] + (len * wordIdx);
        if (len > maxLength)
        {
            return false;
        }

        nuint matchLen = FindMatchLength.WithLimit(data, DictionaryData + offset, len);
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
        if (score < result->Score)
        {
            return false;
        }

        result->Len = matchLen;
        result->LenCodeDelta = (int)len - (int)matchLen;
        result->Distance = backward;
        result->Score = score;
        return true;
    }

    /// <summary>
    /// brotli's <c>SearchInStaticDictionary</c>: the words of the slots of the position's hash, while
    /// the lookups match often enough.
    /// </summary>
    public static void SearchInStaticDictionary(ref nuint numLookups, ref nuint numMatches, byte* data, nuint maxLength, nuint maxBackward, nuint maxDistance, HasherSearchResult* result, bool shallow)
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
            if (length != 0 && TestStaticDictionaryItem(length, words[(int)key], data, maxLength, maxBackward, maxDistance, result))
            {
                numMatches++;
            }
        }
    }
}

/// <summary>A hasher's arrays: pinned, grown when needed, kept from one stream to the next.</summary>
internal sealed unsafe class HasherMemory
{
    private readonly byte[][] _arrays = [[], [], [], []];

    /// <summary>The array <paramref name="index"/>, of at least <paramref name="size"/> bytes, aligned to 8.</summary>
    public byte* Get(int index, nuint size)
    {
        if ((nuint)_arrays[index].Length < size)
        {
            _arrays[index] = GC.AllocateUninitializedArray<byte>(checked((int)size), pinned: true);
        }

        return (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_arrays[index]));
    }
}
