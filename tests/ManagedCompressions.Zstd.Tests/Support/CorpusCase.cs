using System;
using System.Collections.Concurrent;
using System.Globalization;

namespace ManagedCompressions.Zstd.Tests.Support;

/// <summary>
/// One frame of the differential corpus, named by everything needed to rebuild it:
/// <c>kind/size/level</c> then flags — <c>chk</c> (content checksum), <c>dict=trained|raw</c>,
/// <c>wlog=N</c> (window log), <c>ldm</c> (long-distance matching), <c>block=N</c> (target block size),
/// <c>nofcs</c> (streamed in chunks, so that the header declares no content size).
/// </summary>
internal sealed record CorpusCase(
    string Kind, int Size, int Level, bool Checksum, string? Dictionary, int WindowLog, bool LongDistance,
    int BlockSize, bool NoContentSize)
{
    public static CorpusCase Parse(string name)
    {
        string[] parts = name.Split('/');
        string? dictionary = null;
        bool checksum = false, ldm = false, noFcs = false;
        int windowLog = 0, blockSize = 0;
        for (int i = 3; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part == "chk") checksum = true;
            else if (part == "ldm") ldm = true;
            else if (part == "nofcs") noFcs = true;
            else if (part.StartsWith("dict=", StringComparison.Ordinal)) dictionary = part[5..];
            else if (part.StartsWith("wlog=", StringComparison.Ordinal)) windowLog = int.Parse(part[5..], CultureInfo.InvariantCulture);
            else if (part.StartsWith("block=", StringComparison.Ordinal)) blockSize = int.Parse(part[6..], CultureInfo.InvariantCulture);
            else throw new ArgumentException("unknown flag " + part, nameof(name));
        }

        return new CorpusCase(
            parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2][1..], CultureInfo.InvariantCulture),
            checksum, dictionary, windowLog, ldm, blockSize, noFcs);
    }

    public static string Name(string kind, int size, int level, params string[] flags) =>
        string.Join('/', [kind, size.ToString(CultureInfo.InvariantCulture), "L" + level.ToString(CultureInfo.InvariantCulture), .. flags]);

    public byte[] Data => DataKinds.Generate(Kind, Size);

    /// <summary>The dictionary bytes as the decoder receives them, and the oracle's own object.</summary>
    public (byte[] Bytes, NativeZstdDictionary Native)? GetDictionary() => Dictionary is null ? null : Dictionaries.Get(Kind, Dictionary);

    /// <summary>Compresses <see cref="Data"/> with the oracle's libzstd.</summary>
    public byte[] Compress(byte[] data)
    {
        var options = new NativeZstdOptions { Quality = Level, AppendChecksum = Checksum };
        if (WindowLog > 0) options.WindowLog = WindowLog;
        if (LongDistance) options.EnableLongDistanceMatching = true;
        if (BlockSize > 0) options.TargetBlockSize = BlockSize;
        if (GetDictionary() is { } dictionary) options.Dictionary = dictionary.Native;
        return NativeZstd.Compress(data, options, NoContentSize);
    }
}

/// <summary>Dictionaries per data kind, built once: trained on samples, or raw content.</summary>
internal static class Dictionaries
{
    private static readonly ConcurrentDictionary<string, (byte[], NativeZstdDictionary)> Cache = new();

    public static (byte[] Bytes, NativeZstdDictionary Native) Get(string kind, string type) =>
        Cache.GetOrAdd(kind + "/" + type, _ => Build(kind, type));

    private static (byte[], NativeZstdDictionary) Build(string kind, string type)
    {
        switch (type)
        {
            case "trained":
            {
                const int sampleCount = 200;
                const int sampleSize = 1024;
                byte[] samples = new byte[sampleCount * sampleSize];
                int[] lengths = new int[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    DataKinds.Generate(kind, sampleSize, 1000 + i).CopyTo(samples, i * sampleSize);
                    lengths[i] = sampleSize;
                }

                if (NativeZstdDictionary.Train(samples, lengths, 16 * 1024) is { } trained)
                {
                    return (trained.Bytes, trained);
                }

                // Some kinds (zeros, noise) give the trainer nothing to learn: fall back on a
                // dictionary trained on text, which is still a valid zstd-format dictionary.
                return kind == "text" ? throw new InvalidOperationException("cannot train on text") : Get("text", "trained");
            }

            case "raw":
            {
                byte[] raw = DataKinds.Generate(kind, 32 * 1024, 7);
                return (raw, NativeZstdDictionary.Create(raw));
            }

            default:
                throw new ArgumentException("unknown dictionary type " + type, nameof(type));
        }
    }
}

