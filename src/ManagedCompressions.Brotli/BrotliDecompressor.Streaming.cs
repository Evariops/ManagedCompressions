using System;
using System.Diagnostics;
using ManagedCompressions.Brotli.Internal;
using ManagedCompressions.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// The steps <see cref="BrotliStreamDecoder"/> decodes a stream in, as its input comes, each resumed
/// where the last one stopped, so that a stream decodes once whatever pieces it comes in: a compressed
/// meta-block's header part by part, a block's types, a context map's symbols, a prefix code at a time;
/// its commands up to the last point the fast loop can stop at, 64 bytes before the end of the input,
/// then command by command in the careful loop, which goes back to the last command it finished when
/// the input ends inside the next; an uncompressed meta-block's bytes, and a metadata block's, as they
/// arrive.
/// </summary>
public sealed unsafe partial class BrotliDecompressor
{
    /// <summary>What a step did.</summary>
    internal enum StreamResult
    {
        /// <summary>A step forward: call again.</summary>
        Progress,

        /// <summary>The input is not enough to go further: give more, then call again.</summary>
        NeedInput,

        /// <summary>The next meta-block needs <see cref="StreamRoomNeeded"/> bytes past the position.</summary>
        NeedRoom,

        /// <summary>The stream is decoded: <see cref="StreamEnd"/> is its size.</summary>
        Finished,

        /// <summary>The stream is invalid: <see cref="LastError"/> says why.</summary>
        Failed,
    }

    private enum StreamPhase
    {
        WindowBits,

        /// <summary>A meta-block's own header: ISLAST, MLEN and the kind of the meta-block.</summary>
        Header,

        /// <summary>A compressed meta-block's header, from <see cref="_headerStage"/>.</summary>
        CompressedHeader,

        Commands,

        /// <summary>An uncompressed meta-block's bytes, copied as they come.</summary>
        Uncompressed,

        /// <summary>A metadata block's bytes, skipped as they come.</summary>
        Metadata,

        Padding,
        Done,
    }

    /// <summary>The parts of a compressed meta-block's header, in their order.</summary>
    private enum HeaderStage
    {
        LiteralBlockTypes,
        CommandBlockTypes,
        DistanceBlockTypes,
        DistanceParameters,
        LiteralContextMap,
        DistanceContextMap,
        LiteralCodes,
        CommandCodes,
        DistanceCodes,
    }

    private StreamPhase _phase;

    /// <summary>
    /// The bit of the input where the next step starts: in the commands, where the last stop left the
    /// bit reader, which goes on from there with the bytes of the input as they are then, never with the
    /// zeros a reader reads past the end of the input.
    /// </summary>
    private long _bitPosition;

    private bool _lastMetaBlock;

    /// <summary>Whether the decoder streams: its careful loop then stops after each command, for the stream to resume from there.</summary>
    private bool _streaming;

    /// <summary>The part of a compressed meta-block's header that comes next.</summary>
    private HeaderStage _headerStage;

    /// <summary>The prefix codes of the current group read so far, and the entries of its table they take.</summary>
    private int _headerCodes;
    private int _headerCodesUsed;

    /// <summary>A context map read so far: its step (0 its code, 1 its values, 2 its move to front, 3 done), its values, its codes, its longest run code.</summary>
    private int _mapStep;
    private int _mapIndex;
    private int _mapTrees;
    private int _mapRunPrefix;

    /// <summary>The state at the last point the command loop stopped at.</summary>
    private Checkpoint _checkpoint;

    /// <summary>The fields the command loop changes; the literal codes follow from the block type.</summary>
    private struct Checkpoint
    {
        public long BitPosition;
        public int Pos;
        public int MetaBlockRemaining;
        public int InsertRemaining;
        public int CopyLength;
        public int DistanceCode;
        public int DistanceContext;
        public int DistanceTreeIndex;
        public nint CommandTree;
        public nint DistContextMapSlice;
        public int Resume;
        public Fixed S;
        public int DistRbIndex;
    }

    /// <summary>The bytes past the position the next meta-block may write: its length and the copies' slack.</summary>
    internal int StreamRoomNeeded => _metaBlockRemaining + DictionarySlack + CopySlack;

