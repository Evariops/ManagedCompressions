using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedCompressions.Brotli.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// A compressed meta-block's commands (RFC 7932, sections 4, 5, 8 and 9.3): each inserts literals,
/// then copies from the window or from the static dictionary, written straight into the destination.
/// </summary>
/// <remarks>
/// <para>
/// One loop, compiled twice. The fast one (<see cref="FastMode"/>) runs while
/// <see cref="FastInputSlack"/> bytes remain in the source at each command and each literal, which
/// covers every bit a step can read: its refills are single loads, unchecked. Within that distance of
/// the end it hands over to the careful one (<see cref="CarefulMode"/>), which resumes where the fast
/// one stopped, refills byte by byte and checks after each step whether it has read past the end.
/// </para>
/// <para>
/// The checks and their order are brotli's (<c>ProcessCommandsInternal</c>). Where brotli writes a
/// command into its ring buffer and only then finds that the meta-block has gone past its length,
/// this checks before writing: the stream is invalid either way.
/// </para>
/// </remarks>
public sealed unsafe partial class BrotliDecompressor
{
    /// <summary>The source bytes that must remain past <see cref="BitReader.Next"/> for a fast step.</summary>
    private const int FastInputSlack = 64;

    /// <summary>The bytes past a copy that a wide copy may write, and must therefore have room for.</summary>
    private const int CopySlack = 32;

    /// <summary>What <see cref="Commands{TMode}"/> returns when the fast loop hands over to the careful one.</summary>
    private const int SwitchToCareful = -1;

    private const int AtCommandBegin = 0;
    private const int AtLiterals = 1;

    private interface IMode
    {
    }

    /// <summary>The loop that reads whole words: enough input remains for any step.</summary>
    private struct FastMode : IMode
    {
    }

    /// <summary>The loop near the end of the input, which checks every read.</summary>
    private struct CarefulMode : IMode
    {
    }

    /// <summary>Where the careful loop resumes the fast one: <see cref="AtCommandBegin"/> or <see cref="AtLiterals"/>.</summary>
    private int _resume;

    /// <summary>The literals the current command has yet to insert.</summary>
    private int _insertRemaining;

    private int _copyLength;
    private int _distanceCode;
    private int _distanceContext;
    private int _distanceTreeIndex;
    private uint* _commandTree;
    private uint* _literalTree;
    private bool _trivialLiteralContext;
    private byte* _contextMapSlice;
    private byte* _contextLookup;
    private byte* _distContextMapSlice;

