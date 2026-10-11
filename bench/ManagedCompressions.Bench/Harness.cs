using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace ManagedCompressions.Bench;

/// <summary>A candidate's median throughput, in MB/s of content (10^6 bytes), on a data set at a level, in one direction.</summary>
internal sealed record Row(string Codec, string Set, int Level, string Direction, string Candidate, string Label, bool Native, double Speed, double Ratio, bool Differs)
{
    public string Key => $"{Codec}/{Set}/{Level}/{Direction}/{Candidate}";

    public string ToTsv() => string.Join('\t', Codec, Set, Level.ToString(CultureInfo.InvariantCulture), Direction, Candidate, Label,
        Native ? "native" : "managed", Speed.ToString("R", CultureInfo.InvariantCulture), Ratio.ToString("R", CultureInfo.InvariantCulture), Differs ? "differs" : "same");

    public static Row FromTsv(string line)
    {
        string[] f = line.Split('\t');
        return new Row(f[0], f[1], int.Parse(f[2], CultureInfo.InvariantCulture), f[3], f[4], f[5], f[6] == "native",
            double.Parse(f[7], CultureInfo.InvariantCulture), double.Parse(f[8], CultureInfo.InvariantCulture), f[9] == "differs");
    }
}

/// <summary>
/// The measure of one codec on one data set at one level. Every candidate is first warmed up, so that
/// the JIT has its final code; then the candidates compress the data set, then decode the reference's
/// output, each direction in rounds that time every candidate once, in an order that rotates, so that
/// whatever the machine does at that moment lands on all of them alike. The first round is also the
/// check: every compressed record must decode back to its content with the reference's decoder, and
/// every decoder must give the content back.
/// </summary>
internal static class Harness
{
    private const int MinRounds = 3;
    private const int MaxRounds = 400;

    public static List<Row> Measure(string codec, DataSet set, int level, List<Candidate> candidates, double budget, bool compress, bool decompress)
    {
        int records = set.Records;
        int[] bounds = new int[records + 1];
        for (int i = 0; i < records; i++)
        {
            bounds[i + 1] = bounds[i] + Candidates.Bound(codec, set.Offsets[i + 1] - set.Offsets[i]);
        }

        // Every page of the outputs touched before anything is timed.
        byte[] reference = new byte[bounds[^1]];
        byte[] scratch = new byte[bounds[^1]];
        byte[] decoded = new byte[set.Content.Length];
        Array.Fill(reference, (byte)1);
        Array.Fill(scratch, (byte)1);
        Array.Fill(decoded, (byte)1);
        int[] sizes = new int[records];
        int[] written = new int[records];
        int count = candidates.Count;
        Candidate referenceCandidate = candidates[0];

        Warm(codec, set, candidates);

        // The reference's records, written once untimed: the decoders' input and the frames to match.
        Pass(referenceCandidate.Compress!, set.Content, set.Offsets, reference, bounds, sizes);
        long referenceBytes = sizes.Sum(s => (long)s);

        var ratio = new double[count];
        var compressionDiffers = new bool[count];
        double[] compression = new double[count];
        List<int> compressing = Enumerable.Range(0, count).Where(c => candidates[c].Compress is not null).ToList();
        if (compress)
        {
            compression = Time(candidates, compressing, budget, (c, round) =>
            {
                double seconds = Pass(candidates[c].Compress!, set.Content, set.Offsets, scratch, bounds, written);
                if (round == 0)
                {
                    ratio[c] = (double)set.Content.Length / written.Sum(w => (long)w);
                    compressionDiffers[c] = !RoundTrips(referenceCandidate, set, scratch, bounds, written, decoded);
                    if (c != 0)
                    {
                        // The managed compressors claim the native frames and blocks, byte for byte.
                        for (int i = 0; i < records && !compressionDiffers[c]; i++)
                        {
                            compressionDiffers[c] = written[i] != sizes[i] || !scratch.AsSpan(bounds[i], sizes[i]).SequenceEqual(reference.AsSpan(bounds[i], sizes[i]));
                        }
                    }
                }

                return seconds;
            });
        }

        // The reference's records, one after the other, for the decoders.
        int[] frameOffsets = new int[records + 1];
        for (int i = 0; i < records; i++)
        {
            frameOffsets[i + 1] = frameOffsets[i] + sizes[i];
        }

        byte[] frames = new byte[frameOffsets[^1]];
        for (int i = 0; i < records; i++)
        {
            reference.AsSpan(bounds[i], sizes[i]).CopyTo(frames.AsSpan(frameOffsets[i]));
        }

        var decompressionDiffers = new bool[count];
        double[] decompression = new double[count];
        if (decompress)
        {
            decompression = Time(candidates, Enumerable.Range(0, count).ToList(), budget, (c, round) =>
            {
                if (round > 0)
                {
                    return Pass(candidates[c].Decompress, frames, frameOffsets, decoded, set.Offsets, written);
                }

                Array.Fill(decoded, (byte)1);
                double seconds = Pass(candidates[c].Decompress, frames, frameOffsets, decoded, set.Offsets, written);
                decompressionDiffers[c] = !decoded.AsSpan().SequenceEqual(set.Content);
                for (int i = 0; i < records && !decompressionDiffers[c]; i++)
                {
                    decompressionDiffers[c] = written[i] != set.Offsets[i + 1] - set.Offsets[i];
                }

                return seconds;
            });
        }

        double referenceRatio = (double)set.Content.Length / referenceBytes;
        var rows = new List<Row>();
        foreach (int c in compressing.Where(_ => compress))
        {
            rows.Add(new Row(codec, set.Name, level, "compression", candidates[c].Name, candidates[c].Label, candidates[c].Native,
                set.Content.Length / compression[c] / 1e6, ratio[c], compressionDiffers[c]));
        }

        for (int c = 0; c < count && decompress; c++)
        {
            rows.Add(new Row(codec, set.Name, level, "decompression", candidates[c].Name, candidates[c].Label, candidates[c].Native,
                set.Content.Length / decompression[c] / 1e6, referenceRatio, decompressionDiffers[c]));
        }

        return rows;
    }

