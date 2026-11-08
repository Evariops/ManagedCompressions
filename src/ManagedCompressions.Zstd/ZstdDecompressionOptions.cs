using System;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>
/// What a <see cref="ZstdDecoder"/> is created with: the largest window it accepts, and a dictionary.
/// The names follow System.IO.Compression's <c>ZstandardDecompressionOptions</c>.
/// </summary>
public sealed class ZstdDecompressionOptions
{
    /// <summary>libzstd's default for <c>ZSTD_d_windowLogMax</c>: windows of up to 128 MiB.</summary>
    public const int DefaultMaxWindowLog = 27;

    /// <summary>The smallest window the format has, 1 KiB.</summary>
    public const int MinWindowLog = FrameFormat.WindowLogMin;

    /// <summary>
    /// The largest window a decoder can accept, 1 GiB. The format allows 2 GiB, but the window lives in
    /// one array, which .NET caps below that.
    /// </summary>
    public const int WindowLogLimit = 30;

    private int _maxWindowLog = DefaultMaxWindowLog;

    /// <summary>
    /// The largest window a frame may declare, as a power of two, from <see cref="MinWindowLog"/> to
    /// <see cref="WindowLogLimit"/>; <see cref="DefaultMaxWindowLog"/> unless set. It bounds the
    /// decoder's memory: a frame's window, and a block, at most. A frame whose content size is
    /// declared and fits in the destination escapes it, as in libzstd, since it is decoded in place.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
    public int MaxWindowLog
    {
        get => _maxWindowLog;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, MinWindowLog);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, WindowLogLimit);
            _maxWindowLog = value;
        }
    }

    /// <summary>
    /// A dictionary in the zstd format, or any other bytes as raw content; empty for none. The decoder
    /// copies it when it is created.
    /// </summary>
    public ReadOnlyMemory<byte> Dictionary { get; set; }
}