    private static byte* ContextLookup => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Context.Lookup));

    private static byte* DictionaryData => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(StaticDictionary.Data));

    private BrotliError DecodeCompressedMetaBlock(byte* dst, int dstLength)
    {
        BrotliError error = DecodeCompressedHeader();
        if (error != BrotliError.None)
        {
            return error;
        }

        if (_br.Overrun)
        {
            return BrotliError.Truncated;
        }

        PrepareLiteralDecoding();
        _distContextMapSlice = _distContextMap;
        _commandTree = (uint*)_commandGroup.Trees[0];
        int result = Commands<FastMode>(dst, dstLength, AtCommandBegin);
        if (result == SwitchToCareful)
        {
            result = Commands<CarefulMode>(dst, dstLength, _resume);
        }

        return (BrotliError)result;
    }

    private int Commands<TMode>(byte* dst, int dstLength, int resume)
        where TMode : struct, IMode
    {
        bool careful = typeof(TMode) == typeof(CarefulMode);
        BitReader br = _br;
        byte* guard = br.End - FastInputSlack;
        int pos = _pos;
        int insert = _insertRemaining;
        int result;
        ref short commandCodes = ref MemoryMarshal.GetReference(Prefix.CommandCodes);

        if (resume == AtLiterals)
        {
            goto Literals;
        }

    CommandBegin:
        if (!careful && br.Next > guard)
        {
            _resume = AtCommandBegin;
            goto Suspend;
        }

        if (_s.BlockLength[1] == 0)
        {
            _br = br;
            result = (int)DecodeBlockSwitch(1);
            br = _br;
            if (result != 0)
            {
                goto Fail;
            }

            _commandTree = (uint*)_commandGroup.Trees[_s.BlockTypeRb[3]];
            if (careful && br.Overrun)
            {
                goto Truncated;
            }

            goto CommandBegin;
        }

        {
            Refill<TMode>(ref br, Huffman.MaxCodeLength);
            int command = br.DecodeSymbol(_commandTree);
            ref short code = ref Unsafe.Add(ref commandCodes, command * 6);
            int insertExtraBits = code;
            int copyExtraBits = Unsafe.Add(ref code, 1);
            _distanceCode = Unsafe.Add(ref code, 2);
            _distanceContext = Unsafe.Add(ref code, 3);
            _distanceTreeIndex = _distContextMapSlice[_distanceContext];
            insert = Unsafe.Add(ref code, 4);
            if (insertExtraBits != 0)
            {
                Refill<TMode>(ref br, 24);
                insert += (int)br.Read(insertExtraBits);
            }

            Refill<TMode>(ref br, 24);
            _copyLength = Unsafe.Add(ref code, 5) + (int)br.Read(copyExtraBits);
            _s.BlockLength[1]--;
            if (careful && br.Overrun)
            {
                goto Truncated;
            }
        }

        if (insert == 0)
        {
            goto Distance;
        }

        if (insert > _metaBlockRemaining)
        {
            // brotli inserts the literals in its ring buffer, then refuses the meta-block for its length.
            result = (int)BrotliError.BlockLength;
            goto Fail;
        }

        _metaBlockRemaining -= insert;

    Literals:
        if (_trivialLiteralContext)
        {
            uint* tree = _literalTree;
            do
            {
                if (!careful && br.Next > guard)
                {
                    _resume = AtLiterals;
                    goto Suspend;
                }

                if (_s.BlockLength[0] == 0)
                {
                    goto LiteralBlockSwitch;
                }

                Refill<TMode>(ref br, Huffman.MaxCodeLength);
                dst[pos] = (byte)br.DecodeSymbol(tree);
                _s.BlockLength[0]--;
                pos++;
                if (careful && br.Overrun)
                {
                    goto Truncated;
                }
            }
            while (--insert != 0);
        }
        else
        {
            byte p1 = pos > 0 ? dst[pos - 1] : (byte)0;
            byte p2 = pos > 1 ? dst[pos - 2] : (byte)0;
            byte* lookup = _contextLookup;
            byte* slice = _contextMapSlice;
            nint* trees = _literalGroup.Trees;
            do
            {
                if (!careful && br.Next > guard)
                {
                    _resume = AtLiterals;
                    goto Suspend;
                }

                if (_s.BlockLength[0] == 0)
                {
                    goto LiteralBlockSwitch;
                }

                int context = lookup[p1] | lookup[256 + p2];
                uint* tree = (uint*)trees[slice[context]];
                Refill<TMode>(ref br, Huffman.MaxCodeLength);
                p2 = p1;
                p1 = (byte)br.DecodeSymbol(tree);
                dst[pos] = p1;
                _s.BlockLength[0]--;
                pos++;
                if (careful && br.Overrun)
                {
                    goto Truncated;
                }
            }
            while (--insert != 0);
        }

        if (_metaBlockRemaining <= 0)
        {
            goto MetaBlockDone;
        }

    Distance:
        if (_distanceCode >= 0)
        {
            // The implicit distance of codes 0 to 127: the last one, which is not pushed again.
            _distanceContext = _distanceCode != 0 ? 0 : 1;
            --_distRbIndex;
            _distanceCode = _s.DistRb[_distRbIndex & 3];
        }
        else
        {
            if (_s.BlockLength[2] == 0)
            {
                _br = br;
                result = (int)DecodeBlockSwitch(2);
                br = _br;
                if (result != 0)
                {
                    goto Fail;
                }

                _distContextMapSlice = _distContextMap + (_s.BlockTypeRb[5] << Prefix.DistanceContextBits);
                _distanceTreeIndex = _distContextMapSlice[_distanceContext];
            }

            Refill<TMode>(ref br, Huffman.MaxCodeLength);
            int code = br.DecodeSymbol((uint*)_distanceGroup.Trees[_distanceTreeIndex]);
            _s.BlockLength[2]--;
            _distanceContext = 0;
            if ((code & ~0xF) == 0)
            {
                _distanceCode = code;
                TakeDistanceFromRingBuffer();
            }
            else
            {
                Refill<TMode>(ref br, 24);
                uint extra = br.Read(_distExtraBits[code]);
                _distanceCode = _distOffset[code] + (int)(extra << _distancePostfixBits);
            }

            if (careful && br.Overrun)
            {
                goto Truncated;
            }
        }

        {
            int maxDistance = pos < _maxBackwardDistance ? pos : _maxBackwardDistance;
            int distance = _distanceCode;
            int length = _copyLength;
            if (distance > maxDistance)
            {
                result = (int)CopyDictionaryWord(dst, ref pos, maxDistance);
                if (result != 0)
                {
                    goto Fail;
                }
            }
            else
            {
                if (length > _metaBlockRemaining)
                {
                    // brotli copies, then refuses the meta-block for its length.
                    result = (int)BrotliError.BlockLength;
                    goto Fail;
                }

                _s.DistRb[_distRbIndex & 3] = distance;
                ++_distRbIndex;
                _metaBlockRemaining -= length;
                CopyMatch(dst, pos, distance, length, dstLength);
                pos += length;
            }
        }

        if (_metaBlockRemaining <= 0)
        {
            goto MetaBlockDone;
        }

        goto CommandBegin;

    LiteralBlockSwitch:
        _br = br;
        result = (int)DecodeBlockSwitch(0);
        br = _br;
        if (result != 0)
        {
            goto Fail;
        }

        PrepareLiteralDecoding();
        if (careful && br.Overrun)
        {
            goto Truncated;
        }

        goto Literals;

    MetaBlockDone:
        _br = br;
        _pos = pos;
        return 0;

    Suspend:
        _br = br;
        _pos = pos;
        _insertRemaining = insert;
        return SwitchToCareful;

    Truncated:
        _br = br;
        _pos = pos;
        return (int)BrotliError.Truncated;

    Fail:
        _br = br;
        _pos = pos;
        return br.Overrun ? (int)BrotliError.Truncated : result;
    }

    /// <summary>
    /// Tops the accumulator up when it holds fewer than <paramref name="bits"/> bits: with a single load
    /// in the fast loop, which guarantees the input; byte by byte near the end.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Refill<TMode>(ref BitReader br, int bits)
        where TMode : struct, IMode
    {
        if (br.Count < bits)
        {
            if (typeof(TMode) == typeof(FastMode))
            {
                br.RefillFast();
            }
            else
            {
                br.Refill();
            }
        }
    }

    /// <summary>The literal decoding state of the current literal block type.</summary>
    private void PrepareLiteralDecoding()
    {
        int blockType = _s.BlockTypeRb[1];
        _contextMapSlice = _contextMap + (blockType << Prefix.LiteralContextBits);
        _trivialLiteralContext = ((_s.TrivialLiteralContexts[blockType >> 5] >> (blockType & 31)) & 1) != 0;
        _literalTree = (uint*)_literalGroup.Trees[_contextMapSlice[0]];
        _contextLookup = ContextLookup + ((_contextModes[blockType] & 3) << 9);
    }

    /// <summary>
    /// A block switch command (RFC 7932, section 6) of category <paramref name="treeType"/>: the next
    /// block type, then its length.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private BrotliError DecodeBlockSwitch(int treeType)
    {
        int maxBlockType = _s.NumBlockTypes[treeType];
        if (maxBlockType <= 1)
        {
            return BrotliError.BlockSwitch;
        }

        int blockType = _br.ReadSymbol(_blockTypeTrees + (treeType * BlockTypeTableSize));
        _s.BlockLength[treeType] = ReadBlockLength(_blockLengthTrees + (treeType * BlockLengthTableSize));
        int last = _s.BlockTypeRb[(treeType * 2) + 1];
        if (blockType == 1)
        {
            blockType = last + 1;
        }
        else if (blockType == 0)
        {
            blockType = _s.BlockTypeRb[treeType * 2];
        }
        else
        {
            blockType -= 2;
        }

        if (blockType >= maxBlockType)
        {
            blockType -= maxBlockType;
        }

        _s.BlockTypeRb[treeType * 2] = last;
        _s.BlockTypeRb[(treeType * 2) + 1] = blockType;
        return BrotliError.None;
    }

    /// <summary>A distance of codes 0 to 15: one of the last four, possibly adjusted (RFC 7932, section 4).</summary>
    private void TakeDistanceFromRingBuffer()
    {
        int code = _distanceCode;
        if (code <= 3)
        {
            // Code 0 repeats the last distance, which is not pushed again: compensated in advance.
            _distanceContext = 1 >> code;
            _distanceCode = _s.DistRb[(_distRbIndex - (code - 3)) & 3];
            _distRbIndex -= _distanceContext;
        }
        else
        {
            int indexDelta = 3;
            int baseCode = code - 10;
            if (code < 10)
            {
                baseCode = code - 4;
            }
            else
            {
                indexDelta = 2;
            }

            int delta = ((0x605142 >> (4 * baseCode)) & 0xF) - 3;
            _distanceCode = _s.DistRb[(_distRbIndex + indexDelta) & 3] + delta;
            if (_distanceCode <= 0)
            {
                // Refused below as larger than any distance.
                _distanceCode = 0x7FFFFFFF;
            }
        }
    }

    /// <summary>
    /// A reference past the window: a word of the static dictionary, possibly transformed
    /// (RFC 7932, section 8), with brotli's checks in brotli's order.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private BrotliError CopyDictionaryWord(byte* dst, ref int pos, int maxDistance)
    {
        int distance = _distanceCode;
        int length = _copyLength;
        if (distance > Prefix.MaxAllowedDistance)
        {
            return BrotliError.Distance;
        }

        if (length < 4 || length > 24)
        {
            return BrotliError.Dictionary;
        }

        int shift = StaticDictionary.SizeBitsByLength[length];
        int offset = StaticDictionary.OffsetsByLength[length];
        int address = distance - maxDistance - 1;
        int wordIndex = address & ((1 << shift) - 1);
        int transform = address >> shift;

        // A dictionary reference does not push its distance: undo code 0's compensation.
        _distRbIndex += _distanceContext;
        offset += wordIndex * length;
        if (transform >= Transforms.Count)
        {
            return BrotliError.Transform;
        }

        byte* word = DictionaryData + offset;
        if (transform == Transforms.Identity)
        {
            if (length > _metaBlockRemaining)
            {
                return BrotliError.BlockLength;
            }

            Buffer.MemoryCopy(word, dst + pos, length, length);
            pos += length;
            _metaBlockRemaining -= length;
            return BrotliError.None;
        }

        byte* transformed = stackalloc byte[Transforms.MaxLength];
        int transformedLength = Transforms.Apply(transformed, word, length, transform);
        if (transformedLength == 0 && distance <= 120)
        {
            return BrotliError.Transform;
        }

        if (transformedLength > _metaBlockRemaining)
        {
            return BrotliError.BlockLength;
        }

        Buffer.MemoryCopy(transformed, dst + pos, transformedLength, transformedLength);
        pos += transformedLength;
        _metaBlockRemaining -= transformedLength;
        return BrotliError.None;
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from <paramref name="distance"/> back, overlapping when the
    /// distance is shorter than the length: 16 bytes at a time where <paramref name="capacity"/> leaves
    /// room past the copy, byte by byte where it does not.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyMatch(byte* dst, int pos, int distance, int length, int capacity)
    {
        byte* d = dst + pos;
        byte* s = d - distance;
        if (capacity - pos - length >= CopySlack)
        {
            if (distance >= 16)
            {
                Copy16(d, s);
                if (length > 16)
                {
                    int done = 16;
                    do
                    {
                        Copy16(d + done, s + done);
                        done += 16;
                    }
                    while (done < length);
                }

                return;
            }

            CopyShortDistance(d, distance, length);
            return;
        }

        for (int i = 0; i < length; i++)
        {
            d[i] = s[i];
        }
    }

    /// <summary>
    /// A copy from fewer than 16 bytes back, with room for 32 bytes past it: the first 16 bytes one by
    /// one, then 16 at a time from a multiple of the distance back, which holds the same pattern.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyShortDistance(byte* d, int distance, int length)
    {
        byte* s = d - distance;
        for (int i = 0; i < 16; i++)
        {
            d[i] = s[i];
        }

        if (length <= 16)
        {
            return;
        }

        int step = 16 - (16 % distance);
        int done = 16;
        do
        {
            Copy16(d + done, d + done - step);
            done += step;
        }
        while (done < length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy16(byte* d, byte* s)
    {
        Unsafe.WriteUnaligned(d, Unsafe.ReadUnaligned<ulong>(s));
        Unsafe.WriteUnaligned(d + 8, Unsafe.ReadUnaligned<ulong>(s + 8));
    }
}