    /// <summary>The content decoded so far, from the start of the history buffer.</summary>
    internal int StreamPosition => _pos;

    /// <summary>The window: what the history buffer keeps before the position, at least.</summary>
    internal int StreamWindow => _phase == StreamPhase.WindowBits ? 0 : 1 << _windowBits;

    /// <summary>The first byte of the input the state still reads: those before it can go.</summary>
    internal long StreamFrontier => _bitPosition >> 3;

    /// <summary>The stream's size, once <see cref="StreamResult.Finished"/>.</summary>
    internal long StreamEnd => _bitPosition >> 3;

    /// <summary>A new stream.</summary>
    internal void StreamReset()
    {
        _phase = StreamPhase.WindowBits;
        _streaming = true;
        _bitPosition = 0;
        _pos = 0;
        _s.DistRb[0] = 16;
        _s.DistRb[1] = 15;
        _s.DistRb[2] = 11;
        _s.DistRb[3] = 4;
        _distRbIndex = 0;
        LastError = BrotliError.None;
    }

    /// <summary>The input lost its first <paramref name="bytes"/>, which the state no longer reads.</summary>
    internal void StreamDropInput(long bytes)
    {
        _bitPosition -= bytes * 8;
    }

    /// <summary>The history buffer lost its first <paramref name="bytes"/>, all behind the window.</summary>
    internal void StreamDropHistory(int bytes) => _pos -= bytes;

    /// <summary>One step of the stream from <paramref name="input"/>, into <paramref name="history"/> at <see cref="StreamPosition"/>.</summary>
    internal StreamResult StreamStep(byte* input, long inputLength, byte* history, int historyCapacity)
    {
        switch (_phase)
        {
            case StreamPhase.WindowBits:
            {
                StartReader(input, inputLength);
                BrotliError error = DecodeWindowBits();
                if (_br.Overrun)
                {
                    return StreamResult.NeedInput;
                }

                if (error != BrotliError.None)
                {
                    return Failed(error);
                }

                _maxBackwardDistance = (1 << _windowBits) - Prefix.WindowGap;
                _bitPosition = ReaderPosition(input);
                _phase = StreamPhase.Header;
                return StreamResult.Progress;
            }

            case StreamPhase.Header:
                return StreamHeader(input, inputLength, historyCapacity);

            case StreamPhase.CompressedHeader:
                StartReader(input, inputLength);
                return StreamCompressedHeader(input);

            case StreamPhase.Commands:
                return StreamCommands(input, inputLength, history, historyCapacity);

            case StreamPhase.Uncompressed:
                return StreamUncompressed(input, inputLength, history, historyCapacity);

            case StreamPhase.Metadata:
                return StreamMetadata(inputLength);

            case StreamPhase.Padding:
            {
                StartReader(input, inputLength);
                if (_br.JumpToByteBoundary() != 0)
                {
                    return _br.Overrun ? StreamResult.NeedInput : Failed(BrotliError.Padding);
                }

                if (_br.Overrun)
                {
                    return StreamResult.NeedInput;
                }

                _bitPosition = ReaderPosition(input);
                _phase = StreamPhase.Done;
                return StreamResult.Finished;
            }

            default:
                return StreamResult.Finished;
        }
    }

