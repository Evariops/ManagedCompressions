namespace ManagedCompressions.Brotli;

/// <summary>
/// Why <see cref="BrotliDecompressor"/> refused a stream: the check that failed, which
/// <see cref="BrotliDecompressor.LastError"/> reports after a call that did not return
/// <see cref="System.Buffers.OperationStatus.Done"/>. The status says what to do next; this says why.
/// </summary>
/// <remarks>
/// The checks are those of brotli's decoder, and each value names the error code brotli 1.1.1 reports
/// for the same stream (<c>BrotliDecoderErrorCode</c>). brotli has two codes for a meta-block that
/// produces more than it declares, and two for padding bits that are not zero, which depend on the
/// state of its ring buffer; each pair is a single value here.
/// </remarks>
public enum BrotliError
{
    /// <summary>No refusal: the last call decoded its stream.</summary>
    None = 0,

    // ---- the source ends before the stream does: NeedMoreData

    /// <summary>The source ends before the stream does (<c>BROTLI_DECODER_NEEDS_MORE_INPUT</c>).</summary>
    Truncated,

    // ---- the stream does not fit: DestinationTooSmall

    /// <summary>The stream's content does not fit in the destination: a meta-block declares more than the room left.</summary>
    DestinationTooSmall,

    // ---- the stream is invalid: InvalidData

    /// <summary>A meta-block's length has more nibbles than it needs: its last one is zero (<c>FORMAT_EXUBERANT_NIBBLE</c>).</summary>
    ExuberantNibble,

    /// <summary>A metadata meta-block sets its reserved bit (<c>FORMAT_RESERVED</c>).</summary>
    Reserved,

    /// <summary>A metadata meta-block's length has more bytes than it needs: its last one is zero (<c>FORMAT_EXUBERANT_META_NIBBLE</c>).</summary>
    ExuberantMetaNibble,

    /// <summary>A simple prefix code names a symbol outside its alphabet (<c>FORMAT_SIMPLE_HUFFMAN_ALPHABET</c>).</summary>
    SimpleHuffmanAlphabet,

    /// <summary>A simple prefix code names the same symbol twice (<c>FORMAT_SIMPLE_HUFFMAN_SAME</c>).</summary>
    SimpleHuffmanSame,

    /// <summary>The lengths of a code length code do not make a complete prefix code (<c>FORMAT_CL_SPACE</c>).</summary>
    CodeLengthSpace,

    /// <summary>The lengths of a prefix code do not make a complete prefix code (<c>FORMAT_HUFFMAN_SPACE</c>).</summary>
    HuffmanSpace,

    /// <summary>A run of zeros goes past the end of a context map (<c>FORMAT_CONTEXT_MAP_REPEAT</c>).</summary>
    ContextMapRepeat,

    /// <summary>A meta-block's commands produce more than its length (<c>FORMAT_BLOCK_LENGTH_1</c> and <c>_2</c>).</summary>
    BlockLength,

    /// <summary>
    /// A static dictionary reference names a transform that does not exist, or one that leaves nothing
    /// near the start of the stream (<c>FORMAT_TRANSFORM</c>).
    /// </summary>
    Transform,

    /// <summary>A reference past the window has a copy length no dictionary word has (<c>FORMAT_DICTIONARY</c>).</summary>
    Dictionary,

    /// <summary>The stream declares a window the format does not have, or a large window (<c>FORMAT_WINDOW_BITS</c>).</summary>
    WindowBits,

    /// <summary>The bits that pad a meta-block header or the stream's end to a byte are not zero (<c>FORMAT_PADDING_1</c> and <c>_2</c>).</summary>
    Padding,

    /// <summary>A distance is larger than the format allows (<c>FORMAT_DISTANCE</c>).</summary>
    Distance,

    /// <summary>
    /// A block switch in a category of a single block type: its 16 Mi symbols are used up
    /// (<c>FORMAT_BLOCK_SWITCH</c>, the fix of brotli 1.1.1).
    /// </summary>
    BlockSwitch,
}
