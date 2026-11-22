using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    // The current command, kept for the careful loop when the fast one stops inside its literals.
    private int _copyLength;
    private int _distanceCode;
    private int _distanceContext;
    private int _distanceTreeIndex;

    private uint* _commandTree;
    private uint* _literalTree;
    private bool _trivialLiteralContext;
    private byte* _contextLookup;
    private byte* _distContextMapSlice;

    /// <summary>The code of each of the 64 literal contexts of the current block type.</summary>
    private nint* _contextTrees;

    private nint[] _contextTreesArray = [];

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

        if (_contextTrees == null)
        {
            _contextTreesArray = Pinned<nint>(1 << Prefix.LiteralContextBits);
            _contextTrees = Pointer(_contextTreesArray);
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

    [SkipLocalsInit]
    private int Commands<TMode>(byte* dst, int dstLength, int resume)
        where TMode : struct, IMode
    {
        bool careful = typeof(TMode) == typeof(CarefulMode);

        // The bit reader.
        ulong bits = _br.Bits;
        int count = _br.Count;
        byte* next = _br.Next;
        byte* end = _br.End;
        byte* guard = end - FastInputSlack;

        // The output, and the meta-block's counters.
        byte* op = dst + _pos;
        byte* opEnd = dst + dstLength;
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
        uint* commandTree = _commandTree;
        byte* distContextMapSlice = _distContextMapSlice;
        nint* distanceTrees = _distanceGroup.Trees;
        int maxBackwardDistance = _maxBackwardDistance;
        int postfixBits = _distancePostfixBits;

        // The literals.
        bool trivial = _trivialLiteralContext;
        uint* literalTree = _literalTree;
        byte* lookup = _contextLookup;
        nint* contextTrees = _contextTrees;

        ref ulong commandCodes = ref MemoryMarshal.GetReference(Prefix.CommandCodes);
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
            result = (int)DecodeBlockSwitch(1);
            bits = _br.Bits;
            count = _br.Count;
            next = _br.Next;
            if (result != 0)
            {
                goto Fail;
            }

            blockLength1 = _s.BlockLength[1];
            commandTree = (uint*)_commandGroup.Trees[_s.BlockTypeRb[3]];
            if (careful && Overrun(next, end, count))
            {
                goto Truncated;
            }

            goto CommandBegin;
        }

        {
            if (count < Huffman.MaxCodeLength)
            {
                Refill<TMode>(ref bits, ref count, ref next, end);
            }

            int command = DecodeSymbol(commandTree, ref bits, ref count, CommandRootBits);
            ulong code = Unsafe.Add(ref commandCodes, command);
            int insertExtraBits = (int)(code & 0xFF);
            int copyExtraBits = (int)((code >> 8) & 0xFF);
            distanceCode = (sbyte)(byte)(code >> 16);
            distanceContext = (int)((code >> 24) & 0xFF);
            distanceTreeIndex = distContextMapSlice[distanceContext];
            insert = (int)((code >> 32) & 0xFFFF);
            copyLength = (int)(code >> 48);
            if (count < insertExtraBits + copyExtraBits)
            {
                Refill<TMode>(ref bits, ref count, ref next, end);
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
                uint* tree = literalTree;
                if (!careful)
                {
                    // Three literals of at most 15 bits each per refill of at least 56.
                    for (; n >= 3; n -= 3)
                    {
                        if (count < 3 * Huffman.MaxCodeLength)
                        {
                            Refill<TMode>(ref bits, ref count, ref next, end);
                        }

                        op[0] = (byte)DecodeSymbol(tree, ref bits, ref count, LiteralRootBits);
                        op[1] = (byte)DecodeSymbol(tree, ref bits, ref count, LiteralRootBits);
                        op[2] = (byte)DecodeSymbol(tree, ref bits, ref count, LiteralRootBits);
                        op += 3;
                    }
                }

                for (; n != 0; n--)
                {
                    if (count < Huffman.MaxCodeLength)
                    {
                        Refill<TMode>(ref bits, ref count, ref next, end);
                    }

                    *op++ = (byte)DecodeSymbol(tree, ref bits, ref count, LiteralRootBits);
                    if (careful && Overrun(next, end, count))
                    {
                        goto Truncated;
                    }
                }
            }
            else
            {
                long written = op - dst;
                uint p1 = written > 0 ? op[-1] : 0u;
                uint p2 = written > 1 ? op[-2] : 0u;
                if (!careful)
                {
                    for (; n >= 3; n -= 3)
                    {
                        if (count < 3 * Huffman.MaxCodeLength)
                        {
                            Refill<TMode>(ref bits, ref count, ref next, end);
                        }

                        p2 = (uint)DecodeSymbol((uint*)contextTrees[lookup[p1] | lookup[256 + p2]], ref bits, ref count, LiteralRootBits);
                        op[0] = (byte)p2;
                        p1 = (uint)DecodeSymbol((uint*)contextTrees[lookup[p2] | lookup[256 + p1]], ref bits, ref count, LiteralRootBits);
                        op[1] = (byte)p1;
                        p2 = (uint)DecodeSymbol((uint*)contextTrees[lookup[p1] | lookup[256 + p2]], ref bits, ref count, LiteralRootBits);
                        op[2] = (byte)p2;
                        op += 3;
                        (p1, p2) = (p2, p1);
                    }
                }

                for (; n != 0; n--)
                {
                    if (count < Huffman.MaxCodeLength)
                    {
                        Refill<TMode>(ref bits, ref count, ref next, end);
                    }

                    uint* tree = (uint*)contextTrees[lookup[p1] | lookup[256 + p2]];
                    p2 = p1;
                    p1 = (uint)DecodeSymbol(tree, ref bits, ref count, LiteralRootBits);
                    *op++ = (byte)p1;
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
                result = (int)DecodeBlockSwitch(2);
                bits = _br.Bits;
                count = _br.Count;
                next = _br.Next;
                if (result != 0)
                {
                    goto Fail;
                }

                blockLength2 = _s.BlockLength[2];
                distContextMapSlice = _distContextMap + (_s.BlockTypeRb[5] << Prefix.DistanceContextBits);
                distanceTreeIndex = distContextMapSlice[distanceContext];
            }

            if (count < Huffman.MaxCodeLength + Prefix.MaxDistanceBits)
            {
                Refill<TMode>(ref bits, ref count, ref next, end);
            }

            int code = DecodeSymbol((uint*)distanceTrees[distanceTreeIndex], ref bits, ref count, DistanceRootBits);
            blockLength2--;
            distanceContext = 0;
            if ((code & ~0xF) == 0)
            {
                distance = TakeDistanceFromRingBuffer(code, ref distanceContext);
            }
            else
            {
                int extraBits = _distExtraBits[code];
                distance = _distOffset[code] + (int)((uint)(bits & ((1UL << extraBits) - 1)) << postfixBits);
                bits >>= extraBits;
                count -= extraBits;
            }

            if (careful && Overrun(next, end, count))
            {
                goto Truncated;
            }
        }

        {
            int pos = (int)(op - dst);
            int maxDistance = pos < maxBackwardDistance ? pos : maxBackwardDistance;
            if (distance > maxDistance)
            {
                result = CopyDictionaryWord(op, distance, copyLength, maxDistance, distanceContext, remaining);
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
                CopyMatch(op, distance, copyLength, opEnd);
                op += copyLength;
            }
        }

        if (remaining <= 0)
        {
            goto MetaBlockDone;
        }

        goto CommandBegin;

    LiteralBlockSwitch:
        _br.Bits = bits;
        _br.Count = count;
        _br.Next = next;
        result = (int)DecodeBlockSwitch(0);
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
        lookup = _contextLookup;
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
        _pos = (int)(op - dst);
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
    private static bool Overrun(byte* next, byte* end, int count) => next > end && (long)(next - end) * 8 > count;

    /// <summary>
    /// Tops the accumulator up to at least 56 bits: a single load in the fast loop, which guarantees
    /// the input; byte by byte near the end, with zeros past it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Refill<TMode>(ref ulong bits, ref int count, ref byte* next, byte* end)
        where TMode : struct, IMode
    {
        if (typeof(TMode) == typeof(FastMode) || end - next >= 8)
        {
            bits |= Unsafe.ReadUnaligned<ulong>(next) << count;
            next += (63 - count) >> 3;
            count |= 56;
        }
        else
        {
            RefillCareful(ref bits, ref count, ref next, end);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RefillCareful(ref ulong bits, ref int count, ref byte* next, byte* end)
    {
        while (count <= 56)
        {
            if (next < end)
            {
                bits |= (ulong)*next << count;
            }

            next++;
            count += 8;
        }
    }

    /// <summary>Decodes a symbol of the code whose table is <paramref name="table"/>; at least 15 bits must be there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeSymbol(uint* table, ref ulong bits, ref int count, int rootBits = Huffman.RootBits)
    {
        uint entry = table[(nint)(bits & ((1UL << rootBits) - 1))];
        if ((entry & 0xFF) > (uint)rootBits)
        {
            int extra = (int)(entry & 0xFF) - rootBits;
            bits >>= rootBits;
            count -= rootBits;
            entry = table[(nint)(entry >> 16) + (nint)(bits & ((1UL << extra) - 1))];
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
        byte* slice = _contextMap + (blockType << Prefix.LiteralContextBits);
        nint* trees = _literalGroup.Trees;
        _trivialLiteralContext = ((_s.TrivialLiteralContexts[blockType >> 5] >> (blockType & 31)) & 1) != 0;
        _literalTree = (uint*)trees[slice[0]];
        _contextLookup = ContextLookup + ((_contextModes[blockType] & 3) << 9);
        if (!_trivialLiteralContext)
        {
            nint* contextTrees = _contextTrees;
            for (int i = 0; i < 1 << Prefix.LiteralContextBits; i++)
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
    private int CopyDictionaryWord(byte* op, int distance, int length, int maxDistance, int distanceContext, int remaining)
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

        byte* word = DictionaryData + offset;
        if (transform == Transforms.Identity)
        {
            if (length > remaining)
            {
                return -(int)BrotliError.BlockLength;
            }

            Buffer.MemoryCopy(word, op, length, length);
            return length;
        }

        byte* transformed = stackalloc byte[Transforms.MaxLength];
        int transformedLength = Transforms.Apply(transformed, word, length, transform);
        if (transformedLength == 0 && distance <= 120)
        {
            return -(int)BrotliError.Transform;
        }

        if (transformedLength > remaining)
        {
            return -(int)BrotliError.BlockLength;
        }

        Buffer.MemoryCopy(transformed, op, transformedLength, transformedLength);
        return transformedLength;
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from <paramref name="distance"/> back, overlapping when the
    /// distance is shorter than the length: 16 bytes at a time where <paramref name="opEnd"/> leaves room
    /// past the copy, byte by byte where it does not.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyMatch(byte* op, int distance, int length, byte* opEnd)
    {
        byte* s = op - distance;
        if (opEnd - op - length >= CopySlack)
        {
            if (distance >= 16)
            {
                Copy16(op, s);
                if (length > 16)
                {
                    Copy16(op + 16, s + 16);
                    if (length > 32)
                    {
                        CopyLong(op, s, length);
                    }
                }

                return;
            }

            CopyShortDistance(op, distance, length);
            return;
        }

        for (int i = 0; i < length; i++)
        {
            op[i] = s[i];
        }
    }

    /// <summary>The bytes of a copy past its first 32, 16 at a time; the distance is at least 16.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyLong(byte* op, byte* s, int length)
    {
        int done = 32;
        do
        {
            Copy16(op + done, s + done);
            done += 16;
        }
        while (done < length);
    }

    /// <summary>
    /// A copy from fewer than 16 bytes back, with room for 32 bytes past it: the first 16 bytes one by
    /// one, then 16 at a time from a multiple of the distance back, which holds the same pattern.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyShortDistance(byte* op, int distance, int length)
    {
        byte* s = op - distance;
        for (int i = 0; i < 16; i++)
        {
            op[i] = s[i];
        }

        if (length <= 16)
        {
            return;
        }

        int step = 16 - (16 % distance);
        int done = 16;
        do
        {
            Copy16Overlapping(op + done, op + done - step);
            done += step;
        }
        while (done < length);
    }

    /// <summary>16 bytes from <paramref name="s"/>, at least 16 bytes back.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy16(byte* d, byte* s) =>
        Unsafe.WriteUnaligned(d, Unsafe.ReadUnaligned<Vector128<byte>>(s));

    /// <summary>16 bytes from <paramref name="s"/>, at least 8 bytes back: two words, each read once the one before is written.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy16Overlapping(byte* d, byte* s)
    {
        Unsafe.WriteUnaligned(d, Unsafe.ReadUnaligned<ulong>(s));
        Unsafe.WriteUnaligned(d + 8, Unsafe.ReadUnaligned<ulong>(s + 8));
    }
}