    /// <summary>
    /// A meta-block's own header, a few bytes, read again from its start until it is whole and the
    /// history has room for the meta-block; then the meta-block's content, by its kind.
    /// </summary>
    private StreamResult StreamHeader(byte* input, long inputLength, int historyCapacity)
    {
        StartReader(input, inputLength);
        BrotliError error = DecodeMetaBlockHeader(out bool last, out bool uncompressed, out bool metadata);
        if (_br.Overrun || error == BrotliError.Truncated)
        {
            return StreamResult.NeedInput;
        }

        if (error != BrotliError.None)
        {
            return Failed(error);
        }

        if (metadata || uncompressed)
        {
            if (_br.JumpToByteBoundary() != 0)
            {
                return _br.Overrun ? StreamResult.NeedInput : Failed(BrotliError.Padding);
            }

            if (_br.Overrun)
            {
                return StreamResult.NeedInput;
            }
        }

        _lastMetaBlock = last;
        if (metadata)
        {
            // A metadata block can be the last one, as an empty one can.
            _bitPosition = ReaderPosition(input);
            _phase = StreamPhase.Metadata;
            return StreamResult.Progress;
        }

        if (_metaBlockRemaining == 0)
        {
            _bitPosition = ReaderPosition(input);
            _phase = last ? StreamPhase.Padding : StreamPhase.Header;
            return StreamResult.Progress;
        }

        // The meta-block's room first; the header is read again once there is room.
        if (historyCapacity - _pos < StreamRoomNeeded)
        {
            return StreamResult.NeedRoom;
        }

        _bitPosition = ReaderPosition(input);
        if (uncompressed)
        {
            _phase = StreamPhase.Uncompressed;
            return StreamResult.Progress;
        }

        _headerStage = HeaderStage.LiteralBlockTypes;
        _phase = StreamPhase.CompressedHeader;
        return StreamCompressedHeader(input);
    }

    /// <summary>
    /// A compressed meta-block's header, from where the last call stopped, with the bit reader at
    /// <see cref="_bitPosition"/>: each part, once whole, moves the position past it, so that the input
    /// is read once whatever pieces it comes in. A part is a block category's types, the distance
    /// parameters, a context map's code or one of its symbols, or a prefix code: a few hundred bytes at
    /// most, which the next call reads again when the input ends inside them.
    /// </summary>
    private StreamResult StreamCompressedHeader(byte* input)
    {
        StreamResult stop;
        while (_headerStage <= HeaderStage.DistanceBlockTypes)
        {
            BrotliError error = DecodeBlockTypes((int)_headerStage);
            if (!Commit(input, error, out stop))
            {
                return stop;
            }

            _headerStage++;
        }

        if (_headerStage == HeaderStage.DistanceParameters)
        {
            DecodeDistanceParametersAndModes();
            if (!Commit(input, BrotliError.None, out stop))
            {
                return stop;
            }

            _headerStage = HeaderStage.LiteralContextMap;
            _mapStep = 0;
        }

        if (_headerStage == HeaderStage.LiteralContextMap)
        {
            if (!StreamContextMap(input, _s.NumBlockTypes[0] << Prefix.LiteralContextBits, _contextMap, out stop))
            {
                return stop;
            }

            _numLiteralTrees = _mapTrees;
            DetectTrivialLiteralBlockTypes();
            _literalGroup.RootBits = _metaBlockRemaining / WideRootContentPerCode >= _numLiteralTrees ? WideLiteralRootBits : Huffman.RootBits;
            _distanceAlphabetSize = Prefix.DistanceAlphabetSize(_distancePostfixBits, _numDirectDistanceCodes, Prefix.MaxDistanceBits);
            _headerStage = HeaderStage.DistanceContextMap;
            _mapStep = 0;
        }

        if (_headerStage == HeaderStage.DistanceContextMap)
        {
            if (!StreamContextMap(input, _s.NumBlockTypes[2] << Prefix.DistanceContextBits, _distContextMap, out stop))
            {
                return stop;
            }

            _numDistanceTrees = _mapTrees;
            _headerStage = HeaderStage.LiteralCodes;
            _headerCodes = 0;
            _headerCodesUsed = 0;
        }

        if (_headerStage == HeaderStage.LiteralCodes)
        {
            if (!StreamTreeGroup(input, _literalGroup, Prefix.NumLiteralSymbols, Prefix.NumLiteralSymbols, _numLiteralTrees, out stop))
            {
                return stop;
            }

            _headerStage = HeaderStage.CommandCodes;
            _headerCodes = 0;
            _headerCodesUsed = 0;
        }

        if (_headerStage == HeaderStage.CommandCodes)
        {
            if (!StreamTreeGroup(input, _commandGroup, Prefix.NumCommandSymbols, Prefix.NumCommandSymbols, _s.NumBlockTypes[1], out stop))
            {
                return stop;
            }

            _headerStage = HeaderStage.DistanceCodes;
            _headerCodes = 0;
            _headerCodesUsed = 0;
        }

        if (!StreamTreeGroup(input, _distanceGroup, _distanceAlphabetSize, _distanceAlphabetSize, _numDistanceTrees, out stop))
        {
            return stop;
        }

        CalculateDistanceLut();
        if (_contextTrees == null)
        {
            _contextTreesArray = Pinned<nint>(1 << Prefix.LiteralContextBits);
            _contextTrees = Pointer(_contextTreesArray);
        }

        PrepareLiteralDecoding();
        _distContextMapSlice = _distContextMap;
        _commandTree = (uint*)_commandGroup.Trees[0];
        _insertRemaining = 0;
        _resume = AtCommandBegin;
        _phase = StreamPhase.Commands;
        SaveCheckpoint();
        return StreamResult.Progress;
    }

