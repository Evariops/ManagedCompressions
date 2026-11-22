namespace ManagedCompressions.Lz4.Tests;

/// <summary>How the code under test is built.</summary>
internal static class TestBuild
{
    /// <summary>
    /// Whether the libraries are built in Debug, with their assertions on: their allocations are then
    /// not those of the build that ships. A field, not a constant, so that a test skipped on it still
    /// compiles whole.
    /// </summary>
    public static readonly bool Debug =
#if DEBUG
        true;
#else
        false;
#endif
}
