using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>
/// Decompresses Zstandard frames (RFC 8878) in fully managed code.
/// </summary>
/// <remarks>
/// <para>
/// An instance keeps its tables and buffers from one frame to the next, so that a warm decoder
/// allocates nothing. It is not thread-safe: use one per thread.
/// </para>
/// <para>
/// Every block must regenerate at most the frame's <c>Block_Maximum_Size</c>, as RFC 8878 states.
/// libzstd enforces this when it streams and only partly in one shot; ManagedCompressions.Zstd always
/// does.
/// </para>
/// <para>
/// <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/> decodes one whole frame
/// per call, from the start of the source into the start of the destination. When the source ends
/// before the frame does, or the destination cannot hold it, it reports so without consuming or
/// writing anything: call again with the whole frame, or with a larger destination.
/// </para>
/// <para>
/// A dictionary is given to the constructor, which copies it and builds its tables once, or with
/// each frame (<see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, ReadOnlySpan{byte}, out int, out int)"/>):
/// one decompressor then serves frames of any dictionary, building a dictionary's tables once for
/// as long as the frames give the same.
/// </para>
/// </remarks>
public sealed partial class ZstdDecompressor
{
    private readonly DecoderDictionary? _dictionary;

    /// <summary>The tables of the dictionaries given with frames, rebuilt in place when the dictionary changes.</summary>
    private DictionaryEntropy? _callEntropy;

    /// <summary>The bytes <see cref="_callEntropy"/> was built from: magic number, identifier, tables and repeat offsets.</summary>
    private byte[] _callEntropyKey = [];

    /// <summary>How many of <see cref="_callEntropyKey"/>'s bytes are its; -1 while <see cref="_callEntropy"/> holds no usable tables.</summary>
    private int _callEntropyLength = -1;

    /// <summary>The length of the dictionary <see cref="_callEntropy"/> was built from, whose content its repeat offsets were checked against.</summary>
    private int _callDictionaryLength;

    /// <summary>
    /// The fast loops' <c>HistoryGuard</c> for the frame being decoded: <see cref="DecoderDictionary.Margin"/>
    /// when its history is the dictionary given with it, read in place, which has no room after it;
    /// 0 when the history has that room (the constructor's dictionary, a stream's window) or is empty.
    /// </summary>
    private nint _historyGuard;

    /// <summary>
    /// The largest dictionary given with the frame that is copied, with <see cref="DecoderDictionary.Margin"/>
    /// bytes of room after it, rather than read in place.
    /// </summary>
    /// <remarks>
    /// Read in place, a dictionary leaves to the careful path the rest of a block from its first match
    /// that ends within the margin of the dictionary's end, and zstd's trainer puts the content it values
    /// most at that end: the smaller the dictionary, the more blocks meet such a match early. Copied, it
    /// costs its weight at every frame, which weighs the most on the smallest frames. Measured on the 500
    /// github records, a frame each of 0.8 KiB on average, with dictionaries trained on them: copied, one
    /// of 256 bytes to 1.5 KiB decodes 1.4 to 1.7x faster, one of 1.75 to 2 KiB even (0.98 to 1.015),
    /// one of 2.5 to 4 KiB 1.1 to 1.25x slower. On frames of 8 KiB the copy still wins at 16 KiB (1.4x); a column of
    /// 64-bit integers in frames of 8 KiB with a dictionary of 319 bytes decodes 1.96x faster copied.
    /// </remarks>
    internal const int CopiedDictionaryMax = 2048;

    /// <summary>
    /// The dictionary given with the frame, copied with <see cref="DecoderDictionary.Margin"/> bytes of
    /// room after it when it is no larger than <see cref="CopiedDictionaryMax"/>: grown, never shrunk.
    /// </summary>
    private byte[] _callHistory = [];

    /// <summary>
    /// Whether the call fills its destination with the start of the frame's content and stops there:
    /// <see cref="DecompressPrefix"/>'s, which every other call leaves false.
    /// </summary>
    private bool _prefix;

