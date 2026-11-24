using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's <c>BrotliEncoderState</c> for qualities 2 to 9 (<c>c/enc/encode.c</c>): the input copied
/// into the ring buffer a block at a time, backward references searched block by block, and the blocks
/// merged into meta-blocks, which <see cref="EncodeData"/> writes.
/// </summary>
internal sealed unsafe partial class EncoderState
{
    /// <summary>brotli's <c>MAX_NUM_DELAYED_SYMBOLS</c>.</summary>
    private const int MaxNumDelayedSymbols = 0x2FFF;

    private readonly RingBuffer _ringBuffer = new();
    private readonly HasherMemory _hasherMemory = new();
    private readonly PinnedBuffer<byte> _storage = new();
    private readonly PinnedBuffer<int> _caches = new();
    private readonly PinnedBuffer<uint> _contextArena = new();
    private readonly MetaBlockSplit _mb = new();
    private readonly GreedyMetaBlockBuilder _greedy = new();
    private readonly MetaBlockStorage _metaBlockStorage = new();

    private Command[] _commandsArray = [];
    private nuint _cmdAllocSize;

    private int _quality;
    private int _lgwin;
    private int _lgblock;
    private BrotliMode _mode;
    private nuint _sizeHint;
    private DistanceParams _dist;

    private ulong _inputPos;
    private ulong _lastFlushPos;
    private ulong _lastProcessedPos;
    private nuint _numCommands;
    private nuint _numLiterals;
    private nuint _lastInsertLen;
    private byte _prevByte;
    private byte _prevByte2;
    private ushort _lastBytes;
    private int _lastBytesBits;
    private bool _isLastBlockEmitted;

    /// <summary>The 16 entries of brotli's <c>dist_cache_</c>, then the 4 of <c>saved_dist_cache_</c>.</summary>
    private int* _distCache;
    private int* _savedDistCache;

    private int _hasherType;
    private bool _hasherSetup;
    private bool _hasherPrepared;
    private HashLongestMatchQuickly<H2> _h2;
    private HashLongestMatchQuickly<H3> _h3;
    private HashLongestMatchQuickly<H4> _h4;
    private HashLongestMatchQuickly<H54> _h54;
    private HashLongestMatch _h5;
    private HashLongestMatch64 _h6;
    private HashForgetfulChain<H40> _h40;
    private HashForgetfulChain<H41> _h41;
    private HashForgetfulChain<H42> _h42;
    private HashToBinaryTree _h10;
    private Zopfli? _zopfli;
    private HqMetaBlockBuilder? _hqBuilder;

    /// <summary>The window, from the parameters: brotli's <c>params.lgwin</c>.</summary>
    public int WindowLog => _lgwin;

    /// <summary>
    /// brotli's <c>BrotliEncoderInitState</c> and <c>EnsureInitialized</c>: a new stream, at
    /// <paramref name="quality"/> 2 to 11.
    /// </summary>
    public void Reset(int quality, int lgwin, BrotliMode mode, nuint sizeHint)
    {
        _quality = quality;
        _lgwin = lgwin;
        _mode = mode;
        _sizeHint = sizeHint;

        // ComputeLgBlock, with lgblock 0.
        if (quality < 4)
        {
            _lgblock = 14;
        }
        else
        {
            _lgblock = 16;
            if (quality >= 9 && lgwin > _lgblock)
            {
                _lgblock = Math.Min(18, lgwin);
            }
        }

        // ChooseDistanceParams: NPOSTFIX 1 and NDIRECT 12 for fonts, from quality 4.
        _dist = quality >= 4 && mode == BrotliMode.Font ? DistanceParams.Create(1, 12) : DistanceParams.Create(0, 0);

        _inputPos = 0;
        _lastFlushPos = 0;
        _lastProcessedPos = 0;
        _numCommands = 0;
        _numLiterals = 0;
        _lastInsertLen = 0;
        _prevByte = 0;
        _prevByte2 = 0;
        _isLastBlockEmitted = false;
        _hasherSetup = false;
        _hasherPrepared = false;
        _distCache = _caches.Ensure(16 + 4);
        _savedDistCache = _distCache + 16;
        _distCache[0] = 4;
        _distCache[1] = 11;
        _distCache[2] = 15;
        _distCache[3] = 16;
        new Span<int>(_distCache, 4).CopyTo(new Span<int>(_savedDistCache, 4));

        // ComputeRbBits.
        _ringBuffer.Setup(1 + Math.Max(lgwin, _lgblock), _lgblock);
        EncodeWindowBits(lgwin, out _lastBytes, out _lastBytesBits);
    }

