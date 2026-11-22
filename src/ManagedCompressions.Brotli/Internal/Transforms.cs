using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

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


    /// <summary>A transform, unpacked for <see cref="ApplyFast"/>.</summary>
    internal struct Info
    {
        /// <summary>The prefix's bytes, at most 8, then zeros.</summary>
        public ulong Prefix;

        /// <summary>The suffix's bytes, at most 8, then zeros.</summary>
        public ulong Suffix;

        public byte PrefixLength;
        public byte SuffixLength;

        /// <summary>The bytes the transform drops from the word's start, or from its end.</summary>
        public byte OmitFirst;
        public byte OmitLast;

        /// <summary>0, or <see cref="UppercaseFirst"/> or <see cref="UppercaseAll"/>.</summary>
        public byte Uppercase;
    }

    /// <summary>The 121 transforms, unpacked.</summary>
    internal static readonly Info[] Infos = BuildInfos();

    private static Info[] BuildInfos()
    {
        var infos = new Info[Count];
        for (int t = 0; t < Count; t++)
        {
            int type = Triplets[(t * 3) + 1];
            ref Info info = ref infos[t];
            info.Prefix = Affix(Triplets[t * 3], out info.PrefixLength);
            info.Suffix = Affix(Triplets[(t * 3) + 2], out info.SuffixLength);
            if (type <= OmitLast9)
            {
                info.OmitLast = (byte)type;
            }
            else if (type >= OmitFirst1 && type <= OmitFirst9)
            {
                info.OmitFirst = (byte)(type - (OmitFirst1 - 1));
            }
            else if (type is UppercaseFirst or UppercaseAll)
            {
                info.Uppercase = (byte)type;
            }
        }

        return infos;
    }

    private static ulong Affix(int id, out byte length)
    {
        int at = PrefixSuffixMap[id];
        length = PrefixSuffix[at];
        if (length > 8)
        {
            throw new InvalidOperationException("a prefix or suffix of more than 8 bytes");
        }

        ulong value = 0;
        for (int i = 0; i < length; i++)
        {
            value |= (ulong)PrefixSuffix[at + 1 + i] << (8 * i);
        }

        return value;
    }

    /// <summary>
    /// <see cref="Apply"/>, with wide stores: <paramref name="destination"/> must have room for 64
    /// bytes, and <paramref name="word"/> be readable 32 bytes on from its omitted start, which the
    /// zeros past the dictionary guarantee. The bytes past the result are left undefined.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ApplyFast(byte* destination, byte* word, int length, ref Info info)
    {
        Unsafe.WriteUnaligned(destination, info.Prefix);
        byte* body = destination + info.PrefixLength;
        int skip = info.OmitFirst;
        Unsafe.WriteUnaligned(body, Unsafe.ReadUnaligned<Vector128<byte>>(word + skip));
        Unsafe.WriteUnaligned(body + 16, Unsafe.ReadUnaligned<Vector128<byte>>(word + skip + 16));
        int bodyLength = length - skip - info.OmitLast;
        if (bodyLength < 0)
        {
            bodyLength = 0;
        }

        if (info.Uppercase != 0)
        {
            Uppercase(body, bodyLength, info.Uppercase);
        }

        Unsafe.WriteUnaligned(body + bodyLength, info.Suffix);
        return info.PrefixLength + bodyLength + info.SuffixLength;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Uppercase(byte* body, int length, int type)
    {
        if (type == UppercaseFirst)
        {
            ToUpperCase(body);
            return;
        }

        while (length > 0)
        {
            int step = ToUpperCase(body);
            body += step;
            length -= step;
        }
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
