using System;
using System.Runtime.Intrinsics;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>Frames compressed with a dictionary: libzstd's prepared dictionaries (<c>ZSTD_CDict</c>).</summary>
public sealed unsafe partial class ZstdCompressor
{
    /// <summary>
    /// libzstd's <c>ZSTD_USE_CDICT_PARAMS_SRCSIZE_CUTOFF</c>: below it, a frame uses the prepared
    /// dictionary's tables; from it, and from <see cref="LoadDictionaryMultiplier"/> times the
    /// dictionary, it loads the dictionary into tables of its own parameters.
    /// </summary>
    private const ulong PreparedTablesCutoff = 128 << 10;

    /// <summary>libzstd's <c>ZSTD_USE_CDICT_PARAMS_DICTSIZE_MULTIPLIER</c>.</summary>
    private const ulong LoadDictionaryMultiplier = 6;

    private readonly CompressionDictionary? _dictionary;

    /// <summary>
    /// The frame's parameters before a dictionary's replace them in its tables: the post-block
    /// splitter, as the header's window, follows them.
    /// </summary>
    private CompressionParameters _frameParameters;

    /// <summary>Creates a compressor at <paramref name="level"/> that compresses every frame with <paramref name="dictionary"/>.</summary>
    /// <param name="level">From <see cref="MinLevel"/> to <see cref="MaxLevel"/>; 0 is the <see cref="DefaultLevel"/>.</param>
    /// <param name="dictionary">
    /// A dictionary in the zstd format (as zstd's trainer writes them: identifier, entropy tables,
    /// content), or raw content of identifier 0; empty for none.
    /// </param>
    /// <remarks>
    /// The frames are those libzstd writes with the dictionary prepared at the same level
    /// (<c>ZSTD_createCDict</c>, then <c>ZSTD_CCtx_refCDict</c>), byte for byte: the dictionary's level
    /// is the frames' level. The dictionary is prepared once, here.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    /// <exception cref="System.IO.InvalidDataException">A zstd-format dictionary whose tables are invalid.</exception>
    public ZstdCompressor(int level, ReadOnlySpan<byte> dictionary)
        : this(level)
    {
        if (!dictionary.IsEmpty)
        {
            _dictionary = CompressionDictionary.Create(dictionary, Level);
        }
    }

    /// <summary>
    /// libzstd's <c>attachDictSizeCutoffs</c>: up to these sizes, by the dictionary's strategy, a frame
    /// searches the prepared dictionary's tables in place rather than copying them.
    /// </summary>
    private static ulong AttachCutoff(Strategy strategy) => strategy switch
    {
        Strategy.DoubleFast => 16 << 10,
        Strategy.Greedy or Strategy.Lazy or Strategy.Lazy2 or Strategy.BinaryTreeLazy2 or Strategy.BinaryTreeOptimal => 32 << 10,
        _ => 8 << 10,
    };

    /// <summary>
    /// libzstd's start of a frame with a prepared dictionary (<c>ZSTD_CCtx_init_compressStream2</c>, then
    /// <c>ZSTD_compressBegin_internal</c>), in one of three ways by the source's size:
    /// <list type="bullet">
    /// <item>attached (<c>ZSTD_resetCCtx_byAttachingCDict</c>): small frames search the dictionary's
    /// tables in place, beside their own, sized for the source alone;</item>
    /// <item>copied (<c>ZSTD_resetCCtx_byCopyingCDict</c>): the dictionary's tables become the frame's,
    /// the dictionary a segment of its own below the source;</item>
    /// <item>loaded (<c>ZSTD_compress_insertDictionary</c>): from 128 KiB and six times the dictionary,
    /// the frame loads it into tables of its own parameters, again as a segment of its own.</item>
    /// </list>
    /// The frame starts from the dictionary's block state. The long-distance matcher, on as the frame's
    /// parameters decide, takes the parameters of the frame's tables and starts afresh at the source;
    /// a frame that loads a raw-content dictionary loads it into the matcher too.
    /// </summary>
    /// <param name="source">The frame's content.</param>
    /// <param name="sourceSize">Its size.</param>
    /// <param name="inJob">
    /// Whether the frame is started by its first job, which leaves the long-distance matcher to the
    /// frame's (zstdmt's serial state).
    /// </param>
    /// <returns>The frame's parameters: its window.</returns>
    private CompressionParameters BeginDictionaryFrame(byte* source, int sourceSize, bool inJob = false) =>
        BeginDictionaryFrame(PlanDictionaryFrame((ulong)sourceSize, inJob), source, sourceSize, stream: false);

