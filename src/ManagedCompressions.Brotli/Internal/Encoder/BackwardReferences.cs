using System;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's <c>BrotliCreateBackwardReferences</c> (<c>c/enc/backward_references_inc.h</c>): the greedy
/// search of qualities 2 to 9, with a lazy look one byte ahead, compiled for each hasher.
/// </summary>
internal static class BackwardReferences
{
    /// <summary>brotli's <c>ComputeDistanceCode</c>: a short code when the distance cache has the distance.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint ComputeDistanceCode(nuint distance, nuint maxDistance, ReadOnlySpan<int> distCache)
    {
        if (distance <= maxDistance)
        {
            nuint distancePlus3 = distance + 3;
            nuint offset0 = distancePlus3 - (nuint)(nint)distCache[0];
            nuint offset1 = distancePlus3 - (nuint)(nint)distCache[1];
            if (distance == (nuint)(nint)distCache[0])
            {
                return 0;
            }

            if (distance == (nuint)(nint)distCache[1])
            {
                return 1;
            }

            if (offset0 < 7)
            {
                return (nuint)((0x9750468 >> (int)(4 * offset0)) & 0xF);
            }

            if (offset1 < 7)
            {
                return (nuint)((0xFDB1ACE >> (int)(4 * offset1)) & 0xF);
            }

            if (distance == (nuint)(nint)distCache[2])
            {
                return 2;
            }

            if (distance == (nuint)(nint)distCache[3])
            {
                return 3;
            }
        }

        return distance + Prefix.NumDistanceShortCodes - 1;
    }

    /// <summary>
    /// The commands of <paramref name="numBytes"/> bytes from <paramref name="position"/>, appended to
    /// <paramref name="commands"/>; the literals past the last copy are left in <paramref name="lastInsertLen"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Create<THasher>(
        ref THasher hasherRef,
        nuint numBytes,
        nuint position,
        ReadOnlySpan<byte> ringBuffer,
        nuint ringBufferMask,
        int quality,
        int lgwin,
        in DistanceParams dist,
        Span<int> distCache,
        ref nuint lastInsertLen,
        Span<Command> commands,
        ref nuint numCommands,
        ref nuint numLiteralsRef)
        where THasher : struct, IHasher
    {
        // The hasher and the count in locals, which the JIT keeps in registers.
        THasher hasher = hasherRef;
        nuint numLiterals = numLiteralsRef;
        nuint maxBackwardLimit = ((nuint)1 << lgwin) - Prefix.WindowGap;
        int numNew = 0;
        nuint insertLength = lastInsertLen;
        nuint posEnd = position + numBytes;
        nuint storeEnd = numBytes >= (nuint)THasher.StoreLookahead ? position + numBytes - (nuint)THasher.StoreLookahead + 1 : position;
        nuint randomHeuristicsWindowSize = quality < 9 ? 64u : 512u;
        nuint applyRandomHeuristics = position + randomHeuristicsWindowSize;
        nuint minScore = HasherCommon.ScoreBase + 100;
        nuint maxDistanceParam = dist.MaxDistance;

        hasher.PrepareDistanceCache(distCache);
        HasherSearchResult sr;
        HasherSearchResult sr2;
        while (position + (nuint)THasher.HashTypeLength < posEnd)
        {
            nuint maxLength = posEnd - position;
            nuint maxDistance = Math.Min(position, maxBackwardLimit);
            nuint dictionaryStart = Math.Min(position, maxBackwardLimit);
            sr.Len = 0;
            sr.LenCodeDelta = 0;
            sr.Distance = 0;
            sr.Score = minScore;
            hasher.FindLongestMatch(ringBuffer, ringBufferMask, distCache, position, maxLength, maxDistance, dictionaryStart, maxDistanceParam, ref sr);
            if (sr.Score > minScore)
            {
                // A match: look for a better one a byte ahead.
                int delayedBackwardReferencesInRow = 0;
                --maxLength;
                for (; ; --maxLength)
                {
                    const nuint costDiffLazy = 175;
                    sr2.Len = quality < 5 ? Math.Min(sr.Len - 1, maxLength) : 0;
                    sr2.LenCodeDelta = 0;
                    sr2.Distance = 0;
                    sr2.Score = minScore;
                    maxDistance = Math.Min(position + 1, maxBackwardLimit);
                    dictionaryStart = Math.Min(position + 1, maxBackwardLimit);
                    hasher.FindLongestMatch(ringBuffer, ringBufferMask, distCache, position + 1, maxLength, maxDistance, dictionaryStart, maxDistanceParam, ref sr2);
                    if (sr2.Score >= sr.Score + costDiffLazy)
                    {
                        // A literal now, and the match from the next byte.
                        ++position;
                        ++insertLength;
                        sr = sr2;
                        if (++delayedBackwardReferencesInRow < 4 && position + (nuint)THasher.HashTypeLength < posEnd)
                        {
                            continue;
                        }
                    }

                    break;
                }

                // The next search is past the copy, after the copy's positions are stored.
                hasher.Prefetch(ringBuffer, ringBufferMask, position + sr.Len);
                applyRandomHeuristics = position + (2 * sr.Len) + randomHeuristicsWindowSize;
                dictionaryStart = Math.Min(position, maxBackwardLimit);
                {
                    nuint distanceCode = ComputeDistanceCode(sr.Distance, dictionaryStart, distCache);
                    if (sr.Distance <= dictionaryStart && distanceCode > 0)
                    {
                        distCache[3] = distCache[2];
                        distCache[2] = distCache[1];
                        distCache[1] = distCache[0];
                        distCache[0] = (int)sr.Distance;
                        hasher.PrepareDistanceCache(distCache);
                    }

                    Commands.Init(ref commands[numNew++], dist, insertLength, sr.Len, sr.LenCodeDelta, distanceCode);
                }

                numLiterals += insertLength;
                insertLength = 0;

                // The copy's positions into the table, but those of runs, which would poison it.
                {
                    nuint rangeStart = position + 2;
                    nuint rangeEnd = Math.Min(position + sr.Len, storeEnd);
                    if (sr.Distance < (sr.Len >> 2))
                    {
                        rangeStart = Math.Min(rangeEnd, Math.Max(rangeStart, position + sr.Len - (sr.Distance << 2)));
                    }

                    hasher.StoreRange(ringBuffer, ringBufferMask, rangeStart, rangeEnd);
                }

                position += sr.Len;
            }
            else
            {
                ++insertLength;
                ++position;

                // Long without a match: look up less often, and store fewer hashes.
                if (position > applyRandomHeuristics)
                {
                    if (position > applyRandomHeuristics + (4 * randomHeuristicsWindowSize))
                    {
                        nuint margin = (nuint)Math.Max(THasher.StoreLookahead - 1, 4);
                        nuint posJump = Math.Min(position + 16, posEnd - margin);
                        for (; position < posJump; position += 4)
                        {
                            hasher.Store(ringBuffer, ringBufferMask, position);
                            insertLength += 4;
                        }
                    }
                    else
                    {
                        nuint margin = (nuint)Math.Max(THasher.StoreLookahead - 1, 2);
                        nuint posJump = Math.Min(position + 8, posEnd - margin);
                        for (; position < posJump; position += 2)
                        {
                            hasher.Store(ringBuffer, ringBufferMask, position);
                            insertLength += 2;
                        }
                    }
                }
            }
        }

        insertLength += posEnd - position;
        lastInsertLen = insertLength;
        numCommands += (nuint)numNew;
        numLiteralsRef = numLiterals;
        hasherRef = hasher;
    }
}
