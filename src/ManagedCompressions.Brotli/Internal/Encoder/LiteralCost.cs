using System;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's estimate of each literal's cost (<c>c/enc/literal_cost.c</c>), from a sliding window's
/// histogram, by UTF-8 position for text; and its test of text (<c>c/enc/utf8_util.c</c>).
/// </summary>
internal static unsafe class LiteralCost
{
    /// <summary>brotli's <c>kMinUTF8Ratio</c>.</summary>
    public const double MinUtf8Ratio = 0.75;

    /// <summary>brotli's <c>BrotliParseAsUTF8</c>: the bytes of a code point, or 1 and a symbol above Unicode's.</summary>
    private static nuint ParseAsUtf8(out int symbol, byte* input, nuint size)
    {
        if ((input[0] & 0x80) == 0)
        {
            symbol = input[0];
            if (symbol > 0)
            {
                return 1;
            }
        }

        if (size > 1u && (input[0] & 0xE0) == 0xC0 && (input[1] & 0xC0) == 0x80)
        {
            symbol = ((input[0] & 0x1F) << 6) | (input[1] & 0x3F);
            if (symbol > 0x7F)
            {
                return 2;
            }
        }

        if (size > 2u && (input[0] & 0xF0) == 0xE0 && (input[1] & 0xC0) == 0x80 && (input[2] & 0xC0) == 0x80)
        {
            symbol = ((input[0] & 0x0F) << 12) | ((input[1] & 0x3F) << 6) | (input[2] & 0x3F);
            if (symbol > 0x7FF)
            {
                return 3;
            }
        }

        if (size > 3u && (input[0] & 0xF8) == 0xF0 && (input[1] & 0xC0) == 0x80 && (input[2] & 0xC0) == 0x80 && (input[3] & 0xC0) == 0x80)
        {
            symbol = ((input[0] & 0x07) << 18) | ((input[1] & 0x3F) << 12) | ((input[2] & 0x3F) << 6) | (input[3] & 0x3F);
            if (symbol > 0xFFFF && symbol <= 0x10FFFF)
            {
                return 4;
            }
        }

        symbol = 0x110000 | input[0];
        return 1;
    }

    /// <summary>brotli's <c>BrotliIsMostlyUTF8</c>: whether <paramref name="minFraction"/> of the bytes are UTF-8.</summary>
    public static bool IsMostlyUtf8(byte* data, nuint pos, nuint mask, nuint length, double minFraction)
    {
        nuint sizeUtf8 = 0;
        nuint i = 0;
        while (i < length)
        {
            nuint bytesRead = ParseAsUtf8(out int symbol, data + ((pos + i) & mask), length - i);
            i += bytesRead;
            if (symbol < 0x110000)
            {
                sizeUtf8 += bytesRead;
            }
        }

        return (double)sizeUtf8 > minFraction * (double)length;
    }

    private static nuint Utf8Position(nuint last, nuint c, nuint clamp)
    {
        if (c < 128)
        {
            return 0;
        }

        if (c >= 192)
        {
            return Math.Min(1, clamp);
        }

        return last < 0xE0 ? 0 : Math.Min(2, clamp);
    }

    private static nuint DecideMultiByteStatsLevel(nuint pos, nuint len, nuint mask, byte* data)
    {
        nuint* counts = stackalloc nuint[3];
        counts[0] = counts[1] = counts[2] = 0;
        nuint maxUtf8 = 1;
        nuint lastC = 0;
        for (nuint i = 0; i < len; ++i)
        {
            nuint c = data[(pos + i) & mask];
            ++counts[Utf8Position(lastC, c, 2)];
            lastC = c;
        }

        if (counts[2] < 500)
        {
            maxUtf8 = 1;
        }

        if (counts[1] + counts[2] < 25)
        {
            maxUtf8 = 0;
        }

        return maxUtf8;
    }