    /// <summary>
    /// How a frame of <paramref name="size"/> bytes uses the prepared dictionary, as libzstd decides it
    /// before the frame's first byte (<c>ZSTD_shouldAttachDict</c>, <c>ZSTD_compressBegin_internal</c>):
    /// attached up to the strategy's cutoff, and for a frame of unknown size
    /// (<see cref="CompressionParameters.UnknownSize"/>); with the dictionary's tables below 128 KiB,
    /// below six times the dictionary, and for a frame of unknown size; else loaded.
    /// </summary>
    private DictionaryFramePlan PlanDictionaryFrame(ulong size, bool inJob)
    {
        CompressionDictionary dictionary = _dictionary!;
        CompressionParameters prepared = dictionary.Parameters;
        bool unknown = size == CompressionParameters.UnknownSize;
        bool attach = unknown || size <= AttachCutoff(prepared.Strategy);
        CompressionParameters frame = CompressionParameters.ForStream(dictionary.Level, size, (ulong)dictionary.Size, attach, LongDistanceMatching, _windowLog);
        bool longDistance = !inJob && UsesLongDistanceMatcher(frame);
        bool usePreparedTables = unknown || size < PreparedTablesCutoff || size < (ulong)dictionary.Size * LoadDictionaryMultiplier;
        CompressionParameters tables = !usePreparedTables ? frame
            : attach ? prepared.ForAttachedSource(size, (ulong)dictionary.Size, frame.WindowLog)
            : prepared.WithWindow(frame.WindowLog);
        if (longDistance && tables.Strategy < Strategy.BinaryTreeOptimal)
        {
            throw new NotSupportedException("Long-distance matching is implemented for the optimal parsers only.");
        }

        return new DictionaryFramePlan(frame, tables, attach, usePreparedTables, longDistance);
    }

    /// <summary>The frame's parameters, its tables', and how it uses the prepared dictionary.</summary>
    private readonly record struct DictionaryFramePlan(
        CompressionParameters Frame, CompressionParameters Tables, bool Attach, bool UsePreparedTables, bool LongDistance);