    /// <summary>brotli's <c>EncodeWindowBits</c>, outside the large window.</summary>
    public static void EncodeWindowBits(int lgwin, out ushort lastBytes, out int lastBytesBits)
    {
        if (lgwin == 16)
        {
            lastBytes = 0;
            lastBytesBits = 1;
        }
        else if (lgwin == 17)
        {
            lastBytes = 1;
            lastBytesBits = 7;
        }
        else if (lgwin > 17)
        {
            lastBytes = (ushort)(((lgwin - 17) << 1) | 0x01);
            lastBytesBits = 4;
        }
        else
        {
            lastBytes = (ushort)(((lgwin - 8) << 4) | 0x01);
            lastBytesBits = 7;
        }
    }

    /// <summary>brotli's <c>InputBlockSize</c>.</summary>
    public nuint InputBlockSize => (nuint)1 << _lgblock;

    /// <summary>brotli's <c>RemainingInputBlockSize</c>.</summary>
    public nuint RemainingInputBlockSize
    {
        get
        {
            ulong delta = _inputPos - _lastProcessedPos;
            nuint blockSize = InputBlockSize;
            return delta >= blockSize ? 0 : blockSize - (nuint)delta;
        }
    }

    /// <summary>brotli's <c>WrapPosition</c>: a 32-bit position that keeps the "not the first lap" bit.</summary>
    private static uint WrapPosition(ulong position)
    {
        uint result = (uint)position;
        ulong gb = position >> 30;
        if (gb > 2)
        {
            result = (result & ((1u << 30) - 1)) | ((((uint)(gb - 1) & 1) + 1) << 30);
        }

        return result;
    }

    /// <summary>brotli's <c>CopyInputToRingBuffer</c>.</summary>
    public void CopyInputToRingBuffer(byte* input, nuint size)
    {
        _ringBuffer.Write(input, size);
        _inputPos += size;

        // In the first lap, seven zeros past the input, which the 8-byte hashing reads.
        if (_ringBuffer.FirstLap)
        {
            new Span<byte>(_ringBuffer.Buffer + _ringBuffer.Pos, 7).Clear();
        }
    }

    /// <summary>brotli's <c>UpdateLastProcessedPos</c>: true when the position wraps.</summary>
    private bool UpdateLastProcessedPos()
    {
        uint wrappedLastProcessedPos = WrapPosition(_lastProcessedPos);
        uint wrappedInputPos = WrapPosition(_inputPos);
        _lastProcessedPos = _inputPos;
        return wrappedInputPos < wrappedLastProcessedPos;
    }

    private byte* Storage(nuint size) => _storage.Ensure(size);

