using System;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>The fixed codes and constants of RFC 7932.</summary>
internal static partial class Prefix
{
    /// <summary>Symbols of the literal alphabet.</summary>
    public const int NumLiteralSymbols = 256;

    /// <summary>Symbols of the insert-and-copy alphabet.</summary>
    public const int NumCommandSymbols = 704;

    /// <summary>Symbols of the block length alphabet.</summary>
    public const int NumBlockLengthSymbols = 26;

    /// <summary>Symbols of the code length code.</summary>
    public const int NumCodeLengthCodes = 18;

    /// <summary>The code length that repeats the previous non-zero length.</summary>
    public const int RepeatPreviousCodeLength = 16;

    /// <summary>The code length that repeats zeros.</summary>
    public const int RepeatZeroCodeLength = 17;

    /// <summary>The previous length before any, for <see cref="RepeatPreviousCodeLength"/>.</summary>
    public const int InitialRepeatedCodeLength = 8;

    /// <summary>Block types of a category, at most.</summary>
    public const int MaxBlockTypes = 256;

    /// <summary>Contexts of a literal block type: 64 (6 bits).</summary>
    public const int LiteralContextBits = 6;

    /// <summary>Contexts of a distance block type: 4 (2 bits).</summary>
    public const int DistanceContextBits = 2;

    /// <summary>Distance codes that refer to past distances.</summary>
    public const int NumDistanceShortCodes = 16;

    /// <summary>The extra bits of the longest distance, outside the large window.</summary>
    public const int MaxDistanceBits = 24;

    /// <summary>The largest distance a stream may use, brotli's <c>BROTLI_MAX_ALLOWED_DISTANCE</c>.</summary>
    public const int MaxAllowedDistance = 0x7FFFFFFC;

    /// <summary>The window's distances stop short of its size by this much (RFC 7932, section 9.1).</summary>
    public const int WindowGap = 16;

    /// <summary>The block length of a category with a single block type: longer than any meta-block.</summary>
    public const int BlockSizeCap = 1 << 24;

    /// <summary>The size of the distance alphabet for <paramref name="npostfix"/> and <paramref name="ndirect"/>.</summary>
    public static int DistanceAlphabetSize(int npostfix, int ndirect, int maxBits) =>
        NumDistanceShortCodes + ndirect + (maxBits << (npostfix + 1));

    /// <summary>The order in which the code length code's lengths are given.</summary>
    public static ReadOnlySpan<byte> CodeLengthCodeOrder => [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    /// <summary>The static code of the code length code's lengths: the length of the code read from 4 bits.</summary>
    public static ReadOnlySpan<byte> CodeLengthPrefixLength => [2, 2, 2, 3, 2, 2, 2, 4, 2, 2, 2, 3, 2, 2, 2, 4];

    /// <summary>The static code of the code length code's lengths: the value read from 4 bits.</summary>
    public static ReadOnlySpan<byte> CodeLengthPrefixValue => [0, 4, 3, 2, 0, 4, 3, 1, 0, 4, 3, 2, 0, 4, 3, 5];

    /// <summary>The block length codes: the offset of each of the 26.</summary>
    public static ReadOnlySpan<int> BlockLengthOffset =>
    [
        1, 5, 9, 13, 17, 25, 33, 41, 49, 65, 81, 97, 113, 145, 177, 209, 241, 305, 369, 497, 753, 1265, 2289,
        4337, 8433, 16625,
    ];

    /// <summary>The block length codes: the extra bits of each of the 26.</summary>
    public static ReadOnlySpan<byte> BlockLengthBits =>
    [
        2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 7, 8, 9, 10, 11, 12, 13, 24,
    ];
}
