using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace ManagedCompressions.Brotli.Tests.Support;

/// <summary>
/// <c>tests/ManagedCompressions.Brotli.Tests/testdata/</c>: brotli's test data at v1.1.1, which the
/// repository does not keep and <c>tools/native-ref/brotli-testdata.sh</c> writes.
/// </summary>
/// <remarks>
/// A test that reads it calls <see cref="Require"/> first: without it the test is skipped, and in CI,
/// whose workflow writes it before the tests, it fails, so that a run without the data never passes for
/// one that read it.
/// </remarks>
internal static class TestData
{
    /// <summary>What a data set's theory receives in place of its cases when the set is missing.</summary>
    public const string Missing = "(not generated)";

    private static readonly Lazy<string> Root = new(() => FindRoot());

    public static string Directory => Path.Combine(Root.Value, "tests", "ManagedCompressions.Brotli.Tests", "testdata");

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Directory, name));

    public static bool Has(string name) => File.Exists(Path.Combine(Directory, name));

    /// <summary>Skips the test when <paramref name="name"/> is missing, and fails it in CI.</summary>
    public static void Require(string name)
    {
        if (Has(name))
        {
            return;
        }

        string message = $"{name} is missing from {Directory}: run tools/native-ref/brotli-testdata.sh";
        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            Assert.Fail(message);
        }

        Assert.Skip(message);
    }

    /// <summary>The files whose name matches <paramref name="pattern"/>, in order; <see cref="Missing"/> alone without the data.</summary>
    public static IEnumerable<string> Files(string pattern) =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.GetFiles(Directory, pattern).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)
            : [Missing];

    private static string FindRoot([CallerFilePath] string here = "")
    {
        string? directory = Path.GetDirectoryName(here);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "ManagedCompressions.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("ManagedCompressions.slnx not found above " + here);
    }
}
