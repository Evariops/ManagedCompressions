using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ManagedCompressions.Lz4;
using ManagedCompressions.Zstd;

namespace ManagedCompressions.AotSmoke;

/// <summary>
/// Round trips through both libraries, as compiled: CI runs this under Native AOT, on x64 and arm64.
/// Each frame or block must decode back, and must hash to the value below. The compressors write the
/// bytes of libzstd 1.5.7 and lz4 1.10.0, which depend neither on the machine nor on the instruction
/// sets the code uses, so one hash holds everywhere: a difference is a path that computes otherwise.
/// </summary>
/// <remarks>Run with <c>--print</c> to print the hashes instead of checking them.</remarks>
internal static class Program
{
    /// <summary>SHA-256 of each case's output, the first 16 hex digits.</summary>
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["zstd L-5"] = "13927b86c5b71bca",
        ["zstd L1"] = "1231218f02ec70e6",
        ["zstd L3"] = "3035374d845b30d7",
        ["zstd L9"] = "3ab40c895620df2f",
        ["zstd L19"] = "b68707d34e8e9fd7",
        ["zstd L3 dictionary"] = "b69ba8d0047b8e3e",
        ["zstd L1 jobs"] = "cd03fc20b6e2bba7",
        ["lz4 L-1"] = "02f083c3a5546343",
        ["lz4 L1"] = "e952faa01cd845b2",
        ["lz4 L2"] = "99860d8e9e2d522e",
        ["lz4 L9"] = "14705d8759b5fb3c",
        ["lz4 L12"] = "e34956c7a32e5804",
    };

    private static int Main(string[] args)
    {
        bool print = args is ["--print"];
        byte[] text = Text(1 << 20);
        byte[] large = Text(8 << 20);
        var failures = new List<string>();

        void Check(string name, byte[] output)
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(output))[..16];
            if (print)
            {
                Console.WriteLine($"        [\"{name}\"] = \"{hash}\",");
            }
            else if (!Expected.TryGetValue(name, out string? expected) || expected != hash)
            {
                failures.Add($"{name}: {hash}, expected {expected ?? "nothing"}");
            }
        }

        foreach (int level in (int[])[-5, 1, 3, 9, 19])
        {
            var compressor = new ZstdCompressor(level) { AppendChecksum = level == 3 };
            byte[] frame = Zstd(compressor, text);
            Check($"zstd L{level}", frame);
            Expect(failures, $"zstd L{level}", text, Unzstd(new ZstdDecompressor(), frame, text.Length));
        }

        // A raw-content dictionary: its first 64 KiB.
        byte[] dictionary = text.AsSpan(0, 64 << 10).ToArray();
        byte[] sample = text.AsSpan(512 << 10, 16 << 10).ToArray();
        byte[] dictionaryFrame = Zstd(new ZstdCompressor(3, dictionary), sample);
        Check("zstd L3 dictionary", dictionaryFrame);
        Expect(failures, "zstd L3 dictionary", sample, Unzstd(new ZstdDecompressor(dictionary), dictionaryFrame, sample.Length));

        // Jobs on the thread pool: four of 2 MiB at level 1.
        byte[] jobs = new byte[ZstdCompressor.GetMaxCompressedLength(large.Length)];
        int length = Task.Run(async () => await new ZstdCompressor(1).CompressAsync(large, jobs, 4)).GetAwaiter().GetResult();
        Check("zstd L1 jobs", jobs.AsSpan(0, length).ToArray());
        Expect(failures, "zstd L1 jobs", large, Unzstd(new ZstdDecompressor(), jobs.AsSpan(0, length).ToArray(), large.Length));

        foreach (int level in (int[])[-1, 1, 2, 9, 12])
        {
            byte[] block = new byte[Lz4Compressor.GetMaxCompressedLength(text.Length)];
            int size = new Lz4Compressor(level).Compress(text, block);
            Check($"lz4 L{level}", block.AsSpan(0, size).ToArray());
            byte[] content = new byte[text.Length];
            Lz4Block.Decompress(block.AsSpan(0, size), content);
            Expect(failures, $"lz4 L{level}", text, content);
        }

        string where = $"{RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}";
        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"{where}: {failures.Count} failures");
            failures.ForEach(Console.Error.WriteLine);
            return 1;
        }

        Console.WriteLine($"{where}: {(print ? "hashes printed" : "every frame and block as expected")}");
        return 0;
    }

    private static byte[] Zstd(ZstdCompressor compressor, byte[] content)
    {
        byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
        if (compressor.Compress(content, frame, out _, out int written) != OperationStatus.Done)
        {
            throw new InvalidOperationException("compression failed");
        }

        return frame.AsSpan(0, written).ToArray();
    }

    private static byte[] Unzstd(ZstdDecompressor decompressor, byte[] frame, int size)
    {
        byte[] content = new byte[size];
        OperationStatus status = decompressor.Decompress(frame, content, out int consumed, out int written);
        return status == OperationStatus.Done && consumed == frame.Length && written == size ? content : [];
    }

    private static void Expect(List<string> failures, string name, byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            failures.Add(name + ": does not decode back");
        }
    }

    /// <summary>Words drawn by a fixed linear congruential generator: the same bytes everywhere.</summary>
    private static byte[] Text(int size)
    {
        string[] words =
        [
            "the", "frame", "block", "window", "literal", "sequence", "offset", "match", "entropy", "huffman",
            "table", "state", "stream", "content", "dictionary", "checksum", "level", "compress", "of", "and",
        ];

        var text = new StringBuilder(size + 16);
        uint state = 12345;
        while (text.Length < size)
        {
            state = (state * 1103515245) + 12345;
            text.Append(words[(state >> 16) % (uint)words.Length]);
            text.Append((state & 0x300) == 0 ? ".\n" : " ");
        }

        return Encoding.ASCII.GetBytes(text.ToString(0, size));
    }
}
