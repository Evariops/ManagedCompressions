using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using ManagedCompressions.Brotli.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// A compressed meta-block's commands (RFC 7932, sections 4, 5, 8 and 9.3): each inserts literals,
/// then copies from the window or from the static dictionary, written straight into the destination.
/// </summary>
/// <remarks>
/// <para>
/// One loop, compiled twice. The fast one (<see cref="FastMode"/>) runs while
/// <see cref="FastInputSlack"/> bytes remain in the source at each command, and decodes runs of
/// literals bounded by the input left, which covers every bit a step can read: its refills are single
/// loads, unchecked. Within that distance of the end it hands over to the careful one
/// (<see cref="CarefulMode"/>), which resumes where the fast one stopped, refills byte by byte and
/// checks after each step whether it has read past the end.
/// </para>
/// <para>
/// The loop keeps its state in locals, and writes it back to the fields only around the block
/// switches and when it returns. A literal's code comes from a table of the 64 contexts' codes for the
/// current block type, rebuilt at each block switch: one load less on the path from one literal to the
/// next than brotli's context map, then code.
/// </para>
/// <para>
/// The checks and their order are brotli's (<c>ProcessCommandsInternal</c>). Where brotli writes a
/// command into its ring buffer and only then finds that the meta-block has gone past its length,
/// this checks before writing: the stream is invalid either way.
/// </para>
/// </remarks>
public sealed partial class BrotliDecompressor
{
    /// <summary>The source bytes that must remain past <see cref="BitReader.Next"/> for a fast step.</summary>
    private const int FastInputSlack = 64;

    /// <summary>The bytes past a copy that a wide copy may write, and must therefore have room for.</summary>
    private const int CopySlack = 32;

    /// <summary>The room a dictionary word's wide stores need past the output: <see cref="Transforms.ApplyFast"/>'s.</summary>
    private const int DictionarySlack = 64;

    /// <summary>What <see cref="Commands{TMode, TRoot}"/> returns when the fast loop hands over to the careful one.</summary>
    private const int SwitchToCareful = -1;

    private const int AtCommandBegin = 0;
    private const int AtLiterals = 1;

    /// <summary>The size of a context mode's lookup table in <see cref="Context.Lookup"/>.</summary>
    private const int ContextLookupSize = 512;

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

    /// <summary>The width of the literal codes' root tables the command loop is compiled for.</summary>
    private interface IRoot
    {
        static abstract int Bits { get; }
    }

    /// <summary>Root tables of <see cref="WideLiteralRootBits"/> bits.</summary>
    private struct WideRoot : IRoot
    {
        public static int Bits => WideLiteralRootBits;
    }

    /// <summary>Root tables of brotli's 8 bits.</summary>
    private struct NarrowRoot : IRoot
    {
        public static int Bits => Huffman.RootBits;
    }

    /// <summary>Where the careful loop resumes the fast one: <see cref="AtCommandBegin"/> or <see cref="AtLiterals"/>.</summary>
    private int _resume;

    /// <summary>The literals the current command has yet to insert.</summary>
    private int _insertRemaining;

    // The current command, kept for the careful loop when the fast one stops inside its literals.
    private int _copyLength;
    private int _distanceCode;
    private int _distanceContext;
    private int _distanceTreeIndex;

    /// <summary>The current insert-and-copy code: its offset in the command group's tables.</summary>
    private int _commandTree;

    /// <summary>The literal code of a block type whose contexts all share one: its offset in the literal group's tables.</summary>
    private int _literalTree;

    private bool _trivialLiteralContext;

    /// <summary>The current context mode's lookup table: its offset in <see cref="Context.Lookup"/>.</summary>
    private int _contextLookup;

    /// <summary>The current distance block type's context map: its offset in <see cref="_distContextMap"/>.</summary>
    private int _distContextMapSlice;

    /// <summary>The code of each of the 64 literal contexts of the current block type: offsets in the literal group's tables.</summary>
    private readonly int[] _contextTrees = new int[1 << Prefix.LiteralContextBits];