    /// <summary>
    /// <see cref="DecodeContextMap"/>, resumed from <see cref="_mapStep"/> and <see cref="_mapIndex"/>: its
    /// code, then its symbols one at a time, then its move-to-front bit. True once it is whole.
    /// </summary>
    private bool StreamContextMap(byte* input, int contextMapSize, byte* contextMap, out StreamResult stop)
    {
        Debug.Assert(contextMapSize <= Prefix.MaxBlockTypes << Prefix.LiteralContextBits, "a context map within its table");
        if (_mapStep == 0)
        {
            _mapTrees = DecodeVarLenUint8() + 1;
            if (_mapTrees <= 1)
            {
                if (!Commit(input, BrotliError.None, out stop))
                {
                    return false;
                }

                new Span<byte>(contextMap, contextMapSize).Clear();
                _mapStep = 3;
                return true;
            }

            _mapRunPrefix = 0;
            if (_br.Count < 5)
            {
                _br.Refill();
            }

            uint bits = (uint)(_br.Bits & 31);
            if ((bits & 1) != 0)
            {
                // Runs of zeros.
                _mapRunPrefix = (int)(bits >> 1) + 1;
                _br.Bits >>= 5;
                _br.Count -= 5;
            }
            else
            {
                _br.Bits >>= 1;
                _br.Count -= 1;
            }

            int alphabetSize = _mapTrees + _mapRunPrefix;
            BrotliError error = ReadHuffmanCode(alphabetSize, alphabetSize, _contextMapTree, out int tableSize);
            if (!Commit(input, error, out stop))
            {
                return false;
            }

            Debug.Assert(tableSize <= ContextMapTableSize, "a context map code within its table");
            _mapIndex = 0;
            _mapStep = 1;
        }

        if (_mapStep == 1)
        {
            // A symbol a part: its value, or its run of zeros. One read past the input writes within the
            // map, at the values not yet kept, which the next call writes again.
            int maxRunLengthPrefix = _mapRunPrefix;
            while (_mapIndex < contextMapSize)
            {
                int index = _mapIndex;
                BrotliError error = BrotliError.None;
                int code = _br.ReadSymbol(_contextMapTree);
                if (code == 0)
                {
                    contextMap[index++] = 0;
                }
                else if (code > maxRunLengthPrefix)
                {
                    contextMap[index++] = (byte)(code - maxRunLengthPrefix);
                }
                else
                {
                    int repeat = (int)_br.Read(code) + (1 << code);
                    if (index + repeat > contextMapSize)
                    {
                        error = BrotliError.ContextMapRepeat;
                    }
                    else
                    {
                        new Span<byte>(contextMap + index, repeat).Clear();
                        index += repeat;
                    }
                }

                if (!Commit(input, error, out stop))
                {
                    return false;
                }

                _mapIndex = index;
            }

            _mapStep = 2;
        }

        if (_mapStep == 2)
        {
            bool inverse = _br.Read(1) != 0;
            if (!Commit(input, BrotliError.None, out stop))
            {
                return false;
            }

            if (inverse)
            {
                InverseMoveToFront(contextMap, contextMapSize);
            }

            // As DecodeContextMap: the values are below the number of codes, which the command loop relies on.
            Debug.Assert(new ReadOnlySpan<byte>(contextMap, contextMapSize).IndexOfAnyExceptInRange((byte)0, (byte)(_mapTrees - 1)) < 0, "a context map's values below its number of codes");
            _mapStep = 3;
        }

        stop = StreamResult.Progress;
        return true;
    }