    /// <summary>
    /// brotli's <c>EncodeData</c>: processes the input copied so far, and writes a meta-block when it is
    /// the last, a flush is asked, or the meta-block is full; otherwise the input waits for the next block.
    /// </summary>
    /// <returns>The meta-block's bytes, in the state's storage, which hold until the next call; 0 when none.</returns>
    public nuint EncodeData(bool isLast, bool forceFlush, out byte* output)
    {
        ulong delta = _inputPos - _lastProcessedPos;
        uint bytes = (uint)delta;
        uint wrappedLastProcessedPos = WrapPosition(_lastProcessedPos);
        byte* data = _ringBuffer.Buffer;
        nuint mask = _ringBuffer.Mask;
        output = null;

        if (delta == 0)
        {
            if (data == null)
            {
                if (isLast)
                {
                    // The empty stream.
                    _lastBytes |= (ushort)(3u << _lastBytesBits);
                    _lastBytesBits += 2;
                    byte* tiny = Storage(16);
                    tiny[0] = (byte)_lastBytes;
                    tiny[1] = (byte)(_lastBytes >> 8);
                    output = tiny;
                    return (nuint)((_lastBytesBits + 7) >> 3);
                }

                return 0;
            }

            if (!isLast && !forceFlush)
            {
                return 0;
            }
        }

        if (_isLastBlockEmitted)
        {
            // brotli returns false; BrotliStreamEncoder never asks once the last block is out.
            throw new UnreachableException("a block after the last");
        }

        if (isLast)
        {
            _isLastBlockEmitted = true;
        }

        // Commands: one per two bytes at most.
        {
            nuint newSize = _numCommands + (bytes / 2) + 1;
            if (newSize > _cmdAllocSize)
            {
                newSize += (bytes / 4) + 16;
                _cmdAllocSize = newSize;
                Command[] commands = GC.AllocateUninitializedArray<Command>(checked((int)newSize), pinned: true);
                _commandsArray.AsSpan(0, (int)_numCommands).CopyTo(commands);
                _commandsArray = commands;
            }
        }

        Command* commandsBase = (Command*)Unsafe.AsPointer(ref _commandsArray[0]);
        switch (_hasherType = HasherSetupType(wrappedLastProcessedPos == 0 && isLast, bytes))
        {
            case 10: ProcessBlockZopfli(data, mask, ref bytes, ref wrappedLastProcessedPos, commandsBase); break;
            case 2: ProcessBlock(ref _h2, data, mask, ref bytes, ref wrappedLastProcessedPos, isLast, commandsBase); break;
            case 3: ProcessBlock(ref _h3, data, mask, ref bytes, ref wrappedLastProcessedPos, isLast, commandsBase); break;
            case 4: ProcessBlock(ref _h4, data, mask, ref bytes, ref wrappedLastProcessedPos, isLast, commandsBase); break;
            case 54: ProcessBlock(ref _h54, data, mask, ref bytes, ref wrappedLastProcessedPos, isLast, commandsBase); break;
            case 5: ProcessBlock(ref _h5, data, mask, ref bytes, ref wrappedLastProcessedPos, isLast, commandsBase); break;
            case 6: ProcessBlock(ref _h6, data, mask, ref bytes, ref wrappedLastProcessedPos, isLast, commandsBase); break;
            case 40: ProcessBlock(ref _h40, data, mask, ref bytes, ref wrappedLastProcessedPos, isLast, commandsBase); break;
            case 41: ProcessBlock(ref _h41, data, mask, ref bytes, ref wrappedLastProcessedPos, isLast, commandsBase); break;
            default: ProcessBlock(ref _h42, data, mask, ref bytes, ref wrappedLastProcessedPos, isLast, commandsBase); break;
        }

        {
            nuint maxLength = MaxMetablockSize;
            nuint maxLiterals = maxLength / 8;
            nuint maxCommands = maxLength / 8;
            ulong processedBytes = _inputPos - _lastFlushPos;

            // Flush now when a whole next block would not fit the meta-block.
            bool nextInputFitsMetablock = processedBytes + InputBlockSize <= maxLength;

            // Without block splits, flush once there are enough commands and literals.
            bool shouldFlush = _quality < 4 && _numLiterals + _numCommands >= MaxNumDelayedSymbols;
            if (!isLast && !forceFlush && !shouldFlush && nextInputFitsMetablock && _numLiterals < maxLiterals && _numCommands < maxCommands)
            {
                // Merged with the next block.
                if (UpdateLastProcessedPos())
                {
                    _hasherPrepared = false;
                }

                return 0;
            }
        }

        // The last insert, without a copy.
        if (_lastInsertLen > 0)
        {
            Commands.InitInsert(commandsBase + _numCommands++, _lastInsertLen);
            _numLiterals += _lastInsertLen;
            _lastInsertLen = 0;
        }

        if (!isLast && _inputPos == _lastFlushPos)
        {
            return 0;
        }

        {
            uint metablockSize = (uint)(_inputPos - _lastFlushPos);
            byte* storage = Storage((2 * (nuint)metablockSize) + 503);
            int storageIx = _lastBytesBits;
            storage[0] = (byte)_lastBytes;
            storage[1] = (byte)(_lastBytes >> 8);
            // brotli's ChooseContextMode, which reads the meta-block's bytes only: signed contexts for
            // the data of qualities 10 and 11 that is not mostly UTF-8.
            int literalContextMode = _quality >= 10 && !LiteralCost.IsMostlyUtf8(data, WrapPosition(_lastFlushPos), mask, (nuint)(_inputPos - _lastFlushPos), LiteralCost.MinUtf8Ratio)
                ? ContextModeSigned
                : ContextModeUtf8;
            WriteMetaBlockInternal(data, mask, _lastFlushPos, metablockSize, isLast, literalContextMode, commandsBase, ref storageIx, storage);
            AssertWithinStorage(storageIx, metablockSize);
            _lastBytes = storage[storageIx >> 3];
            _lastBytesBits = storageIx & 7;
            _lastFlushPos = _inputPos;
            if (UpdateLastProcessedPos())
            {
                _hasherPrepared = false;
            }

            if (_lastFlushPos > 0)
            {
                _prevByte = data[((uint)_lastFlushPos - 1) & mask];
            }

            if (_lastFlushPos > 1)
            {
                _prevByte2 = data[(uint)(_lastFlushPos - 2) & mask];
            }

            _numCommands = 0;
            _numLiterals = 0;
            new Span<int>(_distCache, 4).CopyTo(new Span<int>(_savedDistCache, 4));
            output = storage;
            return (nuint)(storageIx >> 3);
        }
    }

