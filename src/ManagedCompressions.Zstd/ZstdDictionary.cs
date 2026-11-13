using System;
using System.Threading;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>
/// A dictionary prepared once and shared, on any number of threads, by the compressors, decompressors,
/// encoders, decoders and streams created with it: libzstd's <c>ZSTD_CDict</c> and <c>ZSTD_DDict</c> in
/// one, shaped as System.IO.Compression's <c>ZstandardDictionary</c>.
/// </summary>
/// <remarks>
/// <para>
/// A codec given the dictionary's bytes copies them and builds its tables, which a codec per request, or
/// one per thread, does over and over, a copy each. A codec given a <see cref="ZstdDictionary"/> takes its
/// tables as they are: the decoder's, built here, and the compressor's, built by the first compressor that
/// needs them, at <see cref="Level"/>. The frames are the same bytes either way.
/// </para>
/// <para>Immutable, and thread-safe.</para>
/// </remarks>
public sealed class ZstdDictionary
{
    private readonly DecoderDictionary _decoder;
    private CompressionDictionary? _compressor;

    private ZstdDictionary(DecoderDictionary decoder, int level)
    {
        _decoder = decoder;
        Level = level;
    }

    /// <summary>
    /// The level the dictionary is prepared at for compression, which the frames compressed with it take,
    /// as with libzstd's prepared dictionaries.
    /// </summary>
    public int Level { get; }

    /// <summary>The identifier frames name the dictionary by: 0 for raw content.</summary>
    public uint Id => _decoder.Id;

    /// <summary>The dictionary's bytes, as given.</summary>
    public ReadOnlyMemory<byte> Data => _decoder.Buffer.AsMemory(0, _decoder.Length);

    /// <summary>The decoder's tables and history.</summary>
    internal DecoderDictionary Decoder => _decoder;

    /// <summary>
    /// The compressor's tables, prepared at <see cref="Level"/> by the first compressor that needs them:
    /// two threads that get here at once may both prepare them, and one of the two is kept.
    /// </summary>
    internal CompressionDictionary Compressor =>
        Volatile.Read(ref _compressor)
        ?? Interlocked.CompareExchange(ref _compressor, CompressionDictionary.Create(Data.Span, Level), null)
        ?? _compressor!;

    /// <summary>Prepares <paramref name="dictionary"/> at <see cref="ZstdCompressor.DefaultLevel"/>.</summary>
    /// <inheritdoc cref="Create(ReadOnlySpan{byte}, int)"/>
    public static ZstdDictionary Create(ReadOnlySpan<byte> dictionary) => Create(dictionary, ZstdCompressor.DefaultLevel);

    /// <summary>
    /// Prepares <paramref name="dictionary"/>: a dictionary in the zstd format (as zstd's trainer writes
    /// them), or any other bytes as raw content. The bytes are copied.
    /// </summary>
    /// <param name="dictionary">The dictionary.</param>
    /// <param name="level">
    /// The level frames compressed with it take, from <see cref="ZstdCompressor.MinLevel"/> to
    /// <see cref="ZstdCompressor.MaxLevel"/>; 0 is the <see cref="ZstdCompressor.DefaultLevel"/>.
    /// </param>
    /// <exception cref="ArgumentException">The dictionary is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    /// <exception cref="System.IO.InvalidDataException">The dictionary has the zstd format but invalid tables.</exception>
    public static ZstdDictionary Create(ReadOnlySpan<byte> dictionary, int level)
    {
        if (dictionary.IsEmpty)
        {
            throw new ArgumentException("A dictionary has bytes.", nameof(dictionary));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(level, ZstdCompressor.MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, ZstdCompressor.MaxLevel);
        return new ZstdDictionary(DecoderDictionary.Load(dictionary, shared: true), level == 0 ? ZstdCompressor.DefaultLevel : level);
    }
}
