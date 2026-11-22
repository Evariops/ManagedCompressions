using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>brotli's <c>FindMatchLengthWithLimit</c> (<c>c/enc/find_match_length.h</c>), 8 bytes at a time.</summary>
internal static class FindMatchLength
{
    /// <summary>
    /// The bytes <paramref name="data"/> has in common at <paramref name="s1"/> and <paramref name="s2"/>,
    /// up to <paramref name="limit"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint WithLimit(ReadOnlySpan<byte> data, nuint s1, nuint s2, nuint limit) =>
        WithLimit(data.Slice((int)s1), data.Slice((int)s2), limit);

    /// <summary>The bytes <paramref name="s1"/> and <paramref name="s2"/> have in common, up to <paramref name="limit"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint WithLimit(ReadOnlySpan<byte> s1, ReadOnlySpan<byte> s2, nuint limit)
    {
        int n = (int)limit;
        int i = 0;
        for (; n - i >= 8; i += 8)
        {
            ulong x = BinaryPrimitives.ReadUInt64LittleEndian(s2.Slice(i, 8)) ^ BinaryPrimitives.ReadUInt64LittleEndian(s1.Slice(i, 8));
            if (x != 0)
            {
                return (nuint)(i + (BitOperations.TrailingZeroCount(x) >> 3));
            }
        }

        while (i < n && s1[i] == s2[i])
        {
            i++;
        }

        return (nuint)i;
    }
}