    /// <summary>brotli's <c>MaxMetablockSize</c>.</summary>
    private nuint MaxMetablockSize => (nuint)1 << Math.Min(1 + Math.Max(_lgwin, _lgblock), 24);

    /// <summary>brotli's <c>ChooseHasher</c>, run once a stream, at its first block, by <c>HasherSetup</c>.</summary>
    private int HasherSetupType(bool oneShot, nuint inputSize)
    {
        if (_hasherSetup)
        {
            return _hasherType;
        }

        int type;
        if (_quality > 9)
        {
            type = 10;
        }
        else if (_quality == 4 && _sizeHint >= (1 << 20))
        {
            type = 54;
        }
        else if (_quality < 5)
        {
            type = _quality;
        }
        else if (_lgwin <= 16)
        {
            type = _quality < 7 ? 40 : _quality < 9 ? 41 : 42;
        }
        else if (_sizeHint >= (1 << 20) && _lgwin >= 19)
        {
            type = 6;
        }
        else
        {
            type = 5;
        }

        int numLastDistancesToCheck = _quality < 7 ? 4 : _quality < 9 ? 10 : 16;
        switch (type)
        {
            case 10:
                _h10.Initialize(_hasherMemory, _lgwin, oneShot, inputSize);
                (_zopfli ??= new Zopfli()).Setup(_quality, _lgwin);
                break;
            case 2: _h2.Initialize(_hasherMemory); break;
            case 3: _h3.Initialize(_hasherMemory); break;
            case 4: _h4.Initialize(_hasherMemory); break;
            case 54: _h54.Initialize(_hasherMemory); break;
            case 5: _h5.Initialize(_hasherMemory, _quality < 7 ? 14 : 15, _quality - 1, numLastDistancesToCheck); break;
            case 6: _h6.Initialize(_hasherMemory, 15, _quality - 1, numLastDistancesToCheck); break;
            case 40: _h40.Initialize(_hasherMemory, _quality); break;
            case 41: _h41.Initialize(_hasherMemory, _quality); break;
            default: _h42.Initialize(_hasherMemory, _quality); break;
        }

        _hasherSetup = true;
        _hasherPrepared = false;
        return type;
    }