    /// <summary>Creates a decoder without a dictionary.</summary>
    /// <exception cref="PlatformNotSupportedException">The processor is big-endian: see the README.</exception>
    public ZstdDecompressor()
    {
        LittleEndian.Require();
        _huffman = new HuffmanTable();
        _sequenceTables = new SequenceTableSet();
        _currentHuffman = _huffman;
    }

    /// <summary>
    /// Creates a decoder for frames compressed with <paramref name="dictionary"/>: a dictionary in the
    /// zstd format, or any other bytes as raw content. The bytes are copied.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">The dictionary has the zstd format but invalid tables.</exception>
    public ZstdDecompressor(ReadOnlySpan<byte> dictionary)
        : this()
    {
        if (!dictionary.IsEmpty)
        {
            _dictionary = DecoderDictionary.Load(dictionary);
        }
    }

    /// <summary>
    /// Creates a decoder for frames compressed with <paramref name="dictionary"/>, prepared once and
    /// shared: its tables taken as they are.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="dictionary"/> is null.</exception>
    public ZstdDecompressor(ZstdDictionary dictionary)
        : this()
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        _dictionary = dictionary.Decoder;
    }

    /// <summary>The memory the decompressor holds on to beyond its tables, roughly: its literals and records, a copied small dictionary.</summary>
    internal long RetainedBytes => (long)_literals.Length + (8L * (_records?.Length ?? 0)) + _callHistory.Length;

    /// <summary>
    /// Why the last call refused its frame: the check that failed, where the status it returned says
    /// what to do next. <see cref="ZstdError.None"/> after a call that returned
    /// <see cref="OperationStatus.Done"/>.
    /// </summary>
    public ZstdError LastError { get; private set; }

    /// <summary>
    /// Decompresses the frame that starts <paramref name="source"/> into the start of
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">A zstd or skippable frame, possibly followed by other data, which is left alone.</param>
    /// <param name="destination">Where the frame's content is written.</param>
    /// <param name="bytesConsumed">The size of the frame, once it is decoded; otherwise 0.</param>
    /// <param name="bytesWritten">The size of the frame's content, once it is decoded; otherwise 0.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the frame is decoded;
    /// <see cref="OperationStatus.NeedMoreData"/> when the source ends inside the frame;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when the content does not fit;
    /// <see cref="OperationStatus.InvalidData"/> when the frame is not valid zstd.
    /// </returns>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        _historyGuard = 0;
        try
        {
            int written;
            int consumed;
            DecoderDictionary? dictionary = _dictionary;
            if (dictionary is null)
            {
                written = DecompressFrame(source, destination, default, null, 0, out consumed);
            }
            else
            {
                written = DecompressFrame(source, destination, dictionary.Content, dictionary.Entropy, dictionary.Id, out consumed);
            }

            if (written < 0)
            {
                return Refuse((ZstdError)~written);
            }

            bytesConsumed = consumed;
            bytesWritten = written;
            LastError = ZstdError.None;
            return OperationStatus.Done;
        }
        catch (ZstdException e)
        {
            return Refuse(e.Error);
        }
    }

    /// <summary>
    /// Decompresses the frame that starts <paramref name="source"/> into the start of
    /// <paramref name="destination"/>, with <paramref name="dictionary"/> in place of the dictionary
    /// the decompressor was created with, if any.
    /// </summary>
    /// <param name="source">A zstd or skippable frame, possibly followed by other data, which is left alone.</param>
    /// <param name="destination">Where the frame's content is written.</param>
    /// <param name="dictionary">
    /// A dictionary in the zstd format, or any other bytes as raw content; empty for none. It is read
    /// in place for the call, or copied when it is of 2 KiB or less, and not kept, but the tables built
    /// from it are, for the next calls that give the same dictionary: once they are built, a frame costs
    /// what it would with the dictionary given to the constructor, and another dictionary rebuilds them
    /// without allocating.
    /// </param>
    /// <param name="bytesConsumed">The size of the frame, once it is decoded; otherwise 0.</param>
    /// <param name="bytesWritten">The size of the frame's content, once it is decoded; otherwise 0.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the frame is decoded;
    /// <see cref="OperationStatus.NeedMoreData"/> when the source ends inside the frame;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when the content does not fit;
    /// <see cref="OperationStatus.InvalidData"/> when the frame is not valid zstd, names another
    /// dictionary, or when the dictionary has the zstd format but invalid tables.
    /// </returns>
    public OperationStatus Decompress(
        ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> dictionary, out int bytesConsumed, out int bytesWritten)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        try
        {
            ReadOnlySpan<byte> history = UseDictionary(dictionary, out DictionaryEntropy? entropy, out uint id);
            int written = DecompressFrame(source, destination, history, entropy, id, out int consumed);
            if (written < 0)
            {
                return Refuse((ZstdError)~written);
            }

            bytesConsumed = consumed;
            bytesWritten = written;
            LastError = ZstdError.None;
            return OperationStatus.Done;
        }
        catch (ZstdException e)
        {
            return Refuse(e.Error);
        }
    }

    /// <summary>
    /// Decompresses the start of the frame that starts <paramref name="source"/>: as much of its
    /// content as <paramref name="destination"/> holds, or all of it when the content is shorter,
    /// with <paramref name="dictionary"/> as <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, ReadOnlySpan{byte}, out int, out int)"/>
    /// takes it. A frame whose declared content fits is decoded and checked whole; otherwise nothing
    /// past the destination's end is decoded, so nothing past it is checked: the rest of its block
    /// and the blocks after it, the content size and the checksum.
    /// </summary>
    /// <remarks>
    /// A frame's sequences run in order, so its start costs what it holds: a value near the start
    /// of a frame is reached without executing the matches that fill the rest of it.
    /// </remarks>
    /// <param name="source">A zstd frame, possibly followed by other data, which is left alone.</param>
    /// <param name="destination">Where the start of the frame's content is written.</param>
    /// <param name="dictionary">A dictionary as <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, ReadOnlySpan{byte}, out int, out int)"/> takes it; empty for none.</param>
    /// <param name="bytesWritten">The bytes written: the destination's length, unless the content is shorter.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the destination is full or the frame decoded; otherwise
    /// what <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, ReadOnlySpan{byte}, out int, out int)"/> returns.
    /// </returns>
    public OperationStatus DecompressPrefix(
        ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> dictionary, out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.IsEmpty)
        {
            LastError = ZstdError.None;
            return OperationStatus.Done;
        }

        if (TryGetFrameContentSize(source, out ulong size) && size <= (ulong)destination.Length)
        {
            return Decompress(source, destination, dictionary, out _, out bytesWritten);
        }

        _prefix = true;
        try
        {
            ReadOnlySpan<byte> history = UseDictionary(dictionary, out DictionaryEntropy? entropy, out uint id);
            int written = DecompressFrame(source, destination, history, entropy, id, out _);
            if (written < 0)
            {
                return Refuse((ZstdError)~written);
            }

            bytesWritten = written;
            LastError = ZstdError.None;
            return OperationStatus.Done;
        }
        catch (ZstdException e)
        {
            return Refuse(e.Error);
        }
        finally
        {
            _prefix = false;
        }
    }

    /// <summary>
    /// Forgets the frame in progress. <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/>
    /// decodes whole frames, so there is never one in progress: the method exists for callers written
    /// against a streaming decoder.
    /// </summary>
    public void Reset() => LastError = ZstdError.None;

    /// <summary>
    /// Reads the content size the frame starting <paramref name="source"/> declares in its header.
    /// </summary>
    /// <returns>
    /// Whether the header is complete and valid and declares a size; a skippable frame declares 0.
    /// </returns>
    public static bool TryGetFrameContentSize(ReadOnlySpan<byte> source, out ulong size)
    {
        size = 0;
        if (source.Length < 4)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if ((magic & FrameFormat.SkippableMagicMask) == FrameFormat.SkippableMagic)
        {
            return source.Length >= FrameFormat.SkippableHeaderSize;
        }

        if (magic != FrameFormat.Magic || FrameHeader.Parse(source, out FrameHeader header) != ZstdError.None
            || !header.HasContentSize)
        {
            return false;
        }

        size = header.ContentSize;
        return true;
    }

    /// <summary>The status a refusal reports, which it records.</summary>
    private OperationStatus Refuse(ZstdError error)
    {
        LastError = error;
        return error switch
        {
            ZstdError.Truncated => OperationStatus.NeedMoreData,
            ZstdError.DestinationTooSmall => OperationStatus.DestinationTooSmall,
            _ => OperationStatus.InvalidData,
        };
    }

    /// <summary>
    /// libzstd's <c>ZSTD_decompress_usingDict</c>, without its tables for the same dictionary again:
    /// <paramref name="dictionary"/> made the history of the next frame, read in place or copied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor's dictionary is copied with <see cref="DecoderDictionary.Margin"/> bytes after
    /// it, which the fast sequence loops read past the end of a match that ends near its end, copying
    /// it 32 bytes at a time. This one, read in place, has no such room: the loops leave a match that
    /// ends within the margin of its end to the careful path (<see cref="_historyGuard"/>), which
    /// copies it exactly, and read the rest in place. A copy would cost what the dictionary weighs at
    /// every frame, 110 KiB for zstd's default, several times the decoding of a small frame.
    /// </para>
    /// <para>
    /// A small dictionary is copied all the same, as the constructor's is: read in place, it would send
    /// most blocks to the careful path, which costs more than copying it (<see cref="CopiedDictionaryMax"/>).
    /// </para>
    /// <para>
    /// The entropy tables are a function of the dictionary's first bytes, up to its repeat offsets,
    /// and of its length, which they are checked against: when both are those of the dictionary they
    /// were built from, they are taken as they are.
    /// </para>
    /// </remarks>
    /// <returns>The history: the dictionary, or its copy.</returns>
    private ReadOnlySpan<byte> UseDictionary(ReadOnlySpan<byte> dictionary, out DictionaryEntropy? entropy, out uint id)
    {
        entropy = null;
        id = 0;
        _historyGuard = 0;
        if (dictionary.IsEmpty)
        {
            return default;
        }

        if (DictionaryEntropy.IsZstdFormat(dictionary))
        {
            entropy = LoadCallEntropy(dictionary);
            id = entropy.Id;
        }

        if (dictionary.Length <= CopiedDictionaryMax)
        {
            if (_callHistory.Length < dictionary.Length + DecoderDictionary.Margin)
            {
                _callHistory = new byte[dictionary.Length + DecoderDictionary.Margin];
            }

            dictionary.CopyTo(_callHistory);
            return _callHistory.AsSpan(0, dictionary.Length);
        }

        _historyGuard = DecoderDictionary.Margin;
        return dictionary;
    }

    /// <summary>
    /// The tables of <paramref name="dictionary"/>, a zstd-format one: the ones built last when it is
    /// the same, else rebuilt in place.
    /// </summary>
    private DictionaryEntropy LoadCallEntropy(ReadOnlySpan<byte> dictionary)
    {
        DictionaryEntropy? entropy = _callEntropy;
        int length = _callEntropyLength;
        if (entropy is not null && length >= 0 && dictionary.Length == _callDictionaryLength
            && dictionary.StartsWith(_callEntropyKey.AsSpan(0, length)))
        {
            return entropy;
        }

        // The sets let go of the tables first: they know a table by its reference, and would take
        // the rebuilt ones for those they hold copies of.
        entropy ??= _callEntropy = new DictionaryEntropy();
        _callEntropyLength = -1;
        _sequenceTables.Forget();
        _pairTables?.Forget();
        try
        {
            length = entropy.Load(dictionary);
        }
        catch (ZstdException)
        {
            Throw.Error(ZstdError.DictionaryCorrupted);
        }

        if (_callEntropyKey.Length < length)
        {
            _callEntropyKey = new byte[length];
        }

        dictionary.Slice(0, length).CopyTo(_callEntropyKey);
        _callEntropyLength = length;
        _callDictionaryLength = dictionary.Length;
        return entropy;
    }

    /// <summary>
    /// Decodes one frame: refuses it by returning its error (see <see cref="Fail"/>) when the frame's
    /// structure is the cause, by throwing <see cref="ZstdException"/> when a block's content is.
    /// </summary>
    /// <param name="source">The frame.</param>
    /// <param name="destination">Where its content goes.</param>
    /// <param name="history">What precedes the frame: the dictionary's content, if any.</param>
    /// <param name="entropy">The dictionary's tables, if it has them.</param>
    /// <param name="dictionaryId">The identifier of the dictionary; 0 for none, or for raw content.</param>
    /// <param name="consumed">The size of the frame.</param>
    /// <returns>The size of the content, or the refusal, negative.</returns>
    private int DecompressFrame(
        ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> history, DictionaryEntropy? entropy, uint dictionaryId,
        out int consumed)
    {
        consumed = 0;
        if (source.Length < 4)
        {
            return Fail(FrameHeader.IsMagicPrefix(source) ? ZstdError.Truncated : ZstdError.UnknownMagic);
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if ((magic & FrameFormat.SkippableMagicMask) == FrameFormat.SkippableMagic)
        {
            int skipped = SkipFrame(source);
            if (skipped < 0)
            {
                return skipped;
            }

            consumed = skipped;
            return 0;
        }

        if (magic != FrameFormat.Magic)
        {
            return Fail(ZstdError.UnknownMagic);
        }

        ZstdError headerError = FrameHeader.Parse(source, out FrameHeader header);
        if (headerError != ZstdError.None)
        {
            return Fail(headerError);
        }

        if (header.DictionaryId != 0 && header.DictionaryId != dictionaryId)
        {
            return Fail(ZstdError.DictionaryMismatch);
        }

        if (header.HasContentSize && header.ContentSize > (ulong)destination.Length && !_prefix)
        {
            return Fail(ZstdError.DestinationTooSmall);
        }

        // The literals of a block fit the block and the destination (DecodeLiterals refuses more).
        EnsureLiterals(ref _literals, _prefix ? header.BlockSizeMax : Math.Min(header.BlockSizeMax, destination.Length));
        BeginFrame(entropy);

        int ip = header.HeaderSize;
        int op = 0;
        while (true)
        {
            if (source.Length - ip < FrameFormat.BlockHeaderSize)
            {
                return Fail(ZstdError.Truncated);
            }

            int blockHeader = source[ip] | (source[ip + 1] << 8) | (source[ip + 2] << 16);
            bool lastBlock = (blockHeader & 1) != 0;
            int blockType = (blockHeader >> 1) & 3;
            int blockSize = blockHeader >> 3;
            ip += FrameFormat.BlockHeaderSize;

            switch (blockType)
            {
                case 0: // raw
                    if (blockSize > source.Length - ip)
                    {
                        return Fail(ZstdError.Truncated);
                    }

                    if (blockSize > header.BlockSizeMax)
                    {
                        return Fail(ZstdError.BlockTooLarge);
                    }

                    if (blockSize > destination.Length - op)
                    {
                        if (!_prefix)
                        {
                            return Fail(ZstdError.DestinationTooSmall);
                        }

                        // The destination ends in this block: its start, and the call is done.
                        BulkCopy.Copy(source.Slice(ip, destination.Length - op), destination.Slice(op));
                        return destination.Length;
                    }

                    BulkCopy.Copy(source.Slice(ip, blockSize), destination.Slice(op));
                    op += blockSize;
                    ip += blockSize;
                    break;

                case 1: // RLE: the size is the content's, the block holds one byte
                    if (source.Length - ip < 1)
                    {
                        return Fail(ZstdError.Truncated);
                    }

                    if (blockSize > header.BlockSizeMax)
                    {
                        return Fail(ZstdError.BlockTooLarge);
                    }

                    if (blockSize > destination.Length - op)
                    {
                        if (!_prefix)
                        {
                            return Fail(ZstdError.DestinationTooSmall);
                        }

                        destination.Slice(op).Fill(source[ip]);
                        return destination.Length;
                    }

                    destination.Slice(op, blockSize).Fill(source[ip]);
                    op += blockSize;
                    ip += 1;
                    break;

                case 2: // compressed
                    if (blockSize > source.Length - ip)
                    {
                        return Fail(ZstdError.Truncated);
                    }

                    if (blockSize > header.BlockSizeMax)
                    {
                        return Fail(ZstdError.BlockTooLarge);
                    }

                    // With the next block compressed too, both at once (see ZstdDecompressor.Pairs.cs).
                    int next = ip + blockSize;
                    if (!lastBlock && !_prefix && _lastBlockSequences >= MinPairSequences && PairsBlocks
                        && TryPeekCompressedBlock(source, next, header.BlockSizeMax, out int nextSize, out bool nextLast)
                        && WorthPairing(source, ip, blockSize, next + FrameFormat.BlockHeaderSize, nextSize))
                    {
                        op += DecodeBlockPair(
                            source, ip, blockSize, next + FrameFormat.BlockHeaderSize, nextSize, destination, op, header.BlockSizeMax, history, out bool paired);
                        if (paired)
                        {
                            next += FrameFormat.BlockHeaderSize + nextSize;
                            lastBlock = nextLast;
                        }
                    }
                    else
                    {
                        op += DecodeCompressedBlock(source, ip, blockSize, destination, op, header.BlockSizeMax, history);
                        if (_prefix && op == destination.Length)
                        {
                            return op;
                        }
                    }

                    ip = next;
                    break;

                default:
                    return Fail(ZstdError.ReservedBlockType);
            }

            if (lastBlock)
            {
                break;
            }
        }

        if (header.HasContentSize && (ulong)op != header.ContentSize)
        {
            return Fail(ZstdError.ContentSizeMismatch);
        }

        if (header.HasChecksum)
        {
            if (source.Length - ip < FrameFormat.ChecksumSize)
            {
                return Fail(ZstdError.Truncated);
            }

            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(ip));
            uint actual = (uint)XxHash64.HashToUInt64(destination.Slice(0, op));
            if (actual != expected)
            {
                return Fail(ZstdError.ChecksumMismatch);
            }

            ip += FrameFormat.ChecksumSize;
        }

        consumed = ip;
        return op;
    }

    /// <summary>libzstd's <c>readSkippableFrameSize</c>.</summary>
    /// <returns>The size of the frame, or its refusal (see <see cref="Fail"/>).</returns>
    private static int SkipFrame(ReadOnlySpan<byte> source)
    {
        if (source.Length < FrameFormat.SkippableHeaderSize)
        {
            return Fail(ZstdError.Truncated);
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4));
        if (size + (uint)FrameFormat.SkippableHeaderSize < size)
        {
            return Fail(ZstdError.SkippableFrameTooLarge);
        }

        ulong total = (ulong)FrameFormat.SkippableHeaderSize + size;
        if (total > (ulong)source.Length)
        {
            return Fail(ZstdError.Truncated);
        }

        return (int)total;
    }

    /// <summary>
    /// A refusal of the frame as a whole, returned rather than thrown: a source that ends inside the
    /// frame, a destination too small for the size it declares, which callers meet in normal use, and
    /// every other check of the frame's structure. The errors inside a block are thrown
    /// (<see cref="ZstdException"/>), from deep in its decoding.
    /// </summary>
    /// <returns>The error, as a negative number: <c>~error</c>.</returns>
    private static int Fail(ZstdError error) => ~(int)error;

    /// <summary>
    /// libzstd's <c>ZSTD_decompressBegin_usingDDict</c>: the state every frame starts from, the
    /// dictionary's tables and repeat offsets when it has them.
    /// </summary>
    private void BeginFrame(DictionaryEntropy? entropy)
    {
        _rep0 = 1;
        _rep1 = 4;
        _rep2 = 8;
        _literalEntropy = false;
        _sequenceEntropy = false;
        _lastBlockSequences = MinPairSequences;

        // A reference is written only when it changes: each write is a call to the GC's write
        // barrier, and frame after frame with one dictionary the references stay the same.
        HuffmanTable huffman = entropy is not null ? entropy.Huffman : _huffman;
        if (!ReferenceEquals(_currentHuffman, huffman))
        {
            _currentHuffman = huffman;
        }

        if (entropy is not null)
        {
            _literalEntropy = true;
            _sequenceEntropy = true;
            _sequenceTables.BeginFrame(entropy.LiteralLengths, entropy.Offsets, entropy.MatchLengths);
            _rep0 = entropy.Rep0;
            _rep1 = entropy.Rep1;
            _rep2 = entropy.Rep2;
        }
    }
}
