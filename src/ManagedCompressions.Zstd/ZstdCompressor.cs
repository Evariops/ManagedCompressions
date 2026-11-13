using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>
/// Compresses data into Zstandard frames (RFC 8878) in fully managed code.
/// </summary>
/// <remarks>
/// <para>
/// The frames are those libzstd 1.5.7 writes at the same level (<c>ZSTD_compress</c>), byte for byte:
/// the same parameters, match finders, block splits and entropy decisions, at every level, long-distance
/// matching included (libzstd turns it on at level 22 for sources over 64 MiB); with a dictionary, those
/// of libzstd's prepared dictionaries (see <see cref="ZstdCompressor(int, ReadOnlySpan{byte})"/>).
/// </para>
/// <para>
/// An instance keeps its tables and buffers from one frame to the next, so that a warm compressor
/// allocates nothing; nor does it clear its tables between frames, whose indices follow each other.
/// It is not thread-safe: use one per thread.
/// </para>
/// <para>
/// <see cref="Compress"/> compresses its whole source into one frame, which declares its size.
/// </para>
/// </remarks>
public sealed unsafe partial class ZstdCompressor
{
    /// <summary>The fastest level: libzstd's <c>ZSTD_minCLevel()</c>.</summary>
    public const int MinLevel = CompressionParameters.MinLevel;

    /// <summary>The strongest level: libzstd's <c>ZSTD_maxCLevel()</c>.</summary>
    public const int MaxLevel = CompressionParameters.MaxLevel;

    /// <summary>libzstd's <c>ZSTD_CLEVEL_DEFAULT</c>.</summary>
    public const int DefaultLevel = CompressionParameters.DefaultLevel;

    /// <summary>libzstd's <c>ZSTD_FRAMEHEADERSIZE_MAX</c>.</summary>
    private const int FrameHeaderSizeMax = 18;

    /// <summary>
    /// libzstd's <c>ZSTD_CURRENT_MAX</c> less its <c>ZSTD_INDEXOVERFLOW_MARGIN</c>: the indices
    /// restart, and the tables are cleared, before a frame would reach it.
    /// </summary>
    private const ulong IndexLimit = (3500UL << 20) - (16UL << 20);

    /// <summary>libzstd's <c>ZSTD_WINDOW_START_INDEX</c>: the index of the first byte after a restart.</summary>
    private const uint WindowStartIndex = 2;

    /// <summary>Creates a compressor at the <see cref="DefaultLevel"/>.</summary>
    public ZstdCompressor()
        : this(DefaultLevel)
    {
    }

    /// <summary>Creates a compressor at <paramref name="level"/>.</summary>
    /// <param name="level">From <see cref="MinLevel"/> to <see cref="MaxLevel"/>; 0 is the <see cref="DefaultLevel"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    /// <exception cref="PlatformNotSupportedException">The processor is big-endian: see the README.</exception>
    public ZstdCompressor(int level)
    {
        LittleEndian.Require();
        ArgumentOutOfRangeException.ThrowIfLessThan(level, MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, MaxLevel);
        Level = level == 0 ? DefaultLevel : level;

        // The small pinned tables, carved out of one array.
        _slab = new PinnedSlab(
            (2 * BlockState.SlabSize) + HuffmanWorkspace.SlabSize + SequenceStore.SlabSize
            + PinnedSlab.SizeOf<uint>(LazyMatchFinder.RowHashCacheSize) + PinnedSlab.SizeOf<uint>(LazyMatchFinder.MaxRowCandidates)
            + PinnedSlab.SizeOf<uint>(SplitCountsSize) + PinnedSlab.SizeOf<uint>(MaxBlockSplits + 1) + PinnedSlab.SizeOf<byte>(SplitScratchSize));
        _previous = new BlockState(_slab);
        _next = new BlockState(_slab);
        _huffmanWorkspace = new HuffmanWorkspace(_slab);
        _store = new SequenceStore(_slab);
        _hashCache = _slab.Take<uint>(LazyMatchFinder.RowHashCacheSize);
        _candidates = _slab.Take<uint>(LazyMatchFinder.MaxRowCandidates);
        _splitCounts = _slab.Take<uint>(SplitCountsSize);
        _partitions = _slab.Take<uint>(MaxBlockSplits + 1);
        _splitScratch = _slab.Take<byte>(SplitScratchSize);
    }

