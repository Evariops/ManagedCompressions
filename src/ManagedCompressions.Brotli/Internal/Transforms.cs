using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// The word transforms of RFC 7932's static dictionary (appendix B), as brotli applies them
/// (<c>BrotliTransformDictionaryWord</c> in <c>c/common/transform.c</c>).
/// </summary>
internal static unsafe partial class Transforms
{
    /// <summary>The number of transforms of the format.</summary>
    public const int Count = 121;

    /// <summary>The transform that leaves the word as it is, with no prefix nor suffix.</summary>
    public const int Identity = 0;

    /// <summary>
    /// The room a transformed word needs, and more: the longest prefix and suffix (7 and 8 bytes), the
    /// longest word (24), and the two bytes past the word that uppercasing a lead byte may touch.
    /// </summary>
    public const int MaxLength = 64;

    private const int OmitLast9 = 9;
    private const int UppercaseFirst = 10;
    private const int UppercaseAll = 11;
    private const int OmitFirst1 = 12;
    private const int OmitFirst9 = 20;

    /// <summary>
    /// Writes word <paramref name="word"/> of <paramref name="length"/> bytes, transformed by
    /// <paramref name="transform"/>, at <paramref name="destination"/>, which must have room for
    /// <see cref="MaxLength"/> bytes; returns the length of the result. Uppercasing may change up to
    /// two bytes past the result, as brotli's does in its ring buffer's slack.
    /// </summary>
    public static int Apply(byte* destination, byte* word, int length, int transform)
    {
        ref byte prefixSuffix = ref Unsafe.AsRef(in PrefixSuffix[0]);
        ref byte map = ref Unsafe.AsRef(in PrefixSuffixMap[0]);
        ref byte triplet = ref Unsafe.Add(ref Unsafe.AsRef(in Triplets[0]), transform * 3);
        int type = Unsafe.Add(ref triplet, 1);

        int index = 0;
        ref byte prefix = ref Unsafe.Add(ref prefixSuffix, Unsafe.Add(ref map, triplet));
        int prefixLength = prefix;
        for (int i = 1; i <= prefixLength; i++)
        {
            destination[index++] = Unsafe.Add(ref prefix, i);
        }

        if (type <= OmitLast9)
        {
            length -= type;
        }
        else if (type >= OmitFirst1 && type <= OmitFirst9)
        {
            int skip = type - (OmitFirst1 - 1);
            word += skip;
            length -= skip;
        }

        for (int i = 0; i < length; i++)
        {
            destination[index++] = word[i];
        }

        if (type == UppercaseFirst)
        {
            ToUpperCase(destination + index - length);
        }
        else if (type == UppercaseAll)
        {
            byte* uppercase = destination + index - length;
            while (length > 0)
            {
                int step = ToUpperCase(uppercase);
                uppercase += step;
                length -= step;
            }
        }

        ref byte suffix = ref Unsafe.Add(ref prefixSuffix, Unsafe.Add(ref map, Unsafe.Add(ref triplet, 2)));
        int suffixLength = suffix;
        for (int i = 1; i <= suffixLength; i++)
        {
            destination[index++] = Unsafe.Add(ref suffix, i);
        }

        return index;
    }

    /// <summary>Brotli's simplified uppercasing of the character at <paramref name="p"/>; returns its length.</summary>
    private static int ToUpperCase(byte* p)
    {
        if (p[0] < 0xC0)
        {
            if (p[0] >= 'a' && p[0] <= 'z')
            {
                p[0] ^= 32;
            }

            return 1;
        }

        // An overly simplified uppercasing model for UTF-8.
        if (p[0] < 0xE0)
        {
            p[1] ^= 32;
            return 2;
        }

        // An arbitrary transform for three byte characters.
        p[2] ^= 5;
        return 3;
    }
}
