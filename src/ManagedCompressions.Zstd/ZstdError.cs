namespace ManagedCompressions.Zstd;

/// <summary>
/// Why <see cref="ZstdDecompressor"/> refused a frame: the check that failed, which
/// <see cref="ZstdDecompressor.LastError"/> reports after a call that did not return
/// <see cref="System.Buffers.OperationStatus.Done"/>. The status says what to do next; this says why.
/// New checks may add values.
/// </summary>
public enum ZstdError
{
    /// <summary>No refusal: the last call decoded its frame.</summary>
    None = 0,

    // ---- the source ends before the frame does: NeedMoreData

    /// <summary>The source ends before the frame does.</summary>
    Truncated,

    // ---- the frame does not fit: DestinationTooSmall

    /// <summary>The frame's content does not fit in the destination.</summary>
    DestinationTooSmall,

    // ---- the frame is invalid: InvalidData

    /// <summary>The source starts with neither a Zstandard frame's nor a skippable frame's magic number.</summary>
    UnknownMagic,

    /// <summary>The frame header sets its reserved bit.</summary>
    ReservedBitSet,

    /// <summary>The frame header declares a window larger than the format allows.</summary>
    WindowTooLarge,

    /// <summary>A skippable frame declares a size its header and content cannot add up to.</summary>
    SkippableFrameTooLarge,

    /// <summary>The frame names a dictionary other than the one given, or names one when none is given.</summary>
    DictionaryMismatch,

    /// <summary>A block has the reserved block type.</summary>
    ReservedBlockType,

    /// <summary>A block is larger than the frame's largest block: its window, and at most 128 KiB.</summary>
    BlockTooLarge,

    /// <summary>The content's size is not the one the frame header declares.</summary>
    ContentSizeMismatch,

    /// <summary>The content's checksum is not the one the frame ends with.</summary>
    ChecksumMismatch,

    /// <summary>A block's literals section has an invalid header.</summary>
    LiteralsHeader,

    /// <summary>A block declares more literals than its largest size.</summary>
    LiteralsSizeTooLarge,

    /// <summary>A block reuses the previous Huffman table where no block of the frame has defined one.</summary>
    TreelessWithoutTable,

    /// <summary>A Huffman table is invalid.</summary>
    HuffmanTable,

    /// <summary>A Huffman-coded literals stream does not decode to exactly its literals.</summary>
    HuffmanStream,

    /// <summary>A block's sequences section has an invalid header.</summary>
    SequencesHeader,

    /// <summary>A literal-length, offset or match-length table is invalid.</summary>
    FseTable,

    /// <summary>A block repeats the previous sequence table where no block of the frame has defined one.</summary>
    RepeatWithoutTable,

    /// <summary>The sequences' bitstream is invalid, or does not end where its last sequence does.</summary>
    SequenceBitstream,

    /// <summary>The sequences take more literals than the block has.</summary>
    LiteralsOverrun,

    /// <summary>A match reaches back past the start of the content and of the dictionary.</summary>
    OffsetTooLarge,

    // ---- the dictionary given with the frame is invalid: InvalidData

    /// <summary>The dictionary given has the zstd format but invalid tables.</summary>
    DictionaryCorrupted,
}
