using System;

namespace ManagedCompressions.Zstd;

/// <summary>
/// What a <see cref="ZstdCompressor"/> is created with: a level, and libzstd's parameters beyond it.
/// Each one is libzstd's own, and the frames stay libzstd's, byte for byte, as <c>ZSTD_compress2</c>
/// writes them with the same parameters set (<c>ZSTD_CCtx_setParameter</c>). The names follow
/// System.IO.Compression's <c>ZstandardCompressionOptions</c> where it has them.
/// </summary>
public sealed class ZstdCompressionOptions
{
    /// <summary>The smallest window the format has: 1 KiB, libzstd's <c>ZSTD_WINDOWLOG_MIN</c>.</summary>
    public const int MinWindowLog = 10;

    /// <summary>The largest window libzstd takes on 64-bit machines: 2 GiB, <c>ZSTD_WINDOWLOG_MAX_64</c>.</summary>
    public const int MaxWindowLog = 31;

    private int _level = ZstdCompressor.DefaultLevel;
    private int _windowLog;

    /// <summary>
    /// The level, from <see cref="ZstdCompressor.MinLevel"/> to <see cref="ZstdCompressor.MaxLevel"/>;
    /// 0 is <see cref="ZstdCompressor.DefaultLevel"/>, which it is unless set. With a dictionary, the
    /// dictionary is prepared at this level, and the frames take it, as libzstd's prepared dictionaries do.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    public int Level
    {
        get => _level;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, ZstdCompressor.MinLevel);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, ZstdCompressor.MaxLevel);
            _level = value;
        }
    }

    /// <summary>
    /// The window, as a power of two (<c>ZSTD_c_windowLog</c>): how far back a match may reach, and what
    /// a decoder must hold. 0, the default, takes the level's; otherwise from <see cref="MinWindowLog"/>
    /// to <see cref="MaxWindowLog"/>. libzstd still shrinks it to the source, and fits its tables to it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is neither 0 nor in range.</exception>
    public int WindowLog
    {
        get => _windowLog;
        set
        {
            if (value != 0)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value, MinWindowLog);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxWindowLog);
            }

            _windowLog = value;
        }
    }

    /// <summary>Whether each frame ends with a checksum of its content (<c>ZSTD_c_checksumFlag</c>).</summary>
    public bool AppendChecksum { get; set; }

    /// <summary>
    /// libzstd's long-distance matcher (<c>ZSTD_c_enableLongDistanceMatching</c>): null, the default,
    /// lets libzstd decide, which turns it on at level 22 for sources over 64 MiB; true turns it on,
    /// with a window of 128 MiB unless <see cref="WindowLog"/> says otherwise; false keeps it off. On,
    /// it is implemented for the optimal parsers only, levels 16 and up on large sources: elsewhere
    /// <see cref="ZstdCompressor.Compress"/> throws <see cref="NotSupportedException"/>.
    /// </summary>
    public bool? LongDistanceMatching { get; set; }

    /// <summary>
    /// Whether the frame header declares the content's size (<c>ZSTD_c_contentSizeFlag</c>); true by
    /// default. Without it the header holds the window instead, which a decoder then has to allocate.
    /// </summary>
    public bool WriteContentSize { get; set; } = true;

    /// <summary>
    /// Whether a frame compressed with a dictionary in the zstd format names it in its header
    /// (<c>ZSTD_c_dictIDFlag</c>); true by default. Without it a decoder cannot check that it holds the
    /// right dictionary.
    /// </summary>
    public bool WriteDictionaryId { get; set; } = true;

    /// <summary>
    /// A dictionary in the zstd format, or any other bytes as raw content; empty for none. It is copied
    /// and prepared when the compressor is created.
    /// </summary>
    public ReadOnlyMemory<byte> Dictionary { get; set; }

    /// <summary>
    /// Experimental features, which trade libzstd's exact bytes for speed or ratio (see
    /// <see cref="ZstdExperimentalOptions"/>); not available with a dictionary.
    /// </summary>
    public ZstdExperimentalOptions Experimental { get; set; }
}
