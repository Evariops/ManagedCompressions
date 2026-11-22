using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// A histogram of <c>c/enc/histogram_inc.h</c>: its counts first, then its total and its cost, so that
/// the generic code reaches them from a pointer to the histogram.
/// </summary>
internal unsafe interface IHistogram
{
    /// <summary>brotli's <c>DATA_SIZE</c>: the number of counts.</summary>
    static abstract int DataSize { get; }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HistogramLiteral : IHistogram
{
    public fixed uint Data[Prefix.NumLiteralSymbols];
    public nuint TotalCount;
    public double BitCost;

    public static int DataSize => Prefix.NumLiteralSymbols;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HistogramCommand : IHistogram
{
    public fixed uint Data[Prefix.NumCommandSymbols];
    public nuint TotalCount;
    public double BitCost;

    public static int DataSize => Prefix.NumCommandSymbols;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HistogramDistance : IHistogram
{
    /// <summary>brotli's <c>BROTLI_NUM_HISTOGRAM_DISTANCE_SYMBOLS</c>.</summary>
    public const int Symbols = 544;

    public fixed uint Data[Symbols];
    public nuint TotalCount;
    public double BitCost;

    public static int DataSize => Symbols;
}

/// <summary>The functions of <c>c/enc/histogram_inc.h</c>, for the three histograms.</summary>
internal static unsafe class Histogram
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint* Data<T>(T* h)
        where T : unmanaged, IHistogram => (uint*)h;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint* TotalCount<T>(T* h)
        where T : unmanaged, IHistogram => (nuint*)((uint*)h + T.DataSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double* BitCost<T>(T* h)
        where T : unmanaged, IHistogram => (double*)(TotalCount(h) + 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear<T>(T* h)
        where T : unmanaged, IHistogram
    {
        new Span<uint>(h, T.DataSize).Clear();
        *TotalCount(h) = 0;
        *BitCost(h) = double.PositiveInfinity;
    }

    public static void ClearAll<T>(T* array, nuint length)
        where T : unmanaged, IHistogram
    {
        for (nuint i = 0; i < length; ++i)
        {
            Clear(array + i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add<T>(T* h, nuint value)
        where T : unmanaged, IHistogram
    {
        ++((uint*)h)[value];
        ++*TotalCount(h);
    }

    /// <summary>brotli's <c>HistogramAddHistogram</c>, a vector at a time.</summary>
    public static void AddHistogram<T>(T* self, T* v)
        where T : unmanaged, IHistogram
    {
        *TotalCount(self) += *TotalCount(v);
        AddCounts((uint*)self, (uint*)self, (uint*)v, T.DataSize);
    }

    /// <summary>
    /// <c>*dst = *a</c> then <c>HistogramAddHistogram(dst, b)</c>, as brotli writes it, in one pass: the
    /// counts and the totals added, and <paramref name="a"/>'s cost.
    /// </summary>
    public static void Sum<T>(T* dst, T* a, T* b)
        where T : unmanaged, IHistogram
    {
        AddCounts((uint*)dst, (uint*)a, (uint*)b, T.DataSize);
        *TotalCount(dst) = *TotalCount(a) + *TotalCount(b);
        *BitCost(dst) = *BitCost(a);
    }

    /// <summary><c>dst[i] = a[i] + b[i]</c>, for <paramref name="n"/> counts, a multiple of 8.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddCounts(uint* dst, uint* a, uint* b, int n)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i < n; i += 8)
            {
                (Vector256.Load(a + i) + Vector256.Load(b + i)).Store(dst + i);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; i < n; i += 4)
            {
                (Vector128.Load(a + i) + Vector128.Load(b + i)).Store(dst + i);
            }
        }

        for (; i < n; ++i)
        {
            dst[i] = a[i] + b[i];
        }
    }
}