    private BrotliError DecodeCompressedMetaBlock(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        BrotliError error = DecodeCompressedHeader(src);
        if (error != BrotliError.None)
        {
            return error;
        }

        if (_br.Overrun)
        {
            return BrotliError.Truncated;
        }

        PrepareLiteralDecoding();
        _distContextMapSlice = 0;
        _commandTree = _commandGroup.Trees[0];
        bool wide = _literalGroup.RootBits == WideLiteralRootBits;
        int result = wide ? Commands<FastMode, WideRoot>(src, dst, AtCommandBegin) : Commands<FastMode, NarrowRoot>(src, dst, AtCommandBegin);
        if (result == SwitchToCareful)
        {
            result = wide ? Commands<CarefulMode, WideRoot>(src, dst, _resume) : Commands<CarefulMode, NarrowRoot>(src, dst, _resume);
        }

        return (BrotliError)result;
    }

    private int Commands<TMode, TRoot>(ReadOnlySpan<byte> src, Span<byte> dst, int resume)
        where TMode : struct, IMode
        where TRoot : struct, IRoot
    {
        bool careful = typeof(TMode) == typeof(CarefulMode);

        // The bit reader.
        ulong bits = _br.Bits;
        int count = _br.Count;
        int next = _br.Next;
        int end = _br.End;
        int guard = end - FastInputSlack;

        // The output, and the meta-block's counters.
        int op = _pos;
        int remaining = _metaBlockRemaining;
        int insert = _insertRemaining;
        int blockLength0 = _s.BlockLength[0];
        int blockLength1 = _s.BlockLength[1];
        int blockLength2 = _s.BlockLength[2];

        // The command.
        int copyLength = _copyLength;
        int distanceCode = _distanceCode;
        int distanceContext = _distanceContext;
        int distanceTreeIndex = _distanceTreeIndex;
        ReadOnlySpan<uint> commandTables = _commandGroup.Codes;
        int commandTree = _commandTree;
        ReadOnlySpan<byte> distContextMap = _distContextMap;
        int distContextMapSlice = _distContextMapSlice;
        ReadOnlySpan<uint> distanceTables = _distanceGroup.Codes;
        ReadOnlySpan<int> distanceTrees = _distanceGroup.Trees;
        ReadOnlySpan<byte> distExtraBits = _distExtraBits;
        ReadOnlySpan<int> distOffset = _distOffset;
        int maxBackwardDistance = _maxBackwardDistance;
        int postfixBits = _distancePostfixBits;

        // The literals.
        bool trivial = _trivialLiteralContext;
        ReadOnlySpan<uint> literalTables = _literalGroup.Codes;
        int literalTree = _literalTree;
        ReadOnlySpan<byte> lookup = Context.Lookup.Slice(_contextLookup, ContextLookupSize);
        ReadOnlySpan<int> contextTrees = _contextTrees.AsSpan(0, 1 << Prefix.LiteralContextBits);

        ReadOnlySpan<ulong> commandCodes = Prefix.CommandCodes;
        ReadOnlySpan<byte> sizeBits = StaticDictionary.SizeBitsByLength;
        ReadOnlySpan<int> offsets = StaticDictionary.OffsetsByLength;
        Transforms.Info[] transforms = Transforms.Infos;
        ReadOnlySpan<byte> dictionary = StaticDictionary.Data;
        int result;

        if (resume == AtLiterals)
        {
            goto Literals;
        }

    CommandBegin:
        if (!careful && next > guard)
        {
            _resume = AtCommandBegin;
            goto Suspend;
        }

        if (blockLength1 == 0)
        {
            _br.Bits = bits;
            _br.Count = count;
            _br.Next = next;
            result = (int)DecodeBlockSwitch(src, 1);
            bits = _br.Bits;
            count = _br.Count;
            next = _br.Next;
            if (result != 0)
            {
                goto Fail;
            }

            blockLength1 = _s.BlockLength[1];
            commandTree = _commandGroup.Trees[_s.BlockTypeRb[3]];
            if (careful && Overrun(next, end, count))
            {
                goto Truncated;
            }

            goto CommandBegin;
        }

        {
            if (count < Huffman.MaxCodeLength)
            {
                Refill<TMode>(src, ref bits, ref count, ref next, end);
            }

            int command = DecodeSymbol(commandTables, commandTree, ref bits, ref count, CommandRootBits);
            ulong code = commandCodes[command];
            int insertExtraBits = (int)(code & 0xFF);
            int copyExtraBits = (int)((code >> 8) & 0xFF);
            distanceCode = (sbyte)(byte)(code >> 16);
            distanceContext = (int)((code >> 24) & 0xFF);
            distanceTreeIndex = distContextMap[distContextMapSlice + distanceContext];
            insert = (int)((code >> 32) & 0xFFFF);
            copyLength = (int)(code >> 48);
            if (count < insertExtraBits + copyExtraBits)
            {
                Refill<TMode>(src, ref bits, ref count, ref next, end);
            }

            insert += (int)(bits & ((1UL << insertExtraBits) - 1));
            bits >>= insertExtraBits;
            copyLength += (int)(bits & ((1UL << copyExtraBits) - 1));
            bits >>= copyExtraBits;
            count -= insertExtraBits + copyExtraBits;
            blockLength1--;
            if (careful && Overrun(next, end, count))
            {
                goto Truncated;
            }
        }

        if (insert == 0)
        {
            goto Distance;
        }

        if (insert > remaining)
        {
            // brotli inserts the literals in its ring buffer, then refuses the meta-block for its length.
            result = (int)BrotliError.BlockLength;
            goto Fail;
        }

        remaining -= insert;

    Literals:
        while (true)
        {
            if (blockLength0 == 0)
            {
                goto LiteralBlockSwitch;
            }

            // The literals before the next block switch or the end of the command; in the fast loop,
            // no more than the input left can hold at 15 bits each.
            int n = insert < blockLength0 ? insert : blockLength0;
            if (!careful)
            {
                long margin = guard - next;
                long safe = margin * 8 / Huffman.MaxCodeLength;
                if (safe < n)
                {
                    if (safe <= 0)
                    {
                        _resume = AtLiterals;
                        goto Suspend;
                    }

                    n = (int)safe;
                }
            }

            insert -= n;
            blockLength0 -= n;
            if (trivial)
            {
                if (!careful)
                {
                    // Three literals of at most 15 bits each per refill of at least 56.
                    for (; n >= 3; n -= 3)
                    {
                        if (count < 3 * Huffman.MaxCodeLength)
                        {
                            Refill<TMode>(src, ref bits, ref count, ref next, end);
                        }

                        Span<byte> three = dst.Slice(op, 3);
                        three[0] = (byte)DecodeSymbol(literalTables, literalTree, ref bits, ref count, TRoot.Bits);
                        three[1] = (byte)DecodeSymbol(literalTables, literalTree, ref bits, ref count, TRoot.Bits);
                        three[2] = (byte)DecodeSymbol(literalTables, literalTree, ref bits, ref count, TRoot.Bits);
                        op += 3;
                    }
                }

                for (; n != 0; n--)
                {
                    if (count < Huffman.MaxCodeLength)
                    {
                        Refill<TMode>(src, ref bits, ref count, ref next, end);
                    }

                    dst[op++] = (byte)DecodeSymbol(literalTables, literalTree, ref bits, ref count, TRoot.Bits);
                    if (careful && Overrun(next, end, count))
                    {
                        goto Truncated;
                    }
                }
            }
            else
            {
                byte p1 = op > 0 ? dst[op - 1] : (byte)0;
                byte p2 = op > 1 ? dst[op - 2] : (byte)0;
                if (!careful)
                {
                    for (; n >= 3; n -= 3)
                    {
                        if (count < 3 * Huffman.MaxCodeLength)
                        {
                            Refill<TMode>(src, ref bits, ref count, ref next, end);
                        }

                        Span<byte> three = dst.Slice(op, 3);
                        p2 = (byte)DecodeSymbol(literalTables, contextTrees[(lookup[p1] | lookup[256 + p2]) & 63], ref bits, ref count, TRoot.Bits);
                        three[0] = p2;
                        p1 = (byte)DecodeSymbol(literalTables, contextTrees[(lookup[p2] | lookup[256 + p1]) & 63], ref bits, ref count, TRoot.Bits);
                        three[1] = p1;
                        p2 = (byte)DecodeSymbol(literalTables, contextTrees[(lookup[p1] | lookup[256 + p2]) & 63], ref bits, ref count, TRoot.Bits);
                        three[2] = p2;
                        op += 3;
                        (p1, p2) = (p2, p1);
                    }
                }

                for (; n != 0; n--)
                {
                    if (count < Huffman.MaxCodeLength)
                    {
                        Refill<TMode>(src, ref bits, ref count, ref next, end);
                    }

                    int tree = contextTrees[(lookup[p1] | lookup[256 + p2]) & 63];
                    p2 = p1;
                    p1 = (byte)DecodeSymbol(literalTables, tree, ref bits, ref count, TRoot.Bits);
                    dst[op++] = p1;
                    if (careful && Overrun(next, end, count))
                    {
                        goto Truncated;
                    }
                }
            }

            if (insert == 0)
            {
                break;
            }
        }

        if (remaining <= 0)
        {
            goto MetaBlockDone;
        }

    Distance:
        int distance;
        if (distanceCode >= 0)
        {
            // The implicit distance of codes 0 to 127: the last one, which is not pushed again.
            distanceContext = 1;
            --_distRbIndex;
            distance = _s.DistRb[_distRbIndex & 3];
        }
        else
        {
            if (blockLength2 == 0)
            {
                _br.Bits = bits;
                _br.Count = count;
                _br.Next = next;
                result = (int)DecodeBlockSwitch(src, 2);
                bits = _br.Bits;
                count = _br.Count;
                next = _br.Next;
                if (result != 0)
                {
                    goto Fail;
                }

                blockLength2 = _s.BlockLength[2];
                distContextMapSlice = _s.BlockTypeRb[5] << Prefix.DistanceContextBits;
                distanceTreeIndex = distContextMap[distContextMapSlice + distanceContext];
            }

            if (count < Huffman.MaxCodeLength + Prefix.MaxDistanceBits)
            {
                Refill<TMode>(src, ref bits, ref count, ref next, end);
            }

            int code = DecodeSymbol(distanceTables, distanceTrees[distanceTreeIndex], ref bits, ref count, DistanceRootBits);
            blockLength2--;
            distanceContext = 0;
            if ((code & ~0xF) == 0)
            {
                distance = TakeDistanceFromRingBuffer(code, ref distanceContext);
            }
            else
            {
                int extraBits = distExtraBits[code];
                distance = distOffset[code] + (int)((uint)(bits & ((1UL << extraBits) - 1)) << postfixBits);
                bits >>= extraBits;
                count -= extraBits;
            }

            if (careful && Overrun(next, end, count))
            {
                goto Truncated;
            }
        }

        {
            int maxDistance = op < maxBackwardDistance ? op : maxBackwardDistance;
            if (distance > maxDistance)
            {
                if (dst.Length - op >= DictionarySlack && distance <= Prefix.MaxAllowedDistance && (uint)(copyLength - 4) <= 20)
                {
                    // A word of the static dictionary, with room for wide stores: brotli's checks, inline.
                    int shift = sizeBits[copyLength];
                    int address = distance - maxDistance - 1;
                    int transform = address >> shift;
                    _distRbIndex += distanceContext;
                    if (transform >= Transforms.Count)
                    {
                        result = (int)BrotliError.Transform;
                        goto Fail;
                    }

                    int word = offsets[copyLength] + ((address & ((1 << shift) - 1)) * copyLength);
                    int length = Transforms.ApplyFast(dst.Slice(op, DictionarySlack), dictionary.Slice(word), copyLength, in transforms[transform]);
                    if (length == 0 && distance <= 120)
                    {
                        result = (int)BrotliError.Transform;
                        goto Fail;
                    }

                    if (length > remaining)
                    {
                        result = (int)BrotliError.BlockLength;
                        goto Fail;
                    }

                    op += length;
                    remaining -= length;
                    goto Copied;
                }

                result = CopyDictionaryWord(dst, op, distance, copyLength, maxDistance, distanceContext, remaining);
                if (result < 0)
                {
                    result = -result;
                    goto Fail;
                }

                op += result;
                remaining -= result;
            }
            else
            {
                if (copyLength > remaining)
                {
                    // brotli copies, then refuses the meta-block for its length.
                    result = (int)BrotliError.BlockLength;
                    goto Fail;
                }

                _s.DistRb[_distRbIndex & 3] = distance;
                ++_distRbIndex;
                remaining -= copyLength;
                CopyMatch(dst, op, distance, copyLength);
                op += copyLength;
            }
        }

    Copied:
        if (remaining <= 0)
        {
            goto MetaBlockDone;
        }

        goto CommandBegin;

    LiteralBlockSwitch:
        _br.Bits = bits;
        _br.Count = count;
        _br.Next = next;
        result = (int)DecodeBlockSwitch(src, 0);
        bits = _br.Bits;
        count = _br.Count;
        next = _br.Next;
        if (result != 0)
        {
            goto Fail;
        }

        blockLength0 = _s.BlockLength[0];
        PrepareLiteralDecoding();
        trivial = _trivialLiteralContext;
        literalTree = _literalTree;
        lookup = Context.Lookup.Slice(_contextLookup, ContextLookupSize);
        if (careful && Overrun(next, end, count))
        {
            goto Truncated;
        }

        goto Literals;

    MetaBlockDone:
        result = 0;
        goto Exit;

    Suspend:
        result = SwitchToCareful;
        goto Exit;

    Truncated:
        result = (int)BrotliError.Truncated;
        goto Exit;

    Fail:
        if (Overrun(next, end, count))
        {
            result = (int)BrotliError.Truncated;
        }

    Exit:
        _br.Bits = bits;
        _br.Count = count;
        _br.Next = next;
        _pos = op;
        _metaBlockRemaining = remaining;
        _insertRemaining = insert;
        _s.BlockLength[0] = blockLength0;
        _s.BlockLength[1] = blockLength1;
        _s.BlockLength[2] = blockLength2;
        _copyLength = copyLength;
        _distanceCode = distanceCode;
        _distanceContext = distanceContext;
        _distanceTreeIndex = distanceTreeIndex;
        _commandTree = commandTree;
        _distContextMapSlice = distContextMapSlice;
        return result;
    }

