using System.Numerics;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>brotli's <c>FindMatchLengthWithLimit</c> (<c>c/enc/find_match_length.h</c>), 8 bytes at a time.</summary>
internal static unsafe class FindMatchLength
{
    /// <summary>The bytes <paramref name="s1"/> and <paramref name="s2"/> have in common, up to <paramref name="limit"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint WithLimit(byte* s1, byte* s2, nuint limit)
    {
        byte* s1Origin = s1;
        for (; limit >= 8; limit -= 8)
        {
            ulong x = Unsafe.ReadUnaligned<ulong>(s2) ^ Unsafe.ReadUnaligned<ulong>(s1);
            s2 += 8;
            if (x != 0)
            {
                return (nuint)(s1 - s1Origin) + ((nuint)BitOperations.TrailingZeroCount(x) >> 3);
            }

            s1 += 8;
        }

        while (limit != 0 && *s1 == *s2)
        {
            limit--;
            ++s2;
            ++s1;
        }

        return (nuint)(s1 - s1Origin);
    }
}