    /// <summary>
    /// <see cref="DecodeTreeGroup"/>, resumed from <see cref="_headerCodes"/>: the prefix codes of a group,
    /// one at a time. True once they are all read.
    /// </summary>
    private bool StreamTreeGroup(byte* input, TreeGroup group, int alphabetSizeMax, int alphabetSizeLimit, int count, out StreamResult stop)
    {
        int maxTableSize = group.MaxTableSize(alphabetSizeLimit);

        // The roots kept so far stay: the array only grows past them, and only for a larger count.
        group.ReserveTrees(count);
        while (_headerCodes < count)
        {
            group.Ensure(_headerCodesUsed, maxTableSize, _headerCodes);
            uint* table = group.Codes + _headerCodesUsed;
            BrotliError error = ReadHuffmanCode(alphabetSizeMax, alphabetSizeLimit, table, out int tableSize, group.RootBits);
            if (!Commit(input, error, out stop))
            {
                return false;
            }

            Debug.Assert(tableSize <= maxTableSize, "a code within the room of its table");
            group.Trees[_headerCodes++] = (nint)table;
            _headerCodesUsed += tableSize;
        }

        stop = StreamResult.Progress;
        return true;
    }

    /// <summary>
    /// The end of a part of the header: the input is not enough when the part read past it, the stream is
    /// invalid when the part says so, and otherwise the position moves past the part.
    /// </summary>
    private bool Commit(byte* input, BrotliError error, out StreamResult stop)
    {
        if (_br.Overrun)
        {
            stop = StreamResult.NeedInput;
            return false;
        }

        if (error != BrotliError.None)
        {
            stop = Failed(error);
            return false;
        }

        _bitPosition = ReaderPosition(input);
        stop = StreamResult.Progress;
        return true;
    }

    /// <summary>An uncompressed meta-block's bytes, as many as the input holds, copied to the history.</summary>
    private StreamResult StreamUncompressed(byte* input, long inputLength, byte* history, int historyCapacity)
    {
        // At a byte boundary: the header ended on one.
        long start = _bitPosition >> 3;
        int n = (int)Math.Min(Math.Max(0, inputLength - start), _metaBlockRemaining);
        if (n > 0)
        {
            // SAFETY: the history had room for the whole meta-block at its header, and only grows since.
            Bounds.Within(input + start, n, input, input + inputLength);
            Bounds.Within(history + _pos, n, history, history + historyCapacity);
            Buffer.MemoryCopy(input + start, history + _pos, n, n);
            _pos += n;
            _metaBlockRemaining -= n;
            _bitPosition += (long)n * 8;
        }

        if (_metaBlockRemaining != 0)
        {
            return StreamResult.NeedInput;
        }

        // An uncompressed meta-block is never the last one.
        _phase = StreamPhase.Header;
        return StreamResult.Progress;
    }

    /// <summary>A metadata block's bytes, as many as the input holds, skipped.</summary>
    private StreamResult StreamMetadata(long inputLength)
    {
        long n = Math.Min(Math.Max(0, inputLength - (_bitPosition >> 3)), _metaBlockRemaining);
        _metaBlockRemaining -= (int)n;
        _bitPosition += n * 8;
        if (_metaBlockRemaining != 0)
        {
            return StreamResult.NeedInput;
        }

        _phase = _lastMetaBlock ? StreamPhase.Padding : StreamPhase.Header;
        return StreamResult.Progress;
    }

