using System;
using System.IO;
using ManagedCompressions.Zstd.Bench;

namespace ManagedCompressions.Benchmarks;

/// <summary>
/// silesia.tar, 211,948,032 bytes, the data of NativeCompressions' README and of zstd's: the twelve
/// files of the Silesia corpus in name order, built from silesia.zip, which bench/zstd-corpus.sh downloads.
/// </summary>
internal static class Silesia
{
    private static byte[]? s_tar;

    public static byte[] Tar => s_tar ??= SilesiaTar.Build(Path.Combine(RepositoryRoot(), "tests", "ManagedCompressions.Zstd.Tests", "testdata", "corpus", "silesia.zip"));

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
