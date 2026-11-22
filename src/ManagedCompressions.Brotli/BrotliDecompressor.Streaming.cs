using System;
using ManagedCompressions.Brotli.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// The steps <see cref="BrotliStreamDecoder"/> decodes a stream in, as its input comes: the header of
/// each meta-block once all of it is in, and its commands up to the last point the fast loop can stop at,
/// 64 bytes before the end of the input, from where the careful loop finishes the meta-block if the
/// input holds it, or the state goes back to that point to wait for more.
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
        Header,
        Commands,
        Padding,
        Done,
    }

    private StreamPhase _phase;

    /// <summary>
    /// The bit of the input where the next step starts: in the commands, where the last stop left the
    /// bit reader, which goes on from there with the bytes of the input as they are then, never with the
    /// zeros a reader reads past the end of the input.
    /// </summary>
    private long _bitPosition;

    private bool _lastMetaBlock;

    /// <summary>The state at the last point the fast loop stopped at.</summary>
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
                return StreamHeader(input, inputLength, history, historyCapacity);

            case StreamPhase.Commands:
                return StreamCommands(input, inputLength, history, historyCapacity);

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

    private StreamResult StreamHeader(byte* input, long inputLength, byte* history, int historyCapacity)
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
        }

        if (metadata)
        {
            if (SkipBytes(_metaBlockRemaining) != BrotliError.None)
            {
                return StreamResult.NeedInput;
            }

            // A metadata block can be the last one, as an empty one can.
            _bitPosition = ReaderPosition(input);
            _phase = last ? StreamPhase.Padding : StreamPhase.Header;
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

        if (uncompressed)
        {
            return StreamUncompressed(input, history);
        }

        error = DecodeCompressedHeader();
        if (_br.Overrun || error == BrotliError.Truncated)
        {
            return StreamResult.NeedInput;
        }

        if (error != BrotliError.None)
        {
            return Failed(error);
        }

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
        _lastMetaBlock = last;
        _bitPosition = ReaderPosition(input);
        _phase = StreamPhase.Commands;
        SaveCheckpoint();
        return StreamResult.Progress;
    }

    private StreamResult StreamUncompressed(byte* input, byte* history)
    {
        // Its bytes all in the input, then copied; the header is read again until they are.
        if (CopyUncompressed(history) != BrotliError.None)
        {
            return StreamResult.NeedInput;
        }

        _bitPosition = ReaderPosition(input);
        return StreamResult.Progress;
    }

    private StreamResult StreamCommands(byte* input, long inputLength, byte* history, int historyCapacity)
    {
        // A bit reader at the last stop, which holds the rest of its byte and nothing else: the bits past it
        // come from the input as it is now.
        long start = _bitPosition >> 3;
        _br = new BitReader(input + start, (int)(inputLength - start));
        int used = (int)(_bitPosition & 7);
        if (used != 0)
        {
            _br.Bits = (ulong)(*_br.Next >> used);
            _br.Count = 8 - used;
            _br.Next++;
        }

        bool wide = _literalGroup.RootBits == WideLiteralRootBits;
        int result = wide ? Commands<FastMode, WideRoot>(history, historyCapacity, _resume) : Commands<FastMode, NarrowRoot>(history, historyCapacity, _resume);
        if (result == SwitchToCareful)
        {
            // A stop the input can resume from; the careful loop finishes the meta-block if the input holds it.
            _bitPosition = ReaderPosition(input);
            SaveCheckpoint();
            result = wide ? Commands<CarefulMode, WideRoot>(history, historyCapacity, _resume) : Commands<CarefulMode, NarrowRoot>(history, historyCapacity, _resume);
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
