using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's logarithms (<c>c/enc/fast_log.h</c>) and entropies (<c>c/enc/bit_cost.h</c>), computed as the
/// brotli .NET ships computes them, so that the encoder's decisions, and its bytes, are that brotli's.
/// </summary>
/// <remarks>
/// <para>
/// <c>FastLog2</c> reads a table below 256, whose entries are float literals, and calls the C library
/// above. Which call depends on how brotli was built: its CMake build defines <c>BROTLI_HAVE_LOG2</c> from
/// a check that fails under MSVC, so .NET's Windows brotli computes <c>log(v) * (1 / ln 2)</c>, while its
/// Linux and macOS brotli call <c>log2(v)</c>. The two differ in the last bit for some <c>v</c>, enough to
/// change a few streams at qualities 10 and 11. <see cref="Math.Log(double)"/> and <see cref="Math.Log2"/> call
/// the same C library as the native code on each platform.
/// </para>
/// <para>
/// The arithmetic is brotli's, in its types and its order: a double where brotli computes a double, a
/// float where it computes a float, and no fused multiply-add, which .NET never contracts to and brotli
/// is built without.
/// </para>
/// </remarks>
internal static class FastLog
{
    private const double Log2Inverse = 1.4426950408889634;

    /// <summary>Whether <c>FastLog2</c> above 255 is <c>log(v) * (1 / ln 2)</c>, as .NET's Windows brotli has it.</summary>
    private static readonly bool LogTimesInverse = OperatingSystem.IsWindows();

    private static readonly double[] Table = BuildTable();

    private static double[] BuildTable()
    {
        double[] table = new double[256];
        ReadOnlySpan<float> floats = EncoderTables.Log2;
        for (int i = 0; i < 256; i++)
        {
            table[i] = floats[i];
        }

        return table;
    }

    /// <summary>The index of the highest set bit of a non-zero value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Log2FloorNonZero(nuint n) => BitOperations.Log2(n);

    /// <summary>brotli's <c>FastLog2</c>, with <c>FastLog2(0) == 0</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FastLog2(nuint v)
    {
        if (v < 256)
        {
            return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Table), (nint)v);
        }

        return Slow(v);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double Slow(nuint v) => LogTimesInverse ? Math.Log((double)v) * Log2Inverse : Math.Log2((double)v);

    /// <summary>brotli's <c>ShannonEntropy</c>: the entropy of a population in bits, and its total.</summary>
    public static unsafe double ShannonEntropy(uint* population, int size, out nuint total)
    {
        nuint sum = 0;
        double result = 0;
        for (int i = 0; i < size; i++)
        {
            nuint p = population[i];
            sum += p;
            result -= (double)p * FastLog2(p);
        }

        if (sum != 0)
        {
            result += (double)sum * FastLog2(sum);
        }

        total = sum;
        return result;
    }

    /// <summary>brotli's <c>BitsEntropy</c>: the Shannon entropy, but at least one bit a symbol.</summary>
    public static unsafe double BitsEntropy(uint* population, int size)
    {
        double result = ShannonEntropy(population, size, out nuint sum);
        if (result < (double)sum)
        {
            result = sum;
        }

        return result;
    }
}