    /// <summary>
    /// The start of a frame with the prepared dictionary, as <paramref name="plan"/> decided it: see
    /// <see cref="BeginDictionaryFrame(byte*, int, bool)"/>. A streamed frame (<paramref name="stream"/>)
    /// starts at its first chunk, of a size yet unknown.
    /// </summary>
    private CompressionParameters BeginDictionaryFrame(in DictionaryFramePlan plan, byte* source, int sourceSize, bool stream)
    {
        CompressionDictionary dictionary = _dictionary!;
        CompressionParameters frame = plan.Frame;
        CompressionParameters tables = plan.Tables;
        bool longDistance = plan.LongDistance;
        bool attach = plan.Attach;
        bool usePreparedTables = plan.UsePreparedTables;
        if (usePreparedTables && attach)
        {
            uint dictionaryEnd = dictionary.State->End;
            bool hasContent = dictionary.ContentLength != 0;
            BeginFrame(tables, source, sourceSize, longDistance, hasContent ? dictionaryEnd : WindowStartIndex, stream: stream);
            if (hasContent)
            {
                _matchState.LoadedDictEnd = _matchState.DictLimit;
                _matchState.Dictionary = dictionary.State;
            }
        }
        else if (usePreparedTables)
        {
            BeginFrame(tables, source, sourceSize, longDistance, stream: stream);
            CopyDictionaryTables(dictionary);

            // The dictionary's window, then the source past its end: not contiguous.
            MatchState* state = dictionary.State;
            uint dictionaryEnd = state->End;
            _matchState.DictionaryBase = state->Base;
            _matchState.LowLimit = state->DictLimit;
            _matchState.DictLimit = dictionaryEnd;
            _matchState.Base = source - dictionaryEnd;
            _matchState.NextToUpdate = dictionaryEnd;
            _matchState.LoadedDictEnd = state->LoadedDictEnd;
            if (dictionaryEnd - _matchState.LowLimit < MatchFinder.HashReadSize)
            {
                // libzstd's ZSTD_window_update: an extDict too small to search.
                _matchState.LowLimit = dictionaryEnd;
            }

            // As BeginFrame reserves them, by the strategy that runs: the dictionary's. A dictionary
            // too small to search leaves no segment below the source, and btultra2's first pass runs.
            if (!stream)
            {
                _nextIndex = dictionaryEnd + (uint)sourceSize + (tables.Strategy == Strategy.BinaryTreeUltra2 ? (uint)FrameFormat.MaxBlockSize : 0);
            }
        }
        else
        {
            BeginFrame(frame, source, sourceSize, longDistance, loadedIndices: dictionary.FullContentLength, stream: stream);
            if (longDistance && dictionary.IsRawContent)
            {
                _longDistanceMatcher!.LoadDictionary(dictionary.FullContent, dictionary.FullContentLength, source);
            }

            uint start = _matchState.DictLimit;
            CompressionDictionary.LoadContent(ref _matchState, dictionary.FullContent, dictionary.FullContentLength, preparedDictionary: false);
            if (_matchState.End != start)
            {
                // The source past the content: not contiguous.
                uint contentEnd = _matchState.End;
                _matchState.DictionaryBase = _matchState.Base;
                _matchState.LowLimit = start;
                _matchState.DictLimit = contentEnd;
                _matchState.Base = source - contentEnd;
                _matchState.NextToUpdate = contentEnd;
                if (contentEnd - start < MatchFinder.HashReadSize)
                {
                    _matchState.LowLimit = contentEnd;
                }

                if (!stream)
                {
                    _nextIndex = contentEnd + (uint)sourceSize + (tables.Strategy == Strategy.BinaryTreeUltra2 ? (uint)FrameFormat.MaxBlockSize : 0);
                }
            }
        }

        _frameParameters = frame;
        _previous.CopyFrom(dictionary.Entropy);
        return frame;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_selectBlockCompressor</c> with a dictionary: the extDict search when a segment
    /// lies below the prefix, else the attached one.
    /// </summary>
    /// <returns>The size of the literals after the last sequence.</returns>
    private nuint FindSequencesWithDictionary(byte* source, int size, uint* rep)
    {
        bool extDict = _matchState.LowLimit < _matchState.DictLimit;
        return _parameters.Strategy switch
        {
            Strategy.Fast => extDict
                ? FastMatchFinder.CompressBlockExtDict(ref _matchState, _store, rep, source, (nuint)size)
                : FastMatchFinder.CompressBlockAttached(ref _matchState, _store, rep, source, (nuint)size),
            Strategy.DoubleFast => extDict
                ? DoubleFastMatchFinder.CompressBlockExtDict(ref _matchState, _store, rep, source, (nuint)size)
                : DoubleFastMatchFinder.CompressBlockAttached(ref _matchState, _store, rep, source, (nuint)size),
            Strategy.Greedy or Strategy.Lazy or Strategy.Lazy2 or Strategy.BinaryTreeLazy2 =>
                LazyMatchFinder.CompressBlockWithDictionary(ref _matchState, _store, rep, source, (nuint)size, extDict),
            _ => OptimalMatchFinder.CompressBlockWithDictionary(ref _matchState, _optimal!, _store, rep, source, (nuint)size, extDict),
        };
    }

    /// <summary>
    /// The copy of <c>ZSTD_resetCCtx_byCopyingCDict</c>: the prepared dictionary's tables into the
    /// frame's, the tags of the fast ones stripped; the 3-byte table, which a dictionary never fills,
    /// cleared.
    /// </summary>
    private void CopyDictionaryTables(CompressionDictionary dictionary)
    {
        MatchState* prepared = dictionary.State;
        CompressionParameters parameters = dictionary.Parameters;
        bool tagged = parameters.Strategy <= Strategy.DoubleFast;
        int hashSize = 1 << parameters.HashLog;
        CopyTable(_matchState.HashTable, prepared->HashTable, hashSize, tagged);
        if (parameters.Strategy != Strategy.Fast && !parameters.UsesRowMatchFinder)
        {
            CopyTable(_matchState.ChainTable, prepared->ChainTable, 1 << parameters.ChainLog, tagged);
        }

        if (parameters.UsesRowMatchFinder)
        {
            // The dictionary's rows and tags, hashed without salt: the frame's hashes find them so.
            Buffer.MemoryCopy(prepared->TagTable, _matchState.TagTable, hashSize, hashSize);
            _matchState.RowHashSalt = 0;
        }

        if (_matchState.HashLog3 != 0)
        {
            new Span<uint>(_matchState.HashTable3, 1 << _matchState.HashLog3).Clear();
        }

        // The indices restart at the dictionary's: past what was copied, the tables may hold higher ones.
        _hashTableValid = hashSize;
        _chainTableValid = parameters.Strategy != Strategy.Fast && !parameters.UsesRowMatchFinder ? 1 << parameters.ChainLog : 0;
        _hashTable3Valid = _matchState.HashLog3 != 0 ? 1 << _matchState.HashLog3 : 0;

        static void CopyTable(uint* destination, uint* source, int size, bool tagged)
        {
            if (!tagged)
            {
                Buffer.MemoryCopy(source, destination, (long)size * sizeof(uint), (long)size * sizeof(uint));
                return;
            }

            StripTags(destination, source, size);
        }
    }

    /// <summary>
    /// The tagged indices of <paramref name="source"/> into <paramref name="destination"/>, their tags
    /// stripped: a frame that copies a prepared dictionary's fast tables does so for every frame, over
    /// the whole tables (768 KiB at level 3), which the JIT does not vectorize by itself as clang does.
    /// </summary>
    internal static void StripTags(uint* destination, uint* source, int size)
    {
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            for (; i + 32 <= size; i += 32)
            {
                Vector512<uint> first = Vector512.Load(source + i);
                Vector512<uint> second = Vector512.Load(source + i + 16);
                Vector512.ShiftRightLogical(first, MatchFinder.TagBits).Store(destination + i);
                Vector512.ShiftRightLogical(second, MatchFinder.TagBits).Store(destination + i + 16);
            }
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 16 <= size; i += 16)
            {
                Vector256<uint> first = Vector256.Load(source + i);
                Vector256<uint> second = Vector256.Load(source + i + 8);
                Vector256.ShiftRightLogical(first, MatchFinder.TagBits).Store(destination + i);
                Vector256.ShiftRightLogical(second, MatchFinder.TagBits).Store(destination + i + 8);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 8 <= size; i += 8)
            {
                Vector128<uint> first = Vector128.Load(source + i);
                Vector128<uint> second = Vector128.Load(source + i + 4);
                Vector128.ShiftRightLogical(first, MatchFinder.TagBits).Store(destination + i);
                Vector128.ShiftRightLogical(second, MatchFinder.TagBits).Store(destination + i + 4);
            }
        }

        for (; i < size; i++)
        {
            destination[i] = source[i] >> MatchFinder.TagBits;
        }
    }
}
