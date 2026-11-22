using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// A histogram of <c>c/enc/histogram_inc.h</c>: its counts, its total and its cost, which the generic
/// code reaches through the histogram's members.
/// </summary>
internal interface IHistogram
{
    /// <summary>brotli's <c>DATA_SIZE</c>: the number of counts.</summary>
    static abstract int DataSize { get; }

    /// <summary>The counts.</summary>
    [UnscopedRef]
    Span<uint> Data { get; }

    nuint TotalCount { get; set; }

    double BitCost { get; set; }
}

[InlineArray(Prefix.NumLiteralSymbols)]
internal struct LiteralCounts
{
    private uint _element;
}

[InlineArray(Prefix.NumCommandSymbols)]
internal struct CommandCounts
{
    private uint _element;
}

[InlineArray(HistogramDistance.Symbols)]
internal struct DistanceCounts
{
    private uint _element;
}

internal struct HistogramLiteral : IHistogram
{
    private LiteralCounts _data;

    public static int DataSize => Prefix.NumLiteralSymbols;

    [UnscopedRef]
    public Span<uint> Data => _data;

    public nuint TotalCount { get; set; }

    public double BitCost { get; set; }
}

internal struct HistogramCommand : IHistogram
{
    private CommandCounts _data;

    public static int DataSize => Prefix.NumCommandSymbols;

    [UnscopedRef]
    public Span<uint> Data => _data;

    public nuint TotalCount { get; set; }

    public double BitCost { get; set; }
}

internal struct HistogramDistance : IHistogram
{
    /// <summary>brotli's <c>BROTLI_NUM_HISTOGRAM_DISTANCE_SYMBOLS</c>.</summary>
    public const int Symbols = 544;

    private DistanceCounts _data;

    public static int DataSize => Symbols;

    [UnscopedRef]
    public Span<uint> Data => _data;

    public nuint TotalCount { get; set; }

    public double BitCost { get; set; }
}

/// <summary>The functions of <c>c/enc/histogram_inc.h</c>, for the three histograms.</summary>
internal static class Histogram
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear<T>(ref T h)
        where T : struct, IHistogram
    {
        h.Data.Clear();
        h.TotalCount = 0;
        h.BitCost = double.PositiveInfinity;
    }

    public static void ClearAll<T>(Span<T> array)
        where T : struct, IHistogram
    {
        for (int i = 0; i < array.Length; ++i)
        {
            Clear(ref array[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add<T>(ref T h, nuint value)
        where T : struct, IHistogram
    {
        ++h.Data[(int)value];
        ++h.TotalCount;
    }

    /// <summary>brotli's <c>HistogramAddHistogram</c>, a vector at a time.</summary>
    public static void AddHistogram<T>(ref T self, ref T v)
        where T : struct, IHistogram
    {
        self.TotalCount += v.TotalCount;
        AddCounts(self.Data, self.Data, v.Data);
    }

    /// <summary>
    /// <c>*dst = *a</c> then <c>HistogramAddHistogram(dst, b)</c>, as brotli writes it, in one pass: the
    /// counts and the totals added, and <paramref name="a"/>'s cost.
    /// </summary>
    public static void Sum<T>(ref T dst, ref T a, ref T b)
        where T : struct, IHistogram
    {
        AddCounts(dst.Data, a.Data, b.Data);
        dst.TotalCount = a.TotalCount + b.TotalCount;
        dst.BitCost = a.BitCost;
    }

    /// <summary><c>dst[i] = a[i] + b[i]</c>, for counts of a multiple of 8.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddCounts(Span<uint> dst, ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        int n = dst.Length;
        a = a.Slice(0, n);
        b = b.Slice(0, n);
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= n - 8; i += 8)
            {
                (Vector256.Create(a.Slice(i, 8)) + Vector256.Create(b.Slice(i, 8))).CopyTo(dst.Slice(i, 8));
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= n - 4; i += 4)
            {
                (Vector128.Create(a.Slice(i, 4)) + Vector128.Create(b.Slice(i, 4))).CopyTo(dst.Slice(i, 4));
            }
        }

        for (; i < n; ++i)
        {
            dst[i] = a[i] + b[i];
        }
    }
}
