using System;
using System.Collections.Generic;
using System.Linq;
using ManagedCompressions.Brotli.Internal;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// The decoding tables against the code itself: every symbol of a random complete code, written as its
/// canonical codeword and followed by random bits, decodes to that symbol with its length, at every
/// width of the root table the decoder uses.
/// </summary>
public sealed class HuffmanTests
{
    public static IEnumerable<TheoryDataRow<int, int, int>> Codes()
    {
        foreach (int alphabet in new[] { 2, 3, 26, 64, 256, 520, 704 })
        {
            foreach (int rootBits in new[] { 8, 10 })
            {
                for (int seed = 0; seed < 40; seed++)
                {
                    yield return new(alphabet, rootBits, seed);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void EveryCodewordDecodes(int alphabet, int rootBits, int seed)
    {
        var random = new Random((seed * 7919) + alphabet);
        int[] lengths = RandomCompleteCode(alphabet, random, seed);
        Check(lengths, rootBits, random);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void CodesLongerThanTheirRoot(int rootBits)
    {
        // 320 codes of 9 bits and 384 of 10: no code fits a root of 8 bits.
        int[] lengths = new int[704];
        for (int i = 0; i < 704; i++)
        {
            lengths[i] = i < 320 ? 9 : 10;
        }

        Check(lengths, rootBits, new Random(1));

        // The longest codes there are, 15 bits, under a chain of one code of each length.
        int[] deep = new int[16];
        for (int i = 0; i < 14; i++)
        {
            deep[i] = i + 1;
        }

        deep[14] = 15;
        deep[15] = 15;
        Check(deep, rootBits, new Random(2));
    }

    /// <summary>Lengths of a complete code over <paramref name="alphabet"/> symbols, some unused, none longer than 15.</summary>
    private static int[] RandomCompleteCode(int alphabet, Random random, int seed)
    {
        // Split a random leaf until the code has as many leaves as it is given symbols.
        int symbols = 2 + random.Next(alphabet - 1);
        var leaves = new List<int> { 0 };
        while (leaves.Count < symbols)
        {
            // Some codes favour their deepest leaves, as skewed distributions do.
            int index = seed % 3 == 0 ? leaves.IndexOf(leaves.Max()) : random.Next(leaves.Count);
            if (leaves[index] >= 15)
            {
                index = leaves.IndexOf(leaves.Min());
                if (leaves[index] >= 15)
                {
                    break;
                }
            }

            int depth = leaves[index] + 1;
            leaves[index] = depth;
            leaves.Add(depth);
        }

        int[] lengths = new int[alphabet];
        int[] order = Enumerable.Range(0, alphabet).OrderBy(_ => random.Next()).ToArray();
        for (int i = 0; i < leaves.Count; i++)
        {
            lengths[order[i]] = leaves[i];
        }

        return lengths;
    }

    private static void Check(int[] lengths, int rootBits, Random random)
    {
        int alphabet = lengths.Length;

        // The lists of symbols by length, as the decoder makes them.
        ushort[] lists = new ushort[16 + alphabet];
        ushort[] count = new ushort[16];
        int[] nextSymbol = new int[16];
        for (int i = 0; i < 16; i++)
        {
            nextSymbol[i] = i - 16;
            lists[16 + nextSymbol[i]] = 0xFFFF;
        }

        for (int symbol = 0; symbol < alphabet; symbol++)
        {
            int length = lengths[symbol];
            if (length != 0)
            {
                lists[16 + nextSymbol[length]] = (ushort)symbol;
                nextSymbol[length] = symbol;
                count[length]++;
            }
        }

        // The canonical codewords: by length, then by symbol.
        int[] codewords = new int[alphabet];
        int code = 0;
        for (int length = 1; length <= 15; length++)
        {
            for (int symbol = 0; symbol < alphabet; symbol++)
            {
                if (lengths[symbol] == length)
                {
                    codewords[symbol] = code++;
                }
            }

            code <<= 1;
        }

        uint[] table = new uint[(1 << rootBits) + (alphabet << Math.Max(0, 14 - rootBits)) + 512];
        int size = Huffman.BuildTable(table, rootBits, lists, count);

        Assert.True(size <= table.Length - 512);
        for (int symbol = 0; symbol < alphabet; symbol++)
        {
            int length = lengths[symbol];
            if (length == 0)
            {
                continue;
            }

            // The codeword is read first bit first: reversed into the low bits of the stream.
            ulong bits = 0;
            for (int i = 0; i < length; i++)
            {
                bits |= (ulong)((codewords[symbol] >> (length - 1 - i)) & 1) << i;
            }

            bits |= (ulong)random.NextInt64() << length;
            uint entry = table[(int)(bits & ((1UL << rootBits) - 1))];
            int consumed = (int)(entry & 0xFF);
            if (consumed > rootBits)
            {
                ulong rest = bits >> rootBits;
                entry = table[(int)(entry >> 16) + (int)(rest & ((1UL << (consumed - rootBits)) - 1))];
                consumed = rootBits + (int)(entry & 0xFF);
            }

            Assert.Equal(symbol, (int)(entry >> 16));
            Assert.Equal(length, consumed);
        }
    }
}