    /// <summary>
    /// Each candidate in both directions over the first MiB or so of the data set, at least 30 times
    /// and half a second, at most four seconds: long enough for the tiered JIT to give every method its
    /// final, profiled code. The first MiB of a large record selects the same parameters as the whole.
    /// </summary>
    private static void Warm(string codec, DataSet set, List<Candidate> candidates)
    {
        var offsets = new List<int> { 0 };
        int total = 0;
        for (int i = 0; i < set.Records && total < 1 << 20; i++)
        {
            total += Math.Min(set.Offsets[i + 1] - set.Offsets[i], 1 << 20);
            offsets.Add(total);
        }

        byte[] content = new byte[total];
        for (int i = 0; i + 1 < offsets.Count; i++)
        {
            set.Content.AsSpan(set.Offsets[i], offsets[i + 1] - offsets[i]).CopyTo(content.AsSpan(offsets[i]));
        }

        int[] o = offsets.ToArray();
        int[] bounds = new int[o.Length];
        for (int i = 0; i + 1 < o.Length; i++)
        {
            bounds[i + 1] = bounds[i] + Candidates.Bound(codec, o[i + 1] - o[i]);
        }

        byte[] compressed = new byte[bounds[^1]];
        byte[] decoded = new byte[total];
        int[] sizes = new int[o.Length - 1];
        int[] written = new int[o.Length - 1];
        Pass(candidates[0].Compress!, content, o, compressed, bounds, sizes);
        int[] frameOffsets = new int[o.Length];
        byte[] frames = new byte[compressed.Length];
        for (int i = 0; i < sizes.Length; i++)
        {
            compressed.AsSpan(bounds[i], sizes[i]).CopyTo(frames.AsSpan(frameOffsets[i]));
            frameOffsets[i + 1] = frameOffsets[i] + sizes[i];
        }

        byte[] scratch = new byte[bounds[^1]];
        foreach (Candidate candidate in candidates)
        {
            long start = Stopwatch.GetTimestamp();
            for (int n = 0; ; n++)
            {
                double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                if ((n >= 30 && elapsed >= 0.5) || elapsed >= 4)
                {
                    break;
                }

                if (candidate.Compress is not null)
                {
                    Pass(candidate.Compress, content, o, scratch, bounds, written);
                }

                Pass(candidate.Decompress, frames, frameOffsets, decoded, o, written);
            }
        }
    }

    /// <summary>Whether every record a candidate wrote decodes back to its content with the reference's decoder.</summary>
    private static bool RoundTrips(Candidate reference, DataSet set, byte[] compressed, int[] bounds, int[] written, byte[] decoded)
    {
        for (int i = 0; i < set.Records; i++)
        {
            if (written[i] < 0)
            {
                return false;
            }

            Span<byte> into = decoded.AsSpan(set.Offsets[i], set.Offsets[i + 1] - set.Offsets[i]);
            if (reference.Decompress(compressed.AsSpan(bounds[i], written[i]), into) != into.Length || !into.SequenceEqual(set.Record(i)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The codec over every record, into its slot of the output; the seconds it took.</summary>
    private static double Pass(SpanCodec codec, byte[] input, int[] inputOffsets, byte[] output, int[] outputOffsets, int[] written)
    {
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < written.Length; i++)
        {
            written[i] = codec(
                input.AsSpan(inputOffsets[i], inputOffsets[i + 1] - inputOffsets[i]),
                output.AsSpan(outputOffsets[i], outputOffsets[i + 1] - outputOffsets[i]));
        }

        return Stopwatch.GetElapsedTime(start).TotalSeconds;
    }

    /// <summary>
    /// Rounds of <paramref name="run"/>(candidate, round), every candidate once a round in an order that
    /// rotates; as many as fit in the budget given what the first took. Each candidate's median, in seconds.
    /// </summary>
    private static double[] Time(List<Candidate> candidates, List<int> which, double budget, Func<int, int, double> run)
    {
        var medians = new double[candidates.Count];
        if (which.Count == 0)
        {
            return medians;
        }

        var first = new double[candidates.Count];
        foreach (int c in which)
        {
            first[c] = run(c, 0);
        }

        int rounds = Math.Clamp((int)Math.Ceiling(budget / first.Sum()), MinRounds, MaxRounds);
        var samples = new double[candidates.Count][];
        foreach (int c in which)
        {
            samples[c] = new double[rounds];
            samples[c][0] = first[c];
        }

        for (int r = 1; r < rounds; r++)
        {
            for (int k = 0; k < which.Count; k++)
            {
                int c = which[(r + k) % which.Count];
                samples[c][r] = run(c, r);
            }
        }

        foreach (int c in which)
        {
            Array.Sort(samples[c]);
            medians[c] = samples[c][rounds / 2];
        }

        return medians;
    }
}
