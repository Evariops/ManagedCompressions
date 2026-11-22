namespace ManagedCompressions.Brotli;

/// <summary>What the input is, which tunes the compressor: brotli's <c>BrotliEncoderMode</c>.</summary>
public enum BrotliMode
{
    /// <summary>Nothing is assumed about the input: brotli's default, and .NET's.</summary>
    Generic = 0,

    /// <summary>UTF-8 text.</summary>
    Text = 1,

    /// <summary>WOFF 2.0 fonts.</summary>
    Font = 2,
}