    /// <summary>
    /// A compressor at <paramref name="level"/> with a window of 2^<paramref name="windowLog"/> bytes, 0
    /// for the level's: <see cref="ZstdCompressor(ZstdCompressionOptions)"/> with those two options,
    /// without the options object.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The level or the window is out of range.</exception>
    internal ZstdCompressor(int level, int windowLog)
        : this(level)
    {
        if (windowLog != 0)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(windowLog, ZstdCompressionOptions.MinWindowLog);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(windowLog, ZstdCompressionOptions.MaxWindowLog);
        }

        _windowLog = windowLog;
    }

    /// <summary>
    /// Creates a compressor at <paramref name="level"/> with experimental features: its frames are
    /// standard Zstandard frames, which any decoder reads back, but not libzstd's bytes (see
    /// <see cref="ZstdExperimentalOptions"/>).
    /// </summary>
    /// <param name="level">From <see cref="MinLevel"/> to <see cref="MaxLevel"/>; 0 is the <see cref="DefaultLevel"/>.</param>
    /// <param name="experimental">The features, named explicitly; <see cref="ZstdExperimentalOptions.None"/> is <see cref="ZstdCompressor(int)"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range, or a feature is unknown.</exception>
    public ZstdCompressor(int level, ZstdExperimentalOptions experimental)
        : this(level)
    {
        if ((experimental & ~ZstdExperimentalOptions.EntropyAwareMatches) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(experimental), experimental, "Unknown experimental feature.");
        }

        Experimental = experimental;
    }

    /// <summary>
    /// Creates a compressor with <paramref name="options"/>: a level, and libzstd's parameters beyond
    /// it, the frames still libzstd's bytes with the same parameters.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Experimental features asked for with a dictionary.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An experimental feature is unknown.</exception>
    /// <exception cref="System.IO.InvalidDataException">A zstd-format dictionary whose tables are invalid.</exception>
    /// <exception cref="PlatformNotSupportedException">The processor is big-endian: see the README.</exception>
    public ZstdCompressor(ZstdCompressionOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).PreparedDictionary?.Level ?? options.Level, options.Experimental)
    {
        bool dictionary = !options.Dictionary.IsEmpty || options.PreparedDictionary is not null;
        if (dictionary && options.Experimental != ZstdExperimentalOptions.None)
        {
            throw new ArgumentException("The experimental features are not available with a dictionary.", nameof(options));
        }

        if (!options.Dictionary.IsEmpty && options.PreparedDictionary is not null)
        {
            throw new ArgumentException("A dictionary is given as bytes or prepared, not both.", nameof(options));
        }

        AppendChecksum = options.AppendChecksum;
        LongDistanceMatching = options.LongDistanceMatching == true;
        _noLongDistanceMatching = options.LongDistanceMatching == false;
        _windowLog = options.WindowLog;
        _writeContentSize = options.WriteContentSize;
        _writeDictionaryId = options.WriteDictionaryId;
        if (options.PreparedDictionary is { } prepared)
        {
            _dictionary = prepared.Compressor;
        }
        else if (!options.Dictionary.IsEmpty)
        {
            _dictionary = CompressionDictionary.Create(options.Dictionary.Span, Level);
        }
    }

    /// <summary>The compression level.</summary>
    public int Level { get; }

    /// <summary>The experimental features in use: <see cref="ZstdExperimentalOptions.None"/> for libzstd's bytes.</summary>
    public ZstdExperimentalOptions Experimental { get; }

    /// <summary>Whether the frames end with a checksum of their content (the low 32 bits of its XXH64).</summary>
    public bool AppendChecksum { get; set; }

    /// <summary>
    /// Turns libzstd's long-distance matcher on whatever the source, as <c>ZSTD_c_enableLongDistanceMatching</c>
    /// does, so that the tests compare it with libzstd's on small sources. Only the optimal parsers
    /// (levels 16 and up) take long-distance matches.
    /// </summary>
    internal bool LongDistanceMatching { get; set; }

    /// <summary>The long-distance matcher kept off, as <c>ZSTD_c_enableLongDistanceMatching</c> at <c>ZSTD_ps_disable</c>.</summary>
    private readonly bool _noLongDistanceMatching;

    /// <summary>The window asked for, as <c>ZSTD_c_windowLog</c>; 0 for the level's.</summary>
    private readonly int _windowLog;

    /// <summary>Whether the frame header declares the content's size: <c>ZSTD_c_contentSizeFlag</c>.</summary>
    private readonly bool _writeContentSize = true;

    /// <summary>Whether the frame header names the dictionary: <c>ZSTD_c_dictIDFlag</c>.</summary>
    private readonly bool _writeDictionaryId = true;

    /// <summary>
    /// libzstd's <c>ZSTD_resolveEnableLdm</c>: whether the long-distance matcher runs on a frame of
    /// these parameters, asked for, kept off, or else as libzstd turns it on by itself.
    /// </summary>
    private bool UsesLongDistanceMatcher(in CompressionParameters parameters) =>
        !_noLongDistanceMatching && (LongDistanceMatching || LongDistanceMatcher.EnabledFor(parameters));

    /// <summary>
    /// The largest frame <see cref="Compress"/> writes for <paramref name="sourceLength"/> bytes:
    /// libzstd's <c>ZSTD_compressBound</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The bound would not fit an <see cref="int"/>.</exception>
    public static int GetMaxCompressedLength(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        long bound = sourceLength + ((long)sourceLength >> 8)
            + (sourceLength < FrameFormat.MaxBlockSize ? (FrameFormat.MaxBlockSize - sourceLength) >> 11 : 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bound, int.MaxValue, nameof(sourceLength));
        return (int)bound;
    }

    /// <summary>Compresses <paramref name="source"/> into one frame at the start of <paramref name="destination"/>.</summary>
    /// <param name="source">The content of the frame.</param>
    /// <param name="destination">Where the frame is written; <see cref="GetMaxCompressedLength"/> bytes always suffice.</param>
    /// <param name="bytesConsumed">The size of the source once it is compressed; otherwise 0.</param>
    /// <param name="bytesWritten">The size of the frame once it is written; otherwise 0.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the frame is written;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when it does not fit, the destination then
    /// holding an unspecified part of it.
    /// </returns>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        int written;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            written = CompressFrame(src, source.Length, dst, destination.Length);
        }

        if (written < 0)
        {
            return OperationStatus.DestinationTooSmall;
        }

        bytesConsumed = source.Length;
        bytesWritten = written;
        return OperationStatus.Done;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_compress</c>: the frame header, the blocks, the checksum.
    /// </summary>
    /// <returns>The size of the frame, or -1 when it does not fit.</returns>
    private int CompressFrame(byte* source, int sourceSize, byte* destination, int capacity)
    {
        CompressionParameters parameters;
        uint dictionaryId = 0;
        if (_dictionary is null)
        {
            parameters = CompressionParameters.ForFrame(Level, sourceSize, LongDistanceMatching, _windowLog);
            if (LongDistanceMatching && parameters.Strategy < Strategy.BinaryTreeOptimal)
            {
                throw new NotSupportedException("Long-distance matching is implemented for the optimal parsers only.");
            }

            BeginFrame(parameters, source, sourceSize, UsesLongDistanceMatcher(parameters));
        }
        else
        {
            parameters = BeginDictionaryFrame(source, sourceSize);
            dictionaryId = _dictionary.Id;
        }

        byte* header = stackalloc byte[FrameHeaderSizeMax];
        int headerSize = WriteFrameHeader(
            header, parameters.WindowLog, (ulong)sourceSize, AppendChecksum, dictionaryId, _writeContentSize, _writeDictionaryId);
        if (headerSize > capacity)
        {
            return -1;
        }

        Unsafe.CopyBlockUnaligned(destination, header, (uint)headerSize);
        int op = headerSize;
        if (sourceSize == 0)
        {
            // libzstd's ZSTD_writeEpilogue: a last block, raw and empty.
            if (capacity - op < FrameFormat.BlockHeaderSize)
            {
                return -1;
            }

            WriteBlockHeader(destination + op, 1, 0);
            op += FrameFormat.BlockHeaderSize;
        }
        else
        {
            int blocks = CompressBlocks(source, sourceSize, destination + op, capacity - op);
            if (blocks < 0)
            {
                return -1;
            }

            op += blocks;
        }

        if (AppendChecksum)
        {
            if (capacity - op < FrameFormat.ChecksumSize)
            {
                return -1;
            }

            uint checksum = (uint)XxHash64.HashToUInt64(new ReadOnlySpan<byte>(source, sourceSize));
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(destination + op, FrameFormat.ChecksumSize), checksum);
            op += FrameFormat.ChecksumSize;
        }

        return op;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_writeFrameHeader</c>: a single segment when the frame declares its content size
    /// and the window covers it all, else the window; the dictionary's identifier, if any and asked
    /// for, in as few bytes as it takes; the content size, if asked for.
    /// </summary>
    /// <returns>The size of the header.</returns>
    private static int WriteFrameHeader(
        byte* header, int windowLog, ulong contentSize, bool checksum, uint dictionaryId, bool contentSizeFlag = true, bool dictionaryIdFlag = true)
    {
        ulong windowSize = 1UL << windowLog;
        bool singleSegment = contentSizeFlag && windowSize >= contentSize;
        int contentSizeCode = !contentSizeFlag ? 0
            : (contentSize >= 256 ? 1 : 0) + (contentSize >= 65536 + 256 ? 1 : 0) + (contentSize >= 0xFFFFFFFFUL ? 1 : 0);
        int dictionaryIdCode = !dictionaryIdFlag ? 0
            : (dictionaryId > 0 ? 1 : 0) + (dictionaryId >= 256 ? 1 : 0) + (dictionaryId >= 65536 ? 1 : 0);
        int descriptor = dictionaryIdCode | ((checksum ? 1 : 0) << 2) | ((singleSegment ? 1 : 0) << 5) | (contentSizeCode << 6);
        Unsafe.WriteUnaligned(header, FrameFormat.Magic);
        header[4] = (byte)descriptor;
        int position = 5;
        if (!singleSegment)
        {
            header[position++] = (byte)((windowLog - FrameFormat.WindowLogMin) << 3);
        }

        switch (dictionaryIdCode)
        {
            case 1:
                header[position++] = (byte)dictionaryId;
                break;
            case 2:
                Unsafe.WriteUnaligned(header + position, (ushort)dictionaryId);
                position += 2;
                break;
            case 3:
                Unsafe.WriteUnaligned(header + position, dictionaryId);
                position += 4;
                break;
        }

        switch (contentSizeCode)
        {
            case 0:
                if (singleSegment)
                {
                    header[position++] = (byte)contentSize;
                }

                break;
            case 1:
                Unsafe.WriteUnaligned(header + position, (ushort)(contentSize - 256));
                position += 2;
                break;
            case 2:
                Unsafe.WriteUnaligned(header + position, (uint)contentSize);
                position += 4;
                break;
            default:
                Unsafe.WriteUnaligned(header + position, contentSize);
                position += 8;
                break;
        }

        return position;
    }

    /// <summary>A block header: whether it is the last, its type, and its size.</summary>
    private static void WriteBlockHeader(byte* output, int lastBlock, int typeAndSize)
    {
        uint value = (uint)(lastBlock | typeAndSize);
        output[0] = (byte)value;
        output[1] = (byte)(value >> 8);
        output[2] = (byte)(value >> 16);
    }
}
