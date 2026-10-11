using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ManagedCompressions.Benchmarks;

/// <summary>
/// silesia.tar, 211,948,032 bytes, the data of NativeCompressions' README and of zstd's: the twelve
/// files of the Silesia corpus in name order, built from silesia.zip, which bench/zstd-corpus.sh downloads.
/// </summary>
internal static class Silesia
{
    private static byte[]? s_tar;

    public static byte[] Tar => s_tar ??= Build();

    private static byte[] Build()
    {
        using ZipArchive zip = ZipFile.OpenRead(Path.Combine(RepositoryRoot(), "tests", "ManagedCompressions.Zstd.Tests", "testdata", "corpus", "silesia.zip"));
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

        return tar.ToArray();
    }

    private static string RepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (string? at = start; at is not null; at = Path.GetDirectoryName(at))
            {
                if (File.Exists(Path.Combine(at, "ManagedCompressions.slnx")))
                {
                    return at;
                }
            }
        }

        throw new InvalidOperationException("ManagedCompressions.slnx not found above " + AppContext.BaseDirectory + " nor the current directory");
    }
}