    private static void EstimateBitCostsForLiteralsUtf8(nuint pos, nuint len, nuint mask, byte* data, nuint* histogram, float* cost)
    {
        nuint maxUtf8 = DecideMultiByteStatsLevel(pos, len, mask, data);
        const nuint windowHalf = 495;
        nuint inWindow = Math.Min(windowHalf, len);
        nuint* inWindowUtf8 = stackalloc nuint[3];
        inWindowUtf8[0] = inWindowUtf8[1] = inWindowUtf8[2] = 0;
        new Span<nuint>(histogram, 3 * 256).Clear();
        {
            nuint lastC = 0;
            nuint utf8Pos = 0;
            for (nuint i = 0; i < inWindow; ++i)
            {
                nuint c = data[(pos + i) & mask];
                ++histogram[(256 * utf8Pos) + c];
                ++inWindowUtf8[utf8Pos];
                utf8Pos = Utf8Position(lastC, c, maxUtf8);
                lastC = c;
            }
        }

        for (nuint i = 0; i < len; ++i)
        {
            if (i >= windowHalf)
            {
                // A byte out of the window, behind.
                nuint c = i < windowHalf + 1 ? (nuint)0 : data[(pos + i - windowHalf - 1) & mask];
                nuint lastC = i < windowHalf + 2 ? (nuint)0 : data[(pos + i - windowHalf - 2) & mask];
                nuint utf8Pos2 = Utf8Position(lastC, c, maxUtf8);
                --histogram[(256 * utf8Pos2) + data[(pos + i - windowHalf) & mask]];
                --inWindowUtf8[utf8Pos2];
            }

            if (i + windowHalf < len)
            {
                // A byte into the window, ahead.
                nuint c = data[(pos + i + windowHalf - 1) & mask];
                nuint lastC = data[(pos + i + windowHalf - 2) & mask];
                nuint utf8Pos2 = Utf8Position(lastC, c, maxUtf8);
                ++histogram[(256 * utf8Pos2) + data[(pos + i + windowHalf) & mask]];
                ++inWindowUtf8[utf8Pos2];
            }

            {
                nuint c = i < 1 ? (nuint)0 : data[(pos + i - 1) & mask];
                nuint lastC = i < 2 ? (nuint)0 : data[(pos + i - 2) & mask];
                nuint utf8Pos = Utf8Position(lastC, c, maxUtf8);
                nuint maskedPos = (pos + i) & mask;
                nuint histo = histogram[(256 * utf8Pos) + data[maskedPos]];
                if (histo == 0)
                {
                    histo = 1;
                }

                double litCost = FastLog.FastLog2(inWindowUtf8[utf8Pos]) - FastLog.FastLog2(histo);
                litCost += 0.02905;
                if (litCost < 1.0)
                {
                    litCost *= 0.5;
                    litCost += 0.5;
                }

                // The first bytes cost more.
                if (i < 2000)
                {
                    litCost += 0.7 - ((double)(2000 - i) / 2000.0 * 0.35);
                }

                cost[i] = (float)litCost;
            }
        }
    }

    /// <summary>brotli's <c>BrotliEstimateBitCostsForLiterals</c>: <paramref name="histogram"/> holds 3 * 256 counts.</summary>
    public static void EstimateBitCostsForLiterals(nuint pos, nuint len, nuint mask, byte* data, nuint* histogram, float* cost)
    {
        if (IsMostlyUtf8(data, pos, mask, len, MinUtf8Ratio))
        {
            EstimateBitCostsForLiteralsUtf8(pos, len, mask, data, histogram, cost);
            return;
        }

        const nuint windowHalf = 2000;
        nuint inWindow = Math.Min(windowHalf, len);
        new Span<nuint>(histogram, 256).Clear();
        for (nuint i = 0; i < inWindow; ++i)
        {
            ++histogram[data[(pos + i) & mask]];
        }

        for (nuint i = 0; i < len; ++i)
        {
            if (i >= windowHalf)
            {
                --histogram[data[(pos + i - windowHalf) & mask]];
                --inWindow;
            }

            if (i + windowHalf < len)
            {
                ++histogram[data[(pos + i + windowHalf) & mask]];
                ++inWindow;
            }

            nuint histo = histogram[data[(pos + i) & mask]];
            if (histo == 0)
            {
                histo = 1;
            }

            double litCost = FastLog.FastLog2(inWindow) - FastLog.FastLog2(histo);
            litCost += 0.029;
            if (litCost < 1.0)
            {
                litCost *= 0.5;
                litCost += 0.5;
            }

            cost[i] = (float)litCost;
        }
    }
}
