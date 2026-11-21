using System;
using System.Globalization;

namespace ManagedCompressions.Zstd.Bench;

/// <summary>
/// A speedup measured from paired timings: a candidate and a reference timed in the same rounds, their
/// ratio taken round by round, then the geometric mean of those ratios with its 95% interval (Student's
/// t over the logarithms). Pairing by round cancels what the machine did during a round, which a ratio
/// of two medians keeps: a change is called faster or slower only where the interval excludes 1.
/// </summary>
/// <param name="Speedup">The reference's time over the candidate's: above 1, the candidate is faster.</param>
/// <param name="Low">The interval's lower end; NaN below two pairs.</param>
/// <param name="High">The interval's upper end; NaN below two pairs.</param>
/// <param name="Pairs">The rounds the ratio was taken over.</param>
internal readonly record struct PairedRatio(double Speedup, double Low, double High, int Pairs)
{
    /// <summary>The widest half-interval, relative to the speedup, for an A/A comparison to pass: 1%.</summary>
    public const double AaTolerance = 0.01;

    /// <summary>Whether the interval exists, from two pairs on.</summary>
    public bool HasInterval => !double.IsNaN(Low);

    /// <summary>Half the interval's width, relative to the speedup.</summary>
    public double HalfWidth => (High - Low) / 2 / Speedup;

    /// <summary>
    /// The speedup of <paramref name="candidate"/> over <paramref name="reference"/>, from their times in
    /// the same rounds: element k of each was measured in round k.
    /// </summary>
    public static PairedRatio Of(ReadOnlySpan<double> reference, ReadOnlySpan<double> candidate)
    {
        int n = Math.Min(reference.Length, candidate.Length);
        if (n == 0)
        {
            return new PairedRatio(double.NaN, double.NaN, double.NaN, 0);
        }

        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += Math.Log(reference[i] / candidate[i]);
        }

        double mean = sum / n;
        if (n < 2)
        {
            return new PairedRatio(Math.Exp(mean), double.NaN, double.NaN, n);
        }

        double squares = 0;
        for (int i = 0; i < n; i++)
        {
            double d = Math.Log(reference[i] / candidate[i]) - mean;
            squares += d * d;
        }

        double half = T975(n - 1) * Math.Sqrt(squares / (n - 1) / n);
        return new PairedRatio(Math.Exp(mean), Math.Exp(mean - half), Math.Exp(mean + half), n);
    }

    /// <summary>
    /// The median of the round-by-round speedups of <paramref name="candidate"/> over
    /// <paramref name="reference"/>, with the distribution-free 95% interval of a median: the order
    /// statistics the binomial law puts 2.5% beyond on each side. A round that something else on the
    /// machine slowed moves it no more than any other round, where it would drag a mean and widen a t
    /// interval: the measure for many short rounds, as a process takes them. Below six rounds the
    /// interval is the whole range of the ratios.
    /// </summary>
    public static PairedRatio OfMedian(ReadOnlySpan<double> reference, ReadOnlySpan<double> candidate)
    {
        int n = Math.Min(reference.Length, candidate.Length);
        if (n == 0)
        {
            return new PairedRatio(double.NaN, double.NaN, double.NaN, 0);
        }

        double[] ratios = new double[n];
        for (int i = 0; i < n; i++)
        {
            ratios[i] = reference[i] / candidate[i];
        }

        Array.Sort(ratios);
        double median = (n & 1) == 1 ? ratios[n / 2] : Math.Sqrt(ratios[(n / 2) - 1] * ratios[n / 2]);
        if (n < 2)
        {
            return new PairedRatio(median, double.NaN, double.NaN, n);
        }

        // The largest k with P(Binomial(n, 1/2) < k) <= 2.5%: [ratios[k], ratios[n - 1 - k]] holds the
        // median with 95% confidence at least.
        double probability = Math.Pow(0.5, n);
        double below = 0;
        int k = 0;
        while (k < n / 2 && below + probability <= 0.025)
        {
            below += probability;
            k++;
            probability = probability * (n - k + 1) / k;
        }

        return new PairedRatio(median, ratios[k], ratios[n - 1 - k], n);
    }

    /// <summary>
    /// Whether an A/A comparison (the same code under two names) is clean: its whole interval lies within
    /// <see cref="AaTolerance"/> of 1. Two instances of the same code may differ by a few tenths of a
    /// percent where their tables landed, an interval that excludes 1 and is still clean; a run whose
    /// A/A strays further measured noise, or a placement effect larger than the changes it can judge.
    /// </summary>
    public bool PassesAa => HasInterval && Pairs >= MinPairs && Low >= 1 - AaTolerance && High <= 1 + AaTolerance;

    /// <summary>The fewest pairs for <see cref="OfMedian"/> to give an interval narrower than the range of the ratios.</summary>
    public const int MinPairs = 6;

    /// <summary>The speedup and its interval, as <c>1.091x (1.085-1.097)</c>.</summary>
    public override string ToString() => HasInterval
        ? string.Create(CultureInfo.InvariantCulture, $"{Speedup:F3}x ({Low:F3}-{High:F3})")
        : string.Create(CultureInfo.InvariantCulture, $"{Speedup:F3}x");

    /// <summary>
    /// Student's t at 0.975 with <paramref name="df"/> degrees of freedom: exact below 5, the
    /// Cornish-Fisher expansion of the normal quantile above (within 0.2% from 5 on).
    /// </summary>
    internal static double T975(int df)
    {
        switch (df)
        {
            case <= 0:
                return double.NaN;
            case 1:
                return 12.706;
            case 2:
                return 4.303;
            case 3:
                return 3.182;
            case 4:
                return 2.776;
        }

        const double z = 1.959963984540054;
        double z3 = z * z * z;
        double z5 = z3 * z * z;
        double z7 = z5 * z * z;
        double v = df;
        return z
            + (z3 + z) / (4 * v)
            + ((5 * z5) + (16 * z3) + (3 * z)) / (96 * v * v)
            + ((3 * z7) + (19 * z5) + (17 * z3) - (15 * z)) / (384 * v * v * v);
    }
}
