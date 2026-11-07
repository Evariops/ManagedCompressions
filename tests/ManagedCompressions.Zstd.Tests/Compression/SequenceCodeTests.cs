using System;
using ManagedCompressions.Zstd.Internal;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Compression;

/// <summary>
/// The codes of a block's sequences, computed in one pass once the block is found (x64): the vector
/// loop must give the records and counts the one-at-a-time path gives, sequence for sequence.
/// </summary>
public sealed unsafe class SequenceCodeTests
{
    public static TheoryData<int> Seeds() => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Computed_codes_match_one_at_a_time(int seed)
    {
        Assert.SkipUnless(SequenceStore.DefersCodes, "the codes are computed as the sequences are stored");
        var random = new Random(seed);
        var store = new SequenceStore();
        store.EnsureCapacity(FrameFormat.MaxBlockSize);
        uint* expectedCounts = stackalloc uint[SequenceStore.AllCodes];
        for (int round = 0; round < 50; round++)
        {
            // From none to many, so that the vector loop's tail and its threshold are crossed.
            int count = round < 40 ? random.Next(0, 300) : random.Next(1000, 20_000);
            store.Reset();
            var lengths = new (nuint Literals, uint OffBase, nuint Match)[count];
            for (int i = 0; i < count; i++)
            {
                nuint literals = Pick(random, 1 << 17);
                nuint match = 3 + Pick(random, (1 << 17) - 3);
                // A repeat code, or an offset of up to 31 bits, every scale represented.
                uint offBase = random.Next(4) == 0 ? (uint)random.Next(1, 4) : 3 + 1 + (uint)random.NextInt64(0, (1L << random.Next(3, 32)) - 4);
                lengths[i] = (literals, offBase, match);
                SequenceRecord* sequence = store.Sequences;
                SequenceStore.StoreOnly(ref sequence, store.Counts, literals, offBase, match);
                store.Sequences = sequence;
            }

            store.ComputeCodes();

            var expected = new SequenceRecord[count + 1];
            new Span<uint>(expectedCounts, SequenceStore.AllCodes).Clear();
            fixed (SequenceRecord* first = expected)
            {
                SequenceRecord* at = first;
                foreach ((nuint literals, uint offBase, nuint match) in lengths)
                {
                    SequenceStore.StoreCoded(ref at, expectedCounts, literals, offBase, match);
                }
            }

            for (int i = 0; i < count; i++)
            {
                SequenceRecord actual = store.SequencesStart[i];
                Assert.True(
                    actual.Extras == expected[i].Extras && actual.Codes == expected[i].Codes && actual.OffBase == expected[i].OffBase,
                    $"seed {seed}, round {round}, sequence {i} of {count}: {lengths[i]}");
            }

            for (int c = 0; c < SequenceStore.AllCodes; c++)
            {
                Assert.Equal(expectedCounts[c], store.Counts[c]);
            }
        }
    }

    /// <summary>A length below <paramref name="limit"/>, small ones the most likely, every scale represented.</summary>
    private static nuint Pick(Random random, int limit)
    {
        int bits = random.Next(0, 18);
        return (nuint)random.Next(0, Math.Min(limit, 1 << bits));
    }
}