    /// <summary>Whether the reader has consumed some of the zeros past the end of the input.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Overrun(int next, int end, int count) => next > end && (long)(next - end) * 8 > count;

    /// <summary>
    /// Tops the accumulator up to at least 56 bits: a single load in the fast loop, which guarantees
    /// the input; byte by byte near the end, with zeros past it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Refill<TMode>(ReadOnlySpan<byte> src, ref ulong bits, ref int count, ref int next, int end)
        where TMode : struct, IMode
    {
        if (typeof(TMode) == typeof(FastMode) || end - next >= 8)
        {
            bits |= BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(next, 8)) << count;
            next += (63 - count) >> 3;
            count |= 56;
        }
        else
        {
            RefillCareful(src, ref bits, ref count, ref next, end);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RefillCareful(ReadOnlySpan<byte> src, ref ulong bits, ref int count, ref int next, int end)
    {
        while (count <= 56)
        {
            if (next < end)
            {
                bits |= (ulong)src[next] << count;
            }

            next++;
            count += 8;
        }
    }

    /// <summary>
    /// Decodes a symbol of the code whose table starts at <paramref name="tree"/> in
    /// <paramref name="tables"/>; at least 15 bits must be there.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeSymbol(ReadOnlySpan<uint> tables, int tree, ref ulong bits, ref int count, int rootBits = Huffman.RootBits)
    {
        uint entry = tables[tree + (int)(bits & ((1UL << rootBits) - 1))];
        if ((entry & 0xFF) > (uint)rootBits)
        {
            int extra = (int)(entry & 0xFF) - rootBits;
            bits >>= rootBits;
            count -= rootBits;
            entry = tables[tree + (int)(entry >> 16) + (int)(bits & ((1UL << extra) - 1))];
        }

        int length = (int)(entry & 0xFF);
        bits >>= length;
        count -= length;
        return (int)(entry >> 16);
    }

    /// <summary>The literal decoding state of the current literal block type.</summary>
    private void PrepareLiteralDecoding()
    {
        int blockType = _s.BlockTypeRb[1];
        ReadOnlySpan<byte> slice = _contextMap.AsSpan(blockType << Prefix.LiteralContextBits, 1 << Prefix.LiteralContextBits);
        ReadOnlySpan<int> trees = _literalGroup.Trees;
        _trivialLiteralContext = ((_s.TrivialLiteralContexts[blockType >> 5] >> (blockType & 31)) & 1) != 0;
        _literalTree = trees[slice[0]];
        _contextLookup = (_contextModes[blockType] & 3) << 9;
        if (!_trivialLiteralContext)
        {
            Span<int> contextTrees = _contextTrees;
            for (int i = 0; i < slice.Length; i++)
            {
                contextTrees[i] = trees[slice[i]];
            }
        }
    }

    /// <summary>
    /// A block switch command (RFC 7932, section 6) of category <paramref name="treeType"/>: the next
    /// block type, then its length.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private BrotliError DecodeBlockSwitch(ReadOnlySpan<byte> src, int treeType)
    {
        int maxBlockType = _s.NumBlockTypes[treeType];
        if (maxBlockType <= 1)
        {
            return BrotliError.BlockSwitch;
        }

        int blockType = _br.ReadSymbol(src, _headerTables, BlockTypeTrees + (treeType * BlockTypeTableSize));
        _s.BlockLength[treeType] = ReadBlockLength(src, BlockLengthTrees + (treeType * BlockLengthTableSize));
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

    /// <summary>
    /// The distance of codes 0 to 15: one of the last four, possibly adjusted (RFC 7932, section 4).
    /// Code 0 repeats the last distance, which is not pushed again: compensated in advance, with
    /// <paramref name="distanceContext"/> 1.
    /// </summary>
    private int TakeDistanceFromRingBuffer(int code, ref int distanceContext)
    {
        if (code <= 3)
        {
            distanceContext = 1 >> code;
            int distance = _s.DistRb[(_distRbIndex - (code - 3)) & 3];
            _distRbIndex -= distanceContext;
            return distance;
        }

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
        int adjusted = _s.DistRb[(_distRbIndex + indexDelta) & 3] + delta;

        // A distance of 0 or less is refused below, as larger than any distance.
        return adjusted <= 0 ? 0x7FFFFFFF : adjusted;
    }

    /// <summary>
    /// A reference past the window: a word of the static dictionary, possibly transformed
    /// (RFC 7932, section 8), with brotli's checks in brotli's order. Returns the bytes written at
    /// <paramref name="op"/>, or the error, negated.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int CopyDictionaryWord(Span<byte> dst, int op, int distance, int length, int maxDistance, int distanceContext, int remaining)
    {
        if (distance > Prefix.MaxAllowedDistance)
        {
            return -(int)BrotliError.Distance;
        }

        if (length < 4 || length > 24)
        {
            return -(int)BrotliError.Dictionary;
        }

        int shift = StaticDictionary.SizeBitsByLength[length];
        int offset = StaticDictionary.OffsetsByLength[length];
        int address = distance - maxDistance - 1;
        int wordIndex = address & ((1 << shift) - 1);
        int transform = address >> shift;

        // A dictionary reference does not push its distance: undo code 0's compensation.
        _distRbIndex += distanceContext;
        offset += wordIndex * length;
        if (transform >= Transforms.Count)
        {
            return -(int)BrotliError.Transform;
        }

        ReadOnlySpan<byte> word = StaticDictionary.Data.Slice(offset, length);
        if (transform == Transforms.Identity)
        {
            if (length > remaining)
            {
                return -(int)BrotliError.BlockLength;
            }

            word.CopyTo(dst.Slice(op));
            return length;
        }

        Span<byte> transformed = stackalloc byte[Transforms.MaxLength];
        int transformedLength = Transforms.Apply(transformed, word, length, transform);
        if (transformedLength == 0 && distance <= 120)
        {
            return -(int)BrotliError.Transform;
        }

        if (transformedLength > remaining)
        {
            return -(int)BrotliError.BlockLength;
        }

        transformed.Slice(0, transformedLength).CopyTo(dst.Slice(op));
        return transformedLength;
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from <paramref name="distance"/> back to
    /// <paramref name="op"/>, overlapping when the distance is shorter than the length: 16 bytes at a
    /// time where the destination leaves room past the copy, byte by byte where it does not.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyMatch(Span<byte> dst, int op, int distance, int length)
    {
        int s = op - distance;
        if (dst.Length - op - length >= CopySlack)
        {
            if (distance >= 16)
            {
                Copy16(dst, op, s);
                if (length > 16)
                {
                    Copy16(dst, op + 16, s + 16);
                    if (length > 32)
                    {
                        CopyLong(dst, op, s, length);
                    }
                }

                return;
            }

            CopyShortDistance(dst, op, distance, length);
            return;
        }

        for (int i = 0; i < length; i++)
        {
            dst[op + i] = dst[s + i];
        }
    }

    /// <summary>The bytes of a copy past its first 32, 16 at a time; the distance is at least 16.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyLong(Span<byte> dst, int op, int s, int length)
    {
        int done = 32;
        do
        {
            Copy16(dst, op + done, s + done);
            done += 16;
        }
        while (done < length);
    }

    /// <summary>
    /// A copy from fewer than 16 bytes back, with room for 32 bytes past it: the first 16 bytes one by
    /// one, then 16 at a time from a multiple of the distance back, which holds the same pattern.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyShortDistance(Span<byte> dst, int op, int distance, int length)
    {
        int s = op - distance;
        for (int i = 0; i < 16; i++)
        {
            dst[op + i] = dst[s + i];
        }

        if (length <= 16)
        {
            return;
        }

        int step = 16 - (16 % distance);
        int done = 16;
        do
        {
            Copy16Overlapping(dst, op + done, op + done - step);
            done += step;
        }
        while (done < length);
    }

    /// <summary>16 bytes from <paramref name="s"/> to <paramref name="d"/>, at least 16 bytes on.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy16(Span<byte> dst, int d, int s) =>
        Vector128.Create((ReadOnlySpan<byte>)dst.Slice(s, 16)).CopyTo(dst.Slice(d, 16));

    /// <summary>16 bytes from <paramref name="s"/> to <paramref name="d"/>, at least 8 bytes on: two words, each read once the one before is written.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy16Overlapping(Span<byte> dst, int d, int s)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(d, 8), BinaryPrimitives.ReadUInt64LittleEndian(dst.Slice(s, 8)));
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(d + 8, 8), BinaryPrimitives.ReadUInt64LittleEndian(dst.Slice(s + 8, 8)));
    }
}
