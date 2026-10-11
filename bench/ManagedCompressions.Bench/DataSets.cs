using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ManagedCompressions.Bench;

/// <summary>A data set: its content, where each record starts (a record is a frame or a block of its own), and its dictionary.</summary>
internal sealed record DataSet(string Name, string Title, byte[] Content, int[] Offsets, byte[]? Dictionary)
{
    public int Records => Offsets.Length - 1;

    public ReadOnlySpan<byte> Record(int i) => Content.AsSpan(Offsets[i], Offsets[i + 1] - Offsets[i]);
}

/// <summary>
/// The corpora of zstd's own benchmarks, which <c>bench/zstd-corpus.sh</c> downloads: the Silesia
/// corpus and the 500 JSON records of zstd's regression tests, with their dictionary.
/// </summary>
internal static class DataSets
{
    public static readonly string[] Names = ["silesia", "silesia-64k", "silesia-sample", "github", "github-dict"];

    public static string Directory { get; } = Path.Combine(RepositoryRoot(), "tests", "ManagedCompressions.Zstd.Tests", "testdata", "corpus");

    public static DataSet Load(string name)
    {
        switch (name)
        {
            case "silesia":
            {
                byte[] tar = SilesiaTar();
                return new DataSet(name, $"silesia.tar, {tar.Length:N0} bytes, one frame or block", tar, [0, tar.Length], null);
            }

            case "silesia-64k":
            {
                // Pages of 64 KiB, each a frame or a block of its own: what a columnar file or a
                // network protocol compresses.
                byte[] tar = SilesiaTar();
                const int Page = 64 << 10;
                var offsets = new List<int>();
                for (int at = 0; at < tar.Length; at += Page)
                {
                    offsets.Add(at);
                }

                offsets.Add(tar.Length);
                return new DataSet(name, $"silesia.tar in {offsets.Count - 1:N0} pages of 64 KiB, each its own frame or block", tar, offsets.ToArray(), null);
            }

            case "silesia-sample":
            {
                // 1 MiB every 16 MiB of silesia.tar, as one frame: its kinds of data in a fraction of the time.
                byte[] tar = SilesiaTar();
                const int Slice = 1 << 20;
                const int Stride = 16 << 20;
                using var sample = new MemoryStream();
                for (int at = 0; at + Slice <= tar.Length; at += Stride)
                {
                    sample.Write(tar, at, Slice);
                }

                byte[] content = sample.ToArray();
                return new DataSet(name, $"1 MiB every 16 MiB of silesia.tar, {content.Length:N0} bytes, one frame or block", content, [0, content.Length], null);
            }

            case "github":
            case "github-dict":
            {
                (byte[] content, int[] offsets) = Records(Path.Combine(Directory, "github.tar.zst"));
                byte[]? dictionary = name == "github-dict" ? Unzstd(Path.Combine(Directory, "github.dict.zst")) : null;
                string title = $"{offsets.Length - 1} JSON records, {content.Length:N0} bytes, a frame or block each" +
                    (dictionary is null ? string.Empty : $", with github.dict ({dictionary.Length:N0} bytes)");
                return new DataSet(name, title, content, offsets, dictionary);
            }

            default:
                throw new ArgumentException($"unknown data set {name} ({string.Join(", ", Names)})", nameof(name));
        }
    }

    private static byte[]? s_silesia;

    /// <summary>silesia.tar, as lzbench reads it: the twelve files in name order, in a ustar archive whose headers depend on nothing but the files.</summary>
    private static byte[] SilesiaTar()
    {
        if (s_silesia is not null)
        {
            return s_silesia;
        }

        using ZipArchive zip = ZipFile.OpenRead(Path.Combine(Directory, "silesia.zip"));
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                using var data = new MemoryStream();
                using (Stream source = entry.Open())
                {
                    source.CopyTo(data);
                }

                data.Position = 0;
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "silesia/" + entry.FullName)
                {
                    DataStream = data,
                    ModificationTime = DateTimeOffset.UnixEpoch,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                });
            }
        }

        return s_silesia = tar.ToArray();
    }

    /// <summary>The regular files of a .tar.zst, one after the other, and where each starts.</summary>
    private static (byte[] Content, int[] Offsets) Records(string path)
    {
        using var content = new MemoryStream();
        var offsets = new List<int> { 0 };
        using (var reader = new TarReader(new MemoryStream(Unzstd(path))))
        {
            while (reader.GetNextEntry() is TarEntry entry)
            {
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is Stream data)
                {
                    data.CopyTo(content);
                    offsets.Add(checked((int)content.Length));
                }
            }
        }

        return (content.ToArray(), offsets.ToArray());
    }

    private static byte[] Unzstd(string path)
    {
        using FileStream file = File.OpenRead(path);
        using var zstd = new ZstandardStream(file, CompressionMode.Decompress);
        using var content = new MemoryStream();
        zstd.CopyTo(content);
        return content.ToArray();
    }

    private static string RepositoryRoot()
    {
        for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
        {
            if (File.Exists(Path.Combine(at, "ManagedCompressions.slnx")))
            {
                return at;
            }
        }

        return System.IO.Directory.GetCurrentDirectory();
    }
}
