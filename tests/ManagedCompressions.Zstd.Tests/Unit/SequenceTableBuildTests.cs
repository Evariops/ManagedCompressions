using System;
using ManagedCompressions.Zstd.Internal;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Unit;

/// <summary>
/// The sequence decoding tables against libzstd's <c>ZSTD_buildFSETable</c> written plainly: its slow
/// spread, symbol after symbol along the step, skipping the cells of the symbols below one unit, then
/// each state in table order. Random distributions of every code and table size, with and without
/// symbols below one unit, the case the fast builder turns into holes in its spread.
/// </summary>
public sealed class SequenceTableBuildTests
{
    public static TheoryData<int> Seeds() => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Tables_match_the_plain_algorithm(int seed)
    {
        var random = new Random(seed);
        foreach (SequenceCode code in new[] { SequenceCode.LiteralLength, SequenceCode.Offset, SequenceCode.MatchLength })
        {
            for (int round = 0; round < 60; round++)
            {
                int tableLog = random.Next(5, SequenceCodes.MaxLog(code) + 1);
                short[] norm = RandomDistribution(random, SequenceCodes.MaxSymbol(code) + 1, tableLog);
                var table = new SeqTable(code, norm, tableLog);
                var expected = Reference(code, norm, tableLog);
                int slot = SequenceTableSet.Slot(code);
                for (int u = 0; u < 1 << tableLog; u++)
                {
                    SeqSymbol actual = table.Entries[2 * u];
                    Assert.Equal(actual, table.Entries[(2 * u) + 1]);
                    var fields = ((actual.NextState - slot) / 2, actual.NbAdditionalBits(code), actual.NbBits(code), actual.BaseValue(code));
                    Assert.True(fields == expected[u], $"{code}, log {tableLog}, round {round}, state {u}: expected {expected[u]}, got {fields}");
                }
            }
        }
    }

    /// <summary>
    /// A normalized distribution of up to <paramref name="symbols"/> symbols summing to 2^tableLog:
    /// some symbols absent, some below one unit (-1, a cell each), the rest positive.
    /// </summary>
    private static short[] RandomDistribution(Random random, int symbols, int tableLog)
    {
        int count = random.Next(2, symbols + 1);
        var norm = new short[count];
        int remaining = 1 << tableLog;
        bool lowProbability = random.Next(3) != 0;
        for (int s = 0; s < count && remaining > 1; s++)
        {
            int kind = random.Next(10);
            if (kind < 2)
            {
                continue;
            }

            if (lowProbability && kind < 5)
            {
                norm[s] = -1;
                remaining--;
                continue;
            }

            int take = Math.Max(1, Math.Min(remaining - 1, random.Next(1, Math.Max(2, remaining / 3))));
            norm[s] = (short)take;
            remaining -= take;
        }

        // The rest to the last symbol present, positive.
        int last = count - 1;
        norm[last] = (short)(norm[last] > 0 ? norm[last] + remaining : remaining + (norm[last] < 0 ? 1 : 0));
        int total = 0;
        foreach (short n in norm)
        {
            total += Math.Abs(n);
        }

        Assert.Equal(1 << tableLog, total);
        return norm;
    }

    /// <summary>libzstd's <c>ZSTD_buildFSETable_body</c>, slow spread: (nextState, nbAdditionalBits, nbBits, baseValue) per state.</summary>
    private static (int, int, int, uint)[] Reference(SequenceCode code, short[] norm, int tableLog)
    {
        int tableSize = 1 << tableLog;
        int highThreshold = tableSize - 1;
        int[] symbolOf = new int[tableSize];
        int[] symbolNext = new int[norm.Length];
        for (int s = 0; s < norm.Length; s++)
        {
            if (norm[s] == -1)
            {
                symbolOf[highThreshold--] = s;
                symbolNext[s] = 1;
            }
            else
            {
                symbolNext[s] = norm[s];
            }
        }

        int step = Fse.TableStep(tableSize);
        int mask = tableSize - 1;
        int position = 0;
        for (int s = 0; s < norm.Length; s++)
        {
            for (int i = 0; i < norm[s]; i++)
            {
                symbolOf[position] = s;
                position = (position + step) & mask;
                while (position > highThreshold)
                {
                    position = (position + step) & mask;
                }
            }
        }

        Assert.Equal(0, position);
        ReadOnlySpan<uint> baseValue = code switch
        {
            SequenceCode.LiteralLength => SequenceCodes.LiteralLengthBase,
            SequenceCode.Offset => SequenceCodes.OffsetBase,
            _ => SequenceCodes.MatchLengthBase,
        };
        ReadOnlySpan<byte> bits = code switch
        {
            SequenceCode.LiteralLength => SequenceCodes.LiteralLengthBits,
            SequenceCode.Offset => SequenceCodes.OffsetBits,
            _ => SequenceCodes.MatchLengthBits,
        };

        var entries = new (int, int, int, uint)[tableSize];
        for (int u = 0; u < tableSize; u++)
        {
            int symbol = symbolOf[u];
            int nextState = symbolNext[symbol]++;
            int nbBits = tableLog - (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)nextState));
            entries[u] = ((nextState << nbBits) - tableSize, bits[symbol], nbBits, baseValue[symbol]);
        }

        return entries;
    }
}
