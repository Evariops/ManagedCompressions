using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ManagedCompressions.Zstd.Bench;

/// <summary>silesia.tar, built from silesia.zip, which bench/zstd-corpus.sh downloads: for every bench.</summary>
internal static class SilesiaTar
{
    /// <summary>
    /// silesia.tar, as lzbench reads it: the twelve files in name order, in a ustar archive whose
    /// headers depend on nothing but the files.
    /// </summary>
    public static byte[] Build(string zipPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
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
}
