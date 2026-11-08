using NativeCompressions.Interop;
using Xunit;

namespace ManagedCompressions.Lz4.Tests;

/// <summary>
/// The oracle itself. It must be lz4 1.10.0, the version whose blocks the compressors write: another
/// version (NativeCompressions 1.3.0 ships lz4 1.11.0, whose bytes differ at every level) fails here,
/// rather than in every differential test at once.
/// </summary>
public sealed class OracleTests
{
    [Fact]
    public void TheOracleIsLz4Version1_10_0() => Assert.Equal(11000, (int)LZ4NativeMethods.LZ4_versionNumber());
}