    /// <summary>
    /// The hasher's part of <c>EncodeData</c>: <c>InitOrStitchToPreviousBlock</c>, <c>ExtendLastCommand</c>
    /// and <c>BrotliCreateBackwardReferences</c>.
    /// </summary>
    private void ProcessBlock<THasher>(ref THasher hasher, byte* data, nuint mask, ref uint bytes, ref uint wrappedLastProcessedPos, bool isLast, Command* commands)
        where THasher : struct, IHasher
    {
        if (!_hasherPrepared)
        {
            bool oneShot = wrappedLastProcessedPos == 0 && isLast;
            hasher.Prepare(oneShot, bytes, data);
            _hasherPrepared = true;
        }

        hasher.StitchToPreviousBlock(bytes, wrappedLastProcessedPos, data, mask);
        if (_numCommands != 0 && _lastInsertLen == 0)
        {
            ExtendLastCommand(commands, ref bytes, ref wrappedLastProcessedPos);
        }

        _ringBuffer.AssertBlock(wrappedLastProcessedPos, bytes);
        BackwardReferences.Create(ref hasher, bytes, wrappedLastProcessedPos, data, mask, _quality, _lgwin, _dist, _distCache, ref _lastInsertLen, commands + _numCommands, ref _numCommands, ref _numLiterals);
    }

    /// <summary>The block of qualities 10 and 11: the binary tree hasher, and Zopfli's backward references.</summary>
    private void ProcessBlockZopfli(byte* data, nuint mask, ref uint bytes, ref uint wrappedLastProcessedPos, Command* commands)
    {
        if (!_hasherPrepared)
        {
            _h10.Prepare();
            _hasherPrepared = true;
        }

        _h10.StitchToPreviousBlock(bytes, wrappedLastProcessedPos, data, mask);
        if (_numCommands != 0 && _lastInsertLen == 0)
        {
            ExtendLastCommand(commands, ref bytes, ref wrappedLastProcessedPos);
        }

        _ringBuffer.AssertBlock(wrappedLastProcessedPos, bytes);
        if (_quality == 10)
        {
            _zopfli!.CreateBackwardReferences(bytes, wrappedLastProcessedPos, data, mask, _dist, ref _h10, _distCache, ref _lastInsertLen, commands + _numCommands, ref _numCommands, ref _numLiterals);
        }
        else
        {
            _zopfli!.CreateHqBackwardReferences(bytes, wrappedLastProcessedPos, data, mask, _dist, ref _h10, _distCache, ref _lastInsertLen, commands + _numCommands, ref _numCommands, ref _numLiterals);
        }
    }

    /// <summary>brotli's <c>ExtendLastCommand</c>: the last copy, longer into the new block when it can be.</summary>
    private void ExtendLastCommand(Command* commands, ref uint bytes, ref uint wrappedLastProcessedPos)
    {
        Command* lastCommand = commands + _numCommands - 1;
        byte* data = _ringBuffer.Buffer;
        uint mask = _ringBuffer.Mask;
        ulong maxBackwardDistance = (1ul << _lgwin) - Prefix.WindowGap;
        ulong lastCopyLen = lastCommand->CopyLen & 0x1FFFFFF;
        ulong lastProcessedPos = _lastProcessedPos - lastCopyLen;
        ulong maxDistance = lastProcessedPos < maxBackwardDistance ? lastProcessedPos : maxBackwardDistance;
        ulong cmdDist = (ulong)(long)_distCache[0];
        uint distanceCode = Commands.RestoreDistanceCode(lastCommand, _dist);
        if (distanceCode < Prefix.NumDistanceShortCodes || distanceCode - (Prefix.NumDistanceShortCodes - 1) == cmdDist)
        {
            if (cmdDist <= maxDistance)
            {
                while (bytes != 0 && data[wrappedLastProcessedPos & mask] == data[(ulong)(wrappedLastProcessedPos - cmdDist) & mask])
                {
                    lastCommand->CopyLen++;
                    bytes--;
                    wrappedLastProcessedPos++;
                }
            }

            lastCommand->CmdPrefix = Commands.GetLengthCode(
                lastCommand->InsertLen,
                (nuint)(nint)((int)(lastCommand->CopyLen & 0x1FFFFFF) + (int)(lastCommand->CopyLen >> 25)),
                (lastCommand->DistPrefix & 0x3FF) == 0);
        }
    }
}
