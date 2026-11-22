using ManagedCompressions.Brotli.Tests.Support;
using Xunit;

namespace ManagedCompressions.Brotli.Tests;

/// <summary>
/// The oracle itself. It must be brotli 1.1.1, whose streams the compressor writes and whose checks
/// the decoder makes: another version of .NET's native library fails here, rather than in every
/// differential test at once.
/// </summary>
public sealed class OracleTests
{
    [Fact]
    public void TheEncoderIsBrotli1_1_1() => Assert.Equal(0x1001001u, NativeBrotli.EncoderVersion);

    [Fact]
    public void TheDecoderIsBrotli1_1_1() => Assert.Equal(0x1001001u, NativeBrotli.DecoderVersion);
}
