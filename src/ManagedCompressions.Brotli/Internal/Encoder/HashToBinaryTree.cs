using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>brotli's <c>BackwardMatch</c>: a distance, and a length with the dictionary word's length code.</summary>
internal struct BackwardMatch
{
    public uint Distance;
    public uint LengthAndCode;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BackwardMatch(nuint dist, nuint len)
    {
        Distance = (uint)dist;
        LengthAndCode = (uint)(len << 5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BackwardMatch(nuint dist, nuint len, nuint lenCode)
    {
        Distance = (uint)dist;
        LengthAndCode = (uint)((len << 5) | (len == lenCode ? 0 : lenCode));
    }

    public readonly nuint Length => LengthAndCode >> 5;

    public readonly nuint LengthCode
    {
        get
        {
            nuint code = LengthAndCode & 31;
            return code != 0 ? code : Length;
        }
    }
}

/// <summary>
/// brotli's <c>HashToBinaryTree</c>, H10 (<c>c/enc/hash_to_binary_tree_inc.h</c>): for each 4-byte hash,
/// a binary tree of the positions sorted by their next 128 bytes, a max-heap by position, which yields
/// every match longer than the last, for qualities 10 and 11.
/// </summary>
internal struct HashToBinaryTree
{
    private const int BucketBits = 17;
    private const int BucketSize = 1 << BucketBits;
    private const int MaxTreeSearchDepth = 64;

    /// <summary>brotli's <c>MAX_TREE_COMP_LENGTH</c>.</summary>
    public const int MaxTreeCompLength = 128;

    /// <summary>brotli's <c>MAX_NUM_MATCHES_H10</c>.</summary>
    public const int MaxNumMatches = 128;

    public const int HashTypeLength = 4;

    public const int StoreLookahead = MaxTreeCompLength;

    private nuint _windowMask;
    private uint[] _buckets;
    private uint _invalidPos;
    private uint[] _forest;

    /// <summary>brotli's <c>Initialize</c>, with the forest of <c>HashMemAllocInBytes</c>.</summary>
    public void Initialize(HasherMemory memory, int lgwin, bool oneShot, nuint inputSize)
    {
        nuint numNodes = (nuint)1 << lgwin;
        if (oneShot && inputSize < numNodes)
        {
            numNodes = inputSize;
        }

        _buckets = memory.Get<uint>(0, BucketSize);
        _forest = memory.Get<uint>(1, Math.Max(2 * numNodes, 2));
        _windowMask = ((nuint)1 << lgwin) - 1;
        _invalidPos = (uint)(0 - _windowMask);
    }

    public void Prepare() => _buckets.AsSpan(0, BucketSize).Fill(_invalidPos);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashBytes(ReadOnlySpan<byte> data, nuint at) => (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)at, 4)) * HasherCommon.HashMul32) >> (32 - BucketBits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly nuint LeftChildIndex(nuint pos) => 2 * (pos & _windowMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly nuint RightChildIndex(nuint pos) => (2 * (pos & _windowMask)) + 1;

    /// <summary>
    /// brotli's <c>StoreAndFindMatches</c>: the position into its bucket's tree, which is re-rooted there,
    /// and the matches found on the way, each longer than the last; with fewer than 128 bytes left, the
    /// tree is searched and kept as it is.
    /// </summary>
    private int StoreAndFindMatches(ReadOnlySpan<byte> data, nuint curIx, nuint ringBufferMask, nuint maxLength, nuint maxBackward, ref nuint bestLen, Span<BackwardMatch> matches, int found)
    {
        nuint curIxMasked = curIx & ringBufferMask;
        nuint maxCompLen = Math.Min(maxLength, MaxTreeCompLength);
        bool shouldRerootTree = maxLength >= MaxTreeCompLength;
        uint key = HashBytes(data, curIxMasked);
        uint[] buckets = _buckets;
        uint[] forest = _forest;
        nuint prevIx = buckets[key];
        nuint nodeLeft = LeftChildIndex(curIx);
        nuint nodeRight = RightChildIndex(curIx);
        nuint bestLenLeft = 0;
        nuint bestLenRight = 0;
        if (shouldRerootTree)
        {
            buckets[key] = (uint)curIx;
        }

        for (nuint depthRemaining = MaxTreeSearchDepth; ; --depthRemaining)
        {
            nuint backward = curIx - prevIx;
            nuint prevIxMasked = prevIx & ringBufferMask;
            if (backward == 0 || backward > maxBackward || depthRemaining == 0)
            {
                if (shouldRerootTree)
                {
                    forest[nodeLeft] = _invalidPos;
                    forest[nodeRight] = _invalidPos;
                }

                break;
            }

            nuint curLen = Math.Min(bestLenLeft, bestLenRight);
            nuint len = curLen + FindMatchLength.WithLimit(data, curIxMasked + curLen, prevIxMasked + curLen, maxLength - curLen);
            if (!matches.IsEmpty && len > bestLen)
            {
                bestLen = len;
                matches[found++] = new BackwardMatch(backward, len);
            }

            if (len >= maxCompLen)
            {
                if (shouldRerootTree)
                {
                    forest[nodeLeft] = forest[LeftChildIndex(prevIx)];
                    forest[nodeRight] = forest[RightChildIndex(prevIx)];
                }

                break;
            }

            if (data[(int)(curIxMasked + len)] > data[(int)(prevIxMasked + len)])
            {
                bestLenLeft = len;
                if (shouldRerootTree)
                {
                    forest[nodeLeft] = (uint)prevIx;
                }

                nodeLeft = RightChildIndex(prevIx);
                prevIx = forest[nodeLeft];
            }
            else
            {
                bestLenRight = len;
                if (shouldRerootTree)
                {
                    forest[nodeRight] = (uint)prevIx;
                }

                nodeRight = LeftChildIndex(prevIx);
                prevIx = forest[nodeRight];
            }
        }

        return found;
    }

    /// <summary>
    /// brotli's <c>FindAllMatches</c>: the matches of increasing lengths at <paramref name="curIx"/>, the
    /// short ones among the last 16 or 64 positions, the tree's, and the static dictionary's.
    /// </summary>
    public nuint FindAllMatches(ReadOnlySpan<byte> data, nuint ringBufferMask, nuint curIx, nuint maxLength, nuint maxBackward, nuint dictionaryDistance, int quality, nuint maxDistance, Span<BackwardMatch> matches)
    {
        int found = 0;
        nuint curIxMasked = curIx & ringBufferMask;
        nuint bestLen = 1;
        nuint shortMatchMaxBackward = quality != 11 ? 16u : 64u;
        nuint stop = curIx - shortMatchMaxBackward;
        Span<uint> dictMatches = stackalloc uint[StaticDictionaryLut.MaxStaticDictionaryMatchLen + 1];
        if (curIx < shortMatchMaxBackward)
        {
            stop = 0;
        }

        for (nuint i = curIx - 1; i > stop && bestLen <= 2; --i)
        {
            nuint prevIx = i;
            nuint backward = curIx - prevIx;
            if (backward > maxBackward)
            {
                break;
            }

            prevIx &= ringBufferMask;
            if (data[(int)curIxMasked] != data[(int)prevIx] || data[(int)curIxMasked + 1] != data[(int)prevIx + 1])
            {
                continue;
            }

            nuint len = FindMatchLength.WithLimit(data, prevIx, curIxMasked, maxLength);
            if (len > bestLen)
            {
                bestLen = len;
                matches[found++] = new BackwardMatch(backward, len);
            }
        }

        if (bestLen < maxLength)
        {
            found = StoreAndFindMatches(data, curIx, ringBufferMask, maxLength, maxBackward, ref bestLen, matches, found);
        }

        for (int i = 0; i <= StaticDictionaryLut.MaxStaticDictionaryMatchLen; ++i)
        {
            dictMatches[i] = StaticDictionaryLut.InvalidMatch;
        }

        {
            nuint minLen = Math.Max(4, bestLen + 1);
            if (StaticDictionaryLut.FindAllStaticDictionaryMatches(data.Slice((int)curIxMasked), minLen, maxLength, dictMatches))
            {
                nuint maxLen = Math.Min(StaticDictionaryLut.MaxStaticDictionaryMatchLen, maxLength);
                for (nuint l = minLen; l <= maxLen; ++l)
                {
                    uint dictId = dictMatches[(int)l];
                    if (dictId < StaticDictionaryLut.InvalidMatch)
                    {
                        nuint distance = dictionaryDistance + (dictId >> 5) + 1;
                        if (distance <= maxDistance)
                        {
                            matches[found++] = new BackwardMatch(distance, l, dictId & 31);
                        }
                    }
                }
            }
        }

        return (nuint)found;
    }

    /// <summary>brotli's <c>Store</c>: the position into the tree; it needs 128 bytes after it.</summary>
    public void Store(ReadOnlySpan<byte> data, nuint mask, nuint ix)
    {
        nuint maxBackward = _windowMask - Prefix.WindowGap + 1;
        nuint unused = 0;
        StoreAndFindMatches(data, ix, mask, MaxTreeCompLength, maxBackward, ref unused, default, 0);
    }

    /// <summary>brotli's <c>StoreRange</c>: every 8th position of a long range, then its last 63.</summary>
    public void StoreRange(ReadOnlySpan<byte> data, nuint mask, nuint ixStart, nuint ixEnd)
    {
        nuint i = ixStart;
        nuint j = ixStart;
        if (ixStart + 63 <= ixEnd)
        {
            i = ixEnd - 63;
        }

        if (ixStart + 512 <= i)
        {
            for (; j < i; j += 8)
            {
                Store(data, mask, j);
            }
        }

        for (; i < ixEnd; ++i)
        {
            Store(data, mask, i);
        }
    }

    /// <summary>brotli's <c>StitchToPreviousBlock</c>: the previous block's last 127 positions, which needed this block's bytes.</summary>
    public void StitchToPreviousBlock(nuint numBytes, nuint position, ReadOnlySpan<byte> ringBuffer, nuint ringBufferMask)
    {
        if (numBytes >= HashTypeLength - 1 && position >= MaxTreeCompLength)
        {
            nuint iStart = position - MaxTreeCompLength + 1;
            nuint iEnd = Math.Min(position, iStart + numBytes);
            for (nuint i = iStart; i < iEnd; ++i)
            {
                nuint maxBackward = _windowMask - Math.Max(Prefix.WindowGap - 1, position - i);
                nuint unused = 0;
                StoreAndFindMatches(ringBuffer, i, ringBufferMask, MaxTreeCompLength, maxBackward, ref unused, default, 0);
            }
        }
    }
}
