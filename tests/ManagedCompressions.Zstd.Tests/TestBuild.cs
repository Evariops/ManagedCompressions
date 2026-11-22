namespace ManagedCompressions.Zstd.Tests;

/// <summary>How the code under test is built.</summary>
internal static class TestBuild
{
    /// <summary>
    /// Whether the libraries are built with their assertions on (Debug, Checked): their allocations
    /// and their memory are then not those of the build that ships. A field, not a constant, so that
    /// a test skipped on it still compiles whole.
    /// </summary>
    public static readonly bool Debug =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Whether the libraries are the Checked build: their unchecked memory guarded by inaccessible pages.</summary>
    public static readonly bool Checked =
#if CHECKED
        true;
#else
        false;
#endif
}
