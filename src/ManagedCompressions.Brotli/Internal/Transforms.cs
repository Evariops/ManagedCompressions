using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// The word transforms of RFC 7932's static dictionary (appendix B), as brotli applies them
/// (<c>BrotliTransformDictionaryWord</c> in <c>c/common/transform.c</c>).
/// </summary>
internal static partial class Transforms
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
    public static int Apply(Span<byte> destination, ReadOnlySpan<byte> word, int length, int transform)
    {
        ReadOnlySpan<byte> prefixSuffix = PrefixSuffix;
        ReadOnlySpan<byte> map = PrefixSuffixMap;
        ReadOnlySpan<byte> triplet = Triplets.Slice(transform * 3, 3);
        int type = triplet[1];

        int index = 0;
        int prefix = map[triplet[0]];
        int prefixLength = prefixSuffix[prefix];
        for (int i = 1; i <= prefixLength; i++)
        {
            destination[index++] = prefixSuffix[prefix + i];
        }

        int start = 0;
        if (type <= OmitLast9)
        {
            length -= type;
        }
        else if (type >= OmitFirst1 && type <= OmitFirst9)
        {
            start = type - (OmitFirst1 - 1);
            length -= start;
        }

        for (int i = 0; i < length; i++)
        {
            destination[index++] = word[start + i];
        }

        if (type == UppercaseFirst)
        {
            ToUpperCase(destination, index - length);
        }
        else if (type == UppercaseAll)
        {
            int at = index - length;
            while (length > 0)
            {
                int step = ToUpperCase(destination, at);
                at += step;
                length -= step;
            }
        }

        int suffix = map[triplet[2]];
        int suffixLength = prefixSuffix[suffix];
        for (int i = 1; i <= suffixLength; i++)
        {
            destination[index++] = prefixSuffix[suffix + i];
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
    public static int ApplyFast(Span<byte> destination, ReadOnlySpan<byte> word, int length, in Info info)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, info.Prefix);
        Span<byte> body = destination.Slice(info.PrefixLength);
        int skip = info.OmitFirst;
        Vector128.Create(word.Slice(skip, 16)).CopyTo(body);
        Vector128.Create(word.Slice(skip + 16, 16)).CopyTo(body.Slice(16));
        int bodyLength = length - skip - info.OmitLast;
        if (bodyLength < 0)
        {
            bodyLength = 0;
        }

        if (info.Uppercase != 0)
        {
            Uppercase(body, bodyLength, info.Uppercase);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(bodyLength), info.Suffix);
        return info.PrefixLength + bodyLength + info.SuffixLength;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Uppercase(Span<byte> body, int length, int type)
    {
        if (type == UppercaseFirst)
        {
            ToUpperCase(body, 0);
            return;
        }

        int at = 0;
        while (length > 0)
        {
            int step = ToUpperCase(body, at);
            at += step;
            length -= step;
        }
    }

    /// <summary>Brotli's simplified uppercasing of the character at <paramref name="at"/> in <paramref name="p"/>; returns its length.</summary>
    private static int ToUpperCase(Span<byte> p, int at)
    {
        if (p[at] < 0xC0)
        {
            if (p[at] >= 'a' && p[at] <= 'z')
            {
                p[at] ^= 32;
            }

            return 1;
        }

        // An overly simplified uppercasing model for UTF-8.
        if (p[at] < 0xE0)
        {
            p[at + 1] ^= 32;
            return 2;
        }

        // An arbitrary transform for three byte characters.
        p[at + 2] ^= 5;
        return 3;
    }
}
