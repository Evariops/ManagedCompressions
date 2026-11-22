using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's <c>BrotliFindAllStaticDictionaryMatches</c> (<c>c/enc/static_dict.c</c>): every static
/// dictionary word, under every transform the lookup table knows, that the data starts with; qualities
/// 10 and 11 weigh them all.
/// </summary>
internal static partial class StaticDictionaryLut
{
    /// <summary>brotli's <c>kInvalidMatch</c>: no match of that length.</summary>
    public const uint InvalidMatch = 0xFFFFFFF;

    /// <summary>brotli's <c>BROTLI_MAX_STATIC_DICTIONARY_MATCH_LEN</c>.</summary>
    public const int MaxStaticDictionaryMatchLen = 37;

    private const int DictNumBits = 15;
    private const uint DictHashMul32 = 0x1E35A7BD;
    private const ulong CutoffTransforms = 0x071B520ADA2D3200;
    private const int UppercaseFirst = 10;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(ReadOnlySpan<byte> data) => (BinaryPrimitives.ReadUInt32LittleEndian(data) * DictHashMul32) >> (32 - DictNumBits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SizeBits(nuint length) => StaticDictionary.SizeBitsByLength[(int)length];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Transform(uint word) => (word >> 8) & 0xFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> Word(nuint length, nuint id) => StaticDictionary.Data.Slice((int)((nuint)StaticDictionary.OffsetsByLength[(int)length] + (length * id)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddMatch(nuint distance, nuint len, nuint lenCode, Span<uint> matches)
    {
        uint match = (uint)((distance << 5) + lenCode);
        matches[(int)len] = Math.Min(matches[(int)len], match);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint DictMatchLength(ReadOnlySpan<byte> data, nuint id, nuint len, nuint maxLen) =>
        FindMatchLength.WithLimit(Word(len, id), data, Math.Min(len, maxLen));

    /// <summary>brotli's <c>IsMatch</c>: the word, as is, with its first letter or all its letters upper case.</summary>
    private static bool IsMatch(nuint len, nuint idx, uint transform, ReadOnlySpan<byte> data, nuint maxLength)
    {
        if (len > maxLength)
        {
            return false;
        }

        ReadOnlySpan<byte> dict = Word(len, idx);
        if (transform == 0)
        {
            return FindMatchLength.WithLimit(dict, data, len) == len;
        }

        if (transform == 10)
        {
            return dict[0] >= 'a' && dict[0] <= 'z' && (dict[0] ^ 32) == data[0] && FindMatchLength.WithLimit(dict.Slice(1), data.Slice(1), len - 1) == len - 1;
        }

        for (nuint i = 0; i < len; ++i)
        {
            if (dict[(int)i] >= 'a' && dict[(int)i] <= 'z')
            {
                if ((dict[(int)i] ^ 32) != data[(int)(i)])
                {
                    return false;
                }
            }
            else if (dict[(int)i] != data[(int)(i)])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The matches of <paramref name="minLength"/> to <paramref name="maxLength"/> bytes into
    /// <paramref name="matches"/>, indexed by length: the smallest of distance &lt;&lt; 5 plus the word's length.
    /// </summary>
    public static bool FindAllStaticDictionaryMatches(ReadOnlySpan<byte> data, nuint minLength, nuint maxLength, Span<uint> matches)
    {
        bool hasFoundMatch = false;
        {
            nuint offset = Buckets[(int)Hash(data)];
            bool end = offset == 0;
            while (!end)
            {
                uint w = Words[(int)offset++];
                nuint l = w & 0x1F;
                nuint n = (nuint)1 << SizeBits(l);
                nuint id = w >> 16;
                end = (w & 0x80) != 0;
                if (Transform(w) == 0)
                {
                    nuint matchlen =
                        DictMatchLength(data, id, l, maxLength);
                    ReadOnlySpan<byte> s;
                    nuint minlen;
                    nuint maxlen;
                    nuint len;
                    /* Transform "" + BROTLI_TRANSFORM_IDENTITY + "" */
                    if (matchlen == l)
                    {
                        AddMatch(id, l, l, matches);
                        hasFoundMatch = true;
                    }
                    /* Transforms "" + BROTLI_TRANSFORM_OMIT_LAST_1 + "" and
                                  "" + BROTLI_TRANSFORM_OMIT_LAST_1 + "ing " */
                    if (matchlen >= l - 1)
                    {
                        AddMatch(id + 12 * n, l - 1, l, matches);
                        if (l + 2 < maxLength &&
                            data[(int)(l - 1)] == 'i' && data[(int)(l)] == 'n' && data[(int)(l + 1)] == 'g' &&
                            data[(int)(l + 2)] == ' ')
                        {
                            AddMatch(id + 49 * n, l + 3, l, matches);
                        }
                        hasFoundMatch = true;
                    }
                    /* Transform "" + BROTLI_TRANSFORM_OMIT_LAST_# + "" (# = 2 .. 9) */
                    minlen = minLength;
                    if (l > 9) minlen = Math.Max(minlen, l - 9);
                    maxlen = Math.Min(matchlen, l - 2);
                    for (len = minlen; len <= maxlen; ++len)
                    {
                        nuint cut = l - len;
                        nuint transformId = (cut << 2) +
                            (nuint)((CutoffTransforms >> (int)(cut * 6)) & 0x3F);
                        AddMatch(id + transformId * n, len, l, matches);
                        hasFoundMatch = true;
                    }
                    if (matchlen < l || l + 6 >= maxLength)
                    {
                        continue;
                    }
                    s = data.Slice((int)(l));
                    /* Transforms "" + BROTLI_TRANSFORM_IDENTITY + <suffix> */
                    if (s[0] == ' ')
                    {
                        AddMatch(id + n, l + 1, l, matches);
                        if (s[1] == 'a')
                        {
                            if (s[2] == ' ')
                            {
                                AddMatch(id + 28 * n, l + 3, l, matches);
                            }
                            else if (s[2] == 's')
                            {
                                if (s[3] == ' ') AddMatch(id + 46 * n, l + 4, l, matches);
                            }
                            else if (s[2] == 't')
                            {
                                if (s[3] == ' ') AddMatch(id + 60 * n, l + 4, l, matches);
                            }
                            else if (s[2] == 'n')
                            {
                                if (s[3] == 'd' && s[4] == ' ')
                                {
                                    AddMatch(id + 10 * n, l + 5, l, matches);
                                }
                            }
                        }
                        else if (s[1] == 'b')
                        {
                            if (s[2] == 'y' && s[3] == ' ')
                            {
                                AddMatch(id + 38 * n, l + 4, l, matches);
                            }
                        }
                        else if (s[1] == 'i')
                        {
                            if (s[2] == 'n')
                            {
                                if (s[3] == ' ') AddMatch(id + 16 * n, l + 4, l, matches);
                            }
                            else if (s[2] == 's')
                            {
                                if (s[3] == ' ') AddMatch(id + 47 * n, l + 4, l, matches);
                            }
                        }
                        else if (s[1] == 'f')
                        {
                            if (s[2] == 'o')
                            {
                                if (s[3] == 'r' && s[4] == ' ')
                                {
                                    AddMatch(id + 25 * n, l + 5, l, matches);
                                }
                            }
                            else if (s[2] == 'r')
                            {
                                if (s[3] == 'o' && s[4] == 'm' && s[5] == ' ')
                                {
                                    AddMatch(id + 37 * n, l + 6, l, matches);
                                }
                            }
                        }
                        else if (s[1] == 'o')
                        {
                            if (s[2] == 'f')
                            {
                                if (s[3] == ' ') AddMatch(id + 8 * n, l + 4, l, matches);
                            }
                            else if (s[2] == 'n')
                            {
                                if (s[3] == ' ') AddMatch(id + 45 * n, l + 4, l, matches);
                            }
                        }
                        else if (s[1] == 'n')
                        {
                            if (s[2] == 'o' && s[3] == 't' && s[4] == ' ')
                            {
                                AddMatch(id + 80 * n, l + 5, l, matches);
                            }
                        }
                        else if (s[1] == 't')
                        {
                            if (s[2] == 'h')
                            {
                                if (s[3] == 'e')
                                {
                                    if (s[4] == ' ') AddMatch(id + 5 * n, l + 5, l, matches);
                                }
                                else if (s[3] == 'a')
                                {
                                    if (s[4] == 't' && s[5] == ' ')
                                    {
                                        AddMatch(id + 29 * n, l + 6, l, matches);
                                    }
                                }
                            }
                            else if (s[2] == 'o')
                            {
                                if (s[3] == ' ') AddMatch(id + 17 * n, l + 4, l, matches);
                            }
                        }
                        else if (s[1] == 'w')
                        {
                            if (s[2] == 'i' && s[3] == 't' && s[4] == 'h' && s[5] == ' ')
                            {
                                AddMatch(id + 35 * n, l + 6, l, matches);
                            }
                        }
                    }
                    else if (s[0] == '"')
                    {
                        AddMatch(id + 19 * n, l + 1, l, matches);
                        if (s[1] == '>')
                        {
                            AddMatch(id + 21 * n, l + 2, l, matches);
                        }
                    }
                    else if (s[0] == '.')
                    {
                        AddMatch(id + 20 * n, l + 1, l, matches);
                        if (s[1] == ' ')
                        {
                            AddMatch(id + 31 * n, l + 2, l, matches);
                            if (s[2] == 'T' && s[3] == 'h')
                            {
                                if (s[4] == 'e')
                                {
                                    if (s[5] == ' ') AddMatch(id + 43 * n, l + 6, l, matches);
                                }
                                else if (s[4] == 'i')
                                {
                                    if (s[5] == 's' && s[6] == ' ')
                                    {
                                        AddMatch(id + 75 * n, l + 7, l, matches);
                                    }
                                }
                            }
                        }
                    }
                    else if (s[0] == ',')
                    {
                        AddMatch(id + 76 * n, l + 1, l, matches);
                        if (s[1] == ' ')
                        {
                            AddMatch(id + 14 * n, l + 2, l, matches);
                        }
                    }
                    else if (s[0] == '\n')
                    {
                        AddMatch(id + 22 * n, l + 1, l, matches);
                        if (s[1] == '\t')
                        {
                            AddMatch(id + 50 * n, l + 2, l, matches);
                        }
                    }
                    else if (s[0] == ']')
                    {
                        AddMatch(id + 24 * n, l + 1, l, matches);
                    }
                    else if (s[0] == '\'')
                    {
                        AddMatch(id + 36 * n, l + 1, l, matches);
                    }
                    else if (s[0] == ':')
                    {
                        AddMatch(id + 51 * n, l + 1, l, matches);
                    }
                    else if (s[0] == '(')
                    {
                        AddMatch(id + 57 * n, l + 1, l, matches);
                    }
                    else if (s[0] == '=')
                    {
                        if (s[1] == '"')
                        {
                            AddMatch(id + 70 * n, l + 2, l, matches);
                        }
                        else if (s[1] == '\'')
                        {
                            AddMatch(id + 86 * n, l + 2, l, matches);
                        }
                    }
                    else if (s[0] == 'a')
                    {
                        if (s[1] == 'l' && s[2] == ' ')
                        {
                            AddMatch(id + 84 * n, l + 3, l, matches);
                        }
                    }
                    else if (s[0] == 'e')
                    {
                        if (s[1] == 'd')
                        {
                            if (s[2] == ' ') AddMatch(id + 53 * n, l + 3, l, matches);
                        }
                        else if (s[1] == 'r')
                        {
                            if (s[2] == ' ') AddMatch(id + 82 * n, l + 3, l, matches);
                        }
                        else if (s[1] == 's')
                        {
                            if (s[2] == 't' && s[3] == ' ')
                            {
                                AddMatch(id + 95 * n, l + 4, l, matches);
                            }
                        }
                    }
                    else if (s[0] == 'f')
                    {
                        if (s[1] == 'u' && s[2] == 'l' && s[3] == ' ')
                        {
                            AddMatch(id + 90 * n, l + 4, l, matches);
                        }
                    }
                    else if (s[0] == 'i')
                    {
                        if (s[1] == 'v')
                        {
                            if (s[2] == 'e' && s[3] == ' ')
                            {
                                AddMatch(id + 92 * n, l + 4, l, matches);
                            }
                        }
                        else if (s[1] == 'z')
                        {
                            if (s[2] == 'e' && s[3] == ' ')
                            {
                                AddMatch(id + 100 * n, l + 4, l, matches);
                            }
                        }
                    }
                    else if (s[0] == 'l')
                    {
                        if (s[1] == 'e')
                        {
                            if (s[2] == 's' && s[3] == 's' && s[4] == ' ')
                            {
                                AddMatch(id + 93 * n, l + 5, l, matches);
                            }
                        }
                        else if (s[1] == 'y')
                        {
                            if (s[2] == ' ') AddMatch(id + 61 * n, l + 3, l, matches);
                        }
                    }
                    else if (s[0] == 'o')
                    {
                        if (s[1] == 'u' && s[2] == 's' && s[3] == ' ')
                        {
                            AddMatch(id + 106 * n, l + 4, l, matches);
                        }
                    }
                }
                else
                {
                    /* Set isAllCaps=0 for UppercaseFirst and
                           isAllCaps=1 otherwise (BROTLI_TRANSFORM_UPPERCASE_ALL)
                       transform. */
                    bool isAllCaps =
                        (Transform(w) != UppercaseFirst);
                    ReadOnlySpan<byte> s;
                    if (!IsMatch(l, id, Transform(w), data, maxLength))
                    {
                        continue;
                    }
                    /* Transform "" + kUppercase{First,All} + "" */
                    AddMatch(id + (nuint)(isAllCaps ? 44 : 9) * n, l, l, matches);
                    hasFoundMatch = true;
                    if (l + 1 >= maxLength)
                    {
                        continue;
                    }
                    /* Transforms "" + kUppercase{First,All} + <suffix> */
                    s = data.Slice((int)(l));
                    if (s[0] == ' ')
                    {
                        AddMatch(id + (nuint)(isAllCaps ? 68 : 4) * n, l + 1, l, matches);
                    }
                    else if (s[0] == '"')
                    {
                        AddMatch(id + (nuint)(isAllCaps ? 87 : 66) * n, l + 1, l, matches);
                        if (s[1] == '>')
                        {
                            AddMatch(id + (nuint)(isAllCaps ? 97 : 69) * n, l + 2, l, matches);
                        }
                    }
                    else if (s[0] == '.')
                    {
                        AddMatch(id + (nuint)(isAllCaps ? 101 : 79) * n, l + 1, l, matches);
                        if (s[1] == ' ')
                        {
                            AddMatch(id + (nuint)(isAllCaps ? 114 : 88) * n, l + 2, l, matches);
                        }
                    }
                    else if (s[0] == ',')
                    {
                        AddMatch(id + (nuint)(isAllCaps ? 112 : 99) * n, l + 1, l, matches);
                        if (s[1] == ' ')
                        {
                            AddMatch(id + (nuint)(isAllCaps ? 107 : 58) * n, l + 2, l, matches);
                        }
                    }
                    else if (s[0] == '\'')
                    {
                        AddMatch(id + (nuint)(isAllCaps ? 94 : 74) * n, l + 1, l, matches);
                    }
                    else if (s[0] == '(')
                    {
                        AddMatch(id + (nuint)(isAllCaps ? 113 : 78) * n, l + 1, l, matches);
                    }
                    else if (s[0] == '=')
                    {
                        if (s[1] == '"')
                        {
                            AddMatch(id + (nuint)(isAllCaps ? 105 : 104) * n, l + 2, l, matches);
                        }
                        else if (s[1] == '\'')
                        {
                            AddMatch(id + (nuint)(isAllCaps ? 116 : 108) * n, l + 2, l, matches);
                        }
                    }
                }
            }
        }
        /* Transforms with prefixes " " and "." */
        if (maxLength >= 5 && (data[0] == ' ' || data[0] == '.'))
        {
            bool isSpace = (data[0] == ' ');
            nuint offset = Buckets[(int)Hash(data.Slice(1))];
            bool end = offset == 0;
            while (!end)
            {
                uint w = Words[(int)offset++];
                nuint l = w & 0x1F;
                nuint n = (nuint)1 << SizeBits(l);
                nuint id = w >> 16;
                end = (w & 0x80) != 0;
                if (Transform(w) == 0)
                {
                    ReadOnlySpan<byte> s;
                    if (!IsMatch(l, id, Transform(w), data.Slice(1), maxLength - 1))
                    {
                        continue;
                    }
                    /* Transforms " " + BROTLI_TRANSFORM_IDENTITY + "" and
                                  "." + BROTLI_TRANSFORM_IDENTITY + "" */
                    AddMatch(id + (nuint)(isSpace ? 6 : 32) * n, l + 1, l, matches);
                    hasFoundMatch = true;
                    if (l + 2 >= maxLength)
                    {
                        continue;
                    }
                    /* Transforms " " + BROTLI_TRANSFORM_IDENTITY + <suffix> and
                                  "." + BROTLI_TRANSFORM_IDENTITY + <suffix>
                    */
                    s = data.Slice((int)(l + 1));
                    if (s[0] == ' ')
                    {
                        AddMatch(id + (nuint)(isSpace ? 2 : 77) * n, l + 2, l, matches);
                    }
                    else if (s[0] == '(')
                    {
                        AddMatch(id + (nuint)(isSpace ? 89 : 67) * n, l + 2, l, matches);
                    }
                    else if (isSpace)
                    {
                        if (s[0] == ',')
                        {
                            AddMatch(id + 103 * n, l + 2, l, matches);
                            if (s[1] == ' ')
                            {
                                AddMatch(id + 33 * n, l + 3, l, matches);
                            }
                        }
                        else if (s[0] == '.')
                        {
                            AddMatch(id + 71 * n, l + 2, l, matches);
                            if (s[1] == ' ')
                            {
                                AddMatch(id + 52 * n, l + 3, l, matches);
                            }
                        }
                        else if (s[0] == '=')
                        {
                            if (s[1] == '"')
                            {
                                AddMatch(id + 81 * n, l + 3, l, matches);
                            }
                            else if (s[1] == '\'')
                            {
                                AddMatch(id + 98 * n, l + 3, l, matches);
                            }
                        }
                    }
                }
                else if (isSpace)
                {
                    /* Set isAllCaps=0 for UppercaseFirst and
                           isAllCaps=1 otherwise (BROTLI_TRANSFORM_UPPERCASE_ALL)
                       transform. */
                    bool isAllCaps =
                        (Transform(w) != UppercaseFirst);
                    ReadOnlySpan<byte> s;
                    if (!IsMatch(l, id, Transform(w), data.Slice(1), maxLength - 1))
                    {
                        continue;
                    }
                    /* Transforms " " + kUppercase{First,All} + "" */
                    AddMatch(id + (nuint)(isAllCaps ? 85 : 30) * n, l + 1, l, matches);
                    hasFoundMatch = true;
                    if (l + 2 >= maxLength)
                    {
                        continue;
                    }
                    /* Transforms " " + kUppercase{First,All} + <suffix> */
                    s = data.Slice((int)(l + 1));
                    if (s[0] == ' ')
                    {
                        AddMatch(id + (nuint)(isAllCaps ? 83 : 15) * n, l + 2, l, matches);
                    }
                    else if (s[0] == ',')
                    {
                        if (!isAllCaps)
                        {
                            AddMatch(id + 109 * n, l + 2, l, matches);
                        }
                        if (s[1] == ' ')
                        {
                            AddMatch(id + (nuint)(isAllCaps ? 111 : 65) * n, l + 3, l, matches);
                        }
                    }
                    else if (s[0] == '.')
                    {
                        AddMatch(id + (nuint)(isAllCaps ? 115 : 96) * n, l + 2, l, matches);
                        if (s[1] == ' ')
                        {
                            AddMatch(id + (nuint)(isAllCaps ? 117 : 91) * n, l + 3, l, matches);
                        }
                    }
                    else if (s[0] == '=')
                    {
                        if (s[1] == '"')
                        {
                            AddMatch(id + (nuint)(isAllCaps ? 110 : 118) * n, l + 3, l, matches);
                        }
                        else if (s[1] == '\'')
                        {
                            AddMatch(id + (nuint)(isAllCaps ? 119 : 120) * n, l + 3, l, matches);
                        }
                    }
                }
            }
        }
        if (maxLength >= 6)
        {
            /* Transforms with prefixes "e ", "s ", ", " and "\xC2\xA0" */
            if ((data[1] == ' ' &&
                 (data[0] == 'e' || data[0] == 's' || data[0] == ',')) ||
                (data[0] == 0xC2 && data[1] == 0xA0))
            {
                nuint offset = Buckets[(int)Hash(data.Slice(2))];
                bool end = offset == 0;
                while (!end)
                {
                    uint w = Words[(int)offset++];
                    nuint l = w & 0x1F;
                    nuint n = (nuint)1 << SizeBits(l);
                    nuint id = w >> 16;
                    end = (w & 0x80) != 0;
                    if (Transform(w) == 0 &&
                        IsMatch(l, id, Transform(w), data.Slice(2), maxLength - 2))
                    {
                        if (data[0] == 0xC2)
                        {
                            AddMatch(id + 102 * n, l + 2, l, matches);
                            hasFoundMatch = true;
                        }
                        else if (l + 2 < maxLength && data[(int)(l + 2)] == ' ')
                        {
                            nuint t = (nuint)(data[0] == 'e' ? 18 : (data[0] == 's' ? 7 : 13));
                            AddMatch(id + t * n, l + 3, l, matches);
                            hasFoundMatch = true;
                        }
                    }
                }
            }
        }
        if (maxLength >= 9)
        {
            /* Transforms with prefixes " the " and ".com/" */
            if ((data[0] == ' ' && data[1] == 't' && data[2] == 'h' &&
                 data[3] == 'e' && data[4] == ' ') ||
                (data[0] == '.' && data[1] == 'c' && data[2] == 'o' &&
                 data[3] == 'm' && data[4] == '/'))
            {
                nuint offset = Buckets[(int)Hash(data.Slice(5))];
                bool end = offset == 0;
                while (!end)
                {
                    uint w = Words[(int)offset++];
                    nuint l = w & 0x1F;
                    nuint n = (nuint)1 << SizeBits(l);
                    nuint id = w >> 16;
                    end = (w & 0x80) != 0;
                    if (Transform(w) == 0 &&
                        IsMatch(l, id, Transform(w), data.Slice(5), maxLength - 5))
                    {
                        AddMatch(id + (nuint)(data[0] == ' ' ? 41 : 72) * n, l + 5, l, matches);
                        hasFoundMatch = true;
                        if (l + 5 < maxLength)
                        {
                            ReadOnlySpan<byte> s = data.Slice((int)(l + 5));
                            if (data[0] == ' ')
                            {
                                if (l + 8 < maxLength &&
                                    s[0] == ' ' && s[1] == 'o' && s[2] == 'f' && s[3] == ' ')
                                {
                                    AddMatch(id + 62 * n, l + 9, l, matches);
                                    if (l + 12 < maxLength &&
                                        s[4] == 't' && s[5] == 'h' && s[6] == 'e' && s[7] == ' ')
                                    {
                                        AddMatch(id + 73 * n, l + 13, l, matches);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        return hasFoundMatch;
    }
}