    private StreamResult StreamCommands(byte* input, long inputLength, byte* history, int historyCapacity)
    {
        // A bit reader at the last stop, which holds the rest of its byte and nothing else: the bits past it
        // come from the input as it is now.
        long start = _bitPosition >> 3;
        int used = (int)(_bitPosition & 7);
        if (used != 0 && start >= inputLength)
        {
            // The byte of the last stop went back to the caller with the input past it, as the output was
            // full: nothing goes on without it.
            return StreamResult.NeedInput;
        }

        _br = new BitReader(input + start, (int)(inputLength - start));
        if (used != 0)
        {
            Debug.Assert(start < inputLength, "a partly read byte within the input");
            _br.Bits = (ulong)(*_br.Next >> used);
            _br.Count = 8 - used;
            _br.Next++;
        }

        bool wide = _literalGroup.RootBits == WideLiteralRootBits;
        int result = wide ? Commands<FastMode, WideRoot>(history, historyCapacity, _resume) : Commands<FastMode, NarrowRoot>(history, historyCapacity, _resume);
        if (result == SwitchToCareful)
        {
            // Stops the input can resume from: where the fast loop stopped, then after each command the careful
            // loop finishes; the careful loop finishes the meta-block if the input holds it.
            do
            {
                _bitPosition = ReaderPosition(input);
                SaveCheckpoint();
                result = wide ? Commands<CarefulMode, WideRoot>(history, historyCapacity, _resume) : Commands<CarefulMode, NarrowRoot>(history, historyCapacity, _resume);
            }
            while (result == SwitchToCareful);

            if (result == (int)BrotliError.Truncated || (result == 0 && _br.Overrun))
            {
                RestoreCheckpoint();
                return StreamResult.NeedInput;
            }
        }

        if (result != 0)
        {
            return Failed((BrotliError)result);
        }

        _bitPosition = ReaderPosition(input);
        _phase = _lastMetaBlock ? StreamPhase.Padding : StreamPhase.Header;
        return StreamResult.Progress;
    }

    /// <summary>A bit reader at <see cref="_bitPosition"/>.</summary>
    private void StartReader(byte* input, long inputLength)
    {
        long start = _bitPosition >> 3;
        _br = new BitReader(input + start, (int)(inputLength - start));
        int skip = (int)(_bitPosition & 7);
        if (skip != 0)
        {
            _br.Read(skip);
        }
    }

    /// <summary>The bit reader's position in the input.</summary>
    private long ReaderPosition(byte* input) => ((_br.Start - input) * 8) + _br.Position;

    private StreamResult Failed(BrotliError error)
    {
        LastError = error;
        _phase = StreamPhase.Done;
        return StreamResult.Failed;
    }

    private void SaveCheckpoint()
    {
        _checkpoint.BitPosition = _bitPosition;
        _checkpoint.Pos = _pos;
        _checkpoint.MetaBlockRemaining = _metaBlockRemaining;
        _checkpoint.InsertRemaining = _insertRemaining;
        _checkpoint.CopyLength = _copyLength;
        _checkpoint.DistanceCode = _distanceCode;
        _checkpoint.DistanceContext = _distanceContext;
        _checkpoint.DistanceTreeIndex = _distanceTreeIndex;
        _checkpoint.CommandTree = (nint)_commandTree;
        _checkpoint.DistContextMapSlice = (nint)_distContextMapSlice;
        _checkpoint.Resume = _resume;
        _checkpoint.S = _s;
        _checkpoint.DistRbIndex = _distRbIndex;
    }

    private void RestoreCheckpoint()
    {
        _bitPosition = _checkpoint.BitPosition;
        _pos = _checkpoint.Pos;
        _metaBlockRemaining = _checkpoint.MetaBlockRemaining;
        _insertRemaining = _checkpoint.InsertRemaining;
        _copyLength = _checkpoint.CopyLength;
        _distanceCode = _checkpoint.DistanceCode;
        _distanceContext = _checkpoint.DistanceContext;
        _distanceTreeIndex = _checkpoint.DistanceTreeIndex;
        _commandTree = (uint*)_checkpoint.CommandTree;
        _distContextMapSlice = (byte*)_checkpoint.DistContextMapSlice;
        _resume = _checkpoint.Resume;
        _s = _checkpoint.S;
        _distRbIndex = _checkpoint.DistRbIndex;
        PrepareLiteralDecoding();
    }
}
