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

    /// <summary>
    /// The groups' tables, as fields of their own: a command, a distance or a run of literals reads its table
    /// in one load, not through its group.
    /// </summary>
    private uint[] _commandCodes = [];
    private uint[] _distanceCodes = [];
    private uint[] _literalCodes = [];

    /// <summary>The current insert-and-copy code: its offset in the command group's tables.</summary>
    private int _commandTree;

    /// <summary>The current command's distance code, found with the command: its offset in the distance group's tables.</summary>
    private int _distanceTree;

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
        _commandCodes = _commandGroup.Codes;
        _distanceCodes = _distanceGroup.Codes;
        _literalCodes = _literalGroup.Codes;
        bool wide = _literalGroup.RootBits == WideLiteralRootBits;
        int result = wide ? Commands<FastMode, WideRoot>(src, dst, AtCommandBegin) : Commands<FastMode, NarrowRoot>(src, dst, AtCommandBegin);
        if (result == SwitchToCareful)
        {
            result = wide ? Commands<CarefulMode, WideRoot>(src, dst, _resume) : Commands<CarefulMode, NarrowRoot>(src, dst, _resume);
        }

        return (BrotliError)result;
    }

    /// <summary>
    /// The commands of the meta-block from <paramref name="resume"/>. Only the bit reader and the output
    /// position live in locals: the rest of the state stays in the decoder's fields, read where a command,
    /// a run of literals or a distance needs it, so that the literal loops keep their values in registers.
    /// </summary>
    private int Commands<TMode, TRoot>(ReadOnlySpan<byte> src, Span<byte> dst, int resume)
        where TMode : struct, IMode
        where TRoot : struct, IRoot
    {
        bool careful = typeof(TMode) == typeof(CarefulMode);
        ulong bits = _br.Bits;
        int count = _br.Count;
        int next = _br.Next;
        int op = _pos;
        int result;

        if (resume == AtLiterals)
        {
            goto Literals;
        }

    CommandBegin:
        if (!careful && next > src.Length - FastInputSlack)
        {
            _resume = AtCommandBegin;
            goto Suspend;
        }

        if (_s.BlockLength[1] == 0)
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

            _commandTree = _commandGroup.Trees[_s.BlockTypeRb[3]];
            if (careful && Overrun(next, src.Length, count))
            {
                goto Truncated;
            }

            goto CommandBegin;
        }

        {
            if (count < Huffman.MaxCodeLength)
            {
                Refill<TMode>(src, ref bits, ref count, ref next);
            }

            int command = DecodeSymbol(_commandCodes.AsSpan(_commandTree), ref bits, ref count, CommandRootBits);
            ulong code = Prefix.CommandCodes[command];
            int insertExtraBits = (int)(code & 0xFF);
            int copyExtraBits = (int)((code >> 8) & 0xFF);
            if (count < insertExtraBits + copyExtraBits)
            {
                Refill<TMode>(src, ref bits, ref count, ref next);
            }

            int insert = (int)((code >> 32) & 0xFFFF) + (int)(bits & ((1UL << insertExtraBits) - 1));
            bits >>= insertExtraBits;
            _copyLength = (int)(code >> 48) + (int)(bits & ((1UL << copyExtraBits) - 1));
            bits >>= copyExtraBits;
            count -= insertExtraBits + copyExtraBits;
            _distanceCode = (sbyte)(byte)(code >> 16);
            int commandDistanceContext = (int)((code >> 24) & 0xFF);
            _distanceContext = commandDistanceContext;

            // The distance code's tree now, off the path from the distance to the copy, as brotli does.
            _distanceTree = _distanceGroup.Trees[_distContextMap[_distContextMapSlice + commandDistanceContext]];
            _s.BlockLength[1]--;
            if (careful && Overrun(next, src.Length, count))
            {
                goto Truncated;
            }

            if (insert == 0)
            {
                goto Distance;
            }

            if (insert > _metaBlockRemaining)
            {
                // brotli inserts the literals in its ring buffer, then refuses the meta-block for its length.
                _insertRemaining = insert;
                result = (int)BrotliError.BlockLength;
                goto Fail;
            }

            _metaBlockRemaining -= insert;
            _insertRemaining = insert;
        }

    Literals:
        while (true)
        {
            int blockLength0 = _s.BlockLength[0];
            if (blockLength0 == 0)
            {
                goto LiteralBlockSwitch;
            }

            // The literals before the next block switch or the end of the command; in the fast loop,
            // no more than the input left can hold at 15 bits each.
            int insert = _insertRemaining;
            int n = insert < blockLength0 ? insert : blockLength0;
            if (!careful)
            {
                long margin = src.Length - FastInputSlack - (long)next;
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

            _insertRemaining = insert - n;
            _s.BlockLength[0] = blockLength0 - n;
            uint[] literalTables = _literalCodes;

            // The run's output, which shrinks as the literals are written: no position nor count to keep, and
            // its bounds, once checked, hold for every write.
            Span<byte> run = dst.Slice(op, n);
            if (_trivialLiteralContext)
            {
                int tree = _literalTree;
                if (!careful)
                {
                    // The run in a method of its own, whose few values the JIT keeps in registers.
                    Cursor cursor = TrivialLiterals<TRoot>(new Cursor(bits, count, next), src, run, tree);
                    bits = cursor.Bits;
                    count = cursor.Count;
                    next = cursor.Next;
                    run = run.Slice(run.Length);
                }

                for (int i = 0; i < run.Length; i++)
                {
                    if (count < Huffman.MaxCodeLength)
                    {
                        Refill<TMode>(src, ref bits, ref count, ref next);
                    }

                    run[i] = (byte)DecodeSymbol(literalTables.AsSpan(tree), ref bits, ref count, TRoot.Bits);
                    if (careful && Overrun(next, src.Length, count))
                    {
                        goto Truncated;
                    }
                }
            }
            else
            {
                // The context lookup of the block type's mode: an offset into the constant table of the four modes,
                // read straight from its address each time rather than kept in a register.
                int lut = _contextLookup;
                int[] contextTrees = _contextTrees;
                byte p1 = op > 0 ? dst[op - 1] : (byte)0;
                byte p2 = op > 1 ? dst[op - 2] : (byte)0;
                if (!careful)
                {
                    // The run in a method of its own, whose few values the JIT keeps in registers.
                    Cursor cursor = ContextLiterals<TRoot>(new Cursor(bits, count, next), src, run, p1, p2);
                    bits = cursor.Bits;
                    count = cursor.Count;
                    next = cursor.Next;
                    run = run.Slice(run.Length);
                }

                for (int i = 0; i < run.Length; i++)
                {
                    if (count < Huffman.MaxCodeLength)
                    {
                        Refill<TMode>(src, ref bits, ref count, ref next);
                    }

                    int tree = contextTrees[(Context.Lookup[(lut + p1) & 0x7FF] | Context.Lookup[(lut + 256 + p2) & 0x7FF]) & 63];
                    p2 = p1;
                    p1 = (byte)DecodeSymbol(literalTables, tree, ref bits, ref count, TRoot.Bits);
                    run[i] = p1;
                    if (careful && Overrun(next, src.Length, count))
                    {
                        goto Truncated;
                    }
                }
            }

            op += n;

            if (_insertRemaining == 0)
            {
                break;
            }
        }

        if (_metaBlockRemaining <= 0)
        {
            goto MetaBlockDone;
        }

    Distance:
        int distance;
        int distanceContext = _distanceContext;
        if (_distanceCode >= 0)
        {
            // The implicit distance of codes 0 to 127: the last one, which is not pushed again.
            distanceContext = 1;
            --_distRbIndex;
            distance = _s.DistRb[_distRbIndex & 3];
        }
        else
        {
            if (_s.BlockLength[2] == 0)
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

                _distContextMapSlice = _s.BlockTypeRb[5] << Prefix.DistanceContextBits;
                _distanceTree = _distanceGroup.Trees[_distContextMap[_distContextMapSlice + distanceContext]];
            }

            if (count < Huffman.MaxCodeLength + Prefix.MaxDistanceBits)
            {
                Refill<TMode>(src, ref bits, ref count, ref next);
            }

            int code = DecodeSymbol(_distanceCodes.AsSpan(_distanceTree), ref bits, ref count, DistanceRootBits);
            _s.BlockLength[2]--;
            distanceContext = 0;
            if ((code & ~0xF) == 0)
            {
                distance = TakeDistanceFromRingBuffer(code, ref distanceContext);
            }
            else
            {
                int extraBits = _distExtraBits[code];
                distance = _distOffset[code] + (int)((uint)(bits & ((1UL << extraBits) - 1)) << _distancePostfixBits);
                bits >>= extraBits;
                count -= extraBits;
            }

            if (careful && Overrun(next, src.Length, count))
            {
                _distanceContext = distanceContext;
                goto Truncated;
            }
        }

        _distanceContext = distanceContext;
        {
            int copyLength = _copyLength;
            int remaining = _metaBlockRemaining;
            int maxBackwardDistance = _maxBackwardDistance;
            int maxDistance = op < maxBackwardDistance ? op : maxBackwardDistance;
            if (distance > maxDistance)
            {
                if (dst.Length - op >= DictionarySlack && distance <= Prefix.MaxAllowedDistance && (uint)(copyLength - 4) <= 20)
                {
                    // A word of the static dictionary, with room for wide stores: brotli's checks, inline.
                    int shift = StaticDictionary.SizeBitsByLength[copyLength];
                    int address = distance - maxDistance - 1;
                    int transform = address >> shift;
                    _distRbIndex += distanceContext;
                    if (transform >= Transforms.Count)
                    {
                        result = (int)BrotliError.Transform;
                        goto Fail;
                    }

                    int word = StaticDictionary.OffsetsByLength[copyLength] + ((address & ((1 << shift) - 1)) * copyLength);
                    int length = Transforms.ApplyFast(dst.Slice(op, DictionarySlack), StaticDictionary.Data.Slice(word), copyLength, in Transforms.Infos[transform]);
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
                    _metaBlockRemaining = remaining - length;
                    goto Copied;
                }

                result = CopyDictionaryWord(dst, op, distance, copyLength, maxDistance, distanceContext, remaining);
                if (result < 0)
                {
                    result = -result;
                    goto Fail;
                }

                op += result;
                _metaBlockRemaining = remaining - result;
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
                _metaBlockRemaining = remaining - copyLength;
                CopyMatch(dst, op, distance, copyLength);
                op += copyLength;
            }
        }

    Copied:
        if (_metaBlockRemaining <= 0)
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

        PrepareLiteralDecoding();
        if (careful && Overrun(next, src.Length, count))
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
        if (Overrun(next, src.Length, count))
        {
            result = (int)BrotliError.Truncated;
        }

    Exit:
        _br.Bits = bits;
        _br.Count = count;
        _br.Next = next;
        _pos = op;
        return result;
    }

    /// <summary>The bit reader's state, into and out of the methods of the literal loops.</summary>
    private readonly struct Cursor
    {
        public readonly ulong Bits;
        public readonly int Count;
        public readonly int Next;

        public Cursor(ulong bits, int count, int next)
        {
            Bits = bits;
            Count = count;
            Next = next;
        }
    }

    /// <summary>
    /// The literals of a block type whose contexts share one code, three per refill, into <paramref name="run"/>;
    /// the fast loop's input slack covers every read.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Cursor TrivialLiterals<TRoot>(Cursor cursor, ReadOnlySpan<byte> src, Span<byte> run, int tree)
        where TRoot : struct, IRoot
    {
        ulong bits = cursor.Bits;
        int count = cursor.Count;
        int next = cursor.Next;
        ReadOnlySpan<uint> table = _literalCodes.AsSpan(tree);
        while (run.Length >= 3)
        {
            if (count < 3 * Huffman.MaxCodeLength)
            {
                Refill<FastMode>(src, ref bits, ref count, ref next);
            }

            run[0] = (byte)DecodeSymbol(table, ref bits, ref count, TRoot.Bits);
            run[1] = (byte)DecodeSymbol(table, ref bits, ref count, TRoot.Bits);
            run[2] = (byte)DecodeSymbol(table, ref bits, ref count, TRoot.Bits);
            run = run.Slice(3);
        }

        // The last one or two, of 30 bits at most.
        if (!run.IsEmpty)
        {
            if (count < 2 * Huffman.MaxCodeLength)
            {
                Refill<FastMode>(src, ref bits, ref count, ref next);
            }

            run[0] = (byte)DecodeSymbol(table, ref bits, ref count, TRoot.Bits);
            if (run.Length > 1)
            {
                run[1] = (byte)DecodeSymbol(table, ref bits, ref count, TRoot.Bits);
            }
        }

        return new Cursor(bits, count, next);
    }

    /// <summary>
    /// The literals of a block type with context modeling, three per refill, into <paramref name="run"/>, after the bytes <paramref name="p1"/> and <paramref name="p2"/>; the fast
    /// loop's input slack covers every read. The context lookup is read from its constant table, the codes
    /// of the contexts and the tables from the decoder's fields, once.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Cursor ContextLiterals<TRoot>(Cursor cursor, ReadOnlySpan<byte> src, Span<byte> run, byte p1, byte p2)
        where TRoot : struct, IRoot
    {
        ulong bits = cursor.Bits;
        int count = cursor.Count;
        int next = cursor.Next;
        uint[] tables = _literalCodes;
        ReadOnlySpan<int> contextTrees = _contextTrees.AsSpan(0, 1 << Prefix.LiteralContextBits);

        // The mode's lookup at a constant length: a literal indexes it with no addition nor mask, and a
        // context, always below 64, with a bounds check off the path from one literal to the next.
        ReadOnlySpan<byte> lookup = Context.Lookup.Slice(_contextLookup, 512);
        while (run.Length >= 3)
        {
            if (count < 3 * Huffman.MaxCodeLength)
            {
                Refill<FastMode>(src, ref bits, ref count, ref next);
            }

            p2 = (byte)DecodeSymbol(tables, contextTrees[lookup[p1] | lookup[256 + p2]], ref bits, ref count, TRoot.Bits);
            run[0] = p2;
            p1 = (byte)DecodeSymbol(tables, contextTrees[lookup[p2] | lookup[256 + p1]], ref bits, ref count, TRoot.Bits);
            run[1] = p1;
            p2 = (byte)DecodeSymbol(tables, contextTrees[lookup[p1] | lookup[256 + p2]], ref bits, ref count, TRoot.Bits);
            run[2] = p2;
            run = run.Slice(3);
            (p1, p2) = (p2, p1);
        }

        // The last one or two, of 30 bits at most.
        if (!run.IsEmpty)
        {
            if (count < 2 * Huffman.MaxCodeLength)
            {
                Refill<FastMode>(src, ref bits, ref count, ref next);
            }

            p2 = (byte)DecodeSymbol(tables, contextTrees[lookup[p1] | lookup[256 + p2]], ref bits, ref count, TRoot.Bits);
            run[0] = p2;
            if (run.Length > 1)
            {
                run[1] = (byte)DecodeSymbol(tables, contextTrees[lookup[p2] | lookup[256 + p1]], ref bits, ref count, TRoot.Bits);
            }
        }

        return new Cursor(bits, count, next);
    }

    /// <summary>Whether the reader has consumed some of the zeros past the end of the input.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Overrun(int next, int end, int count) => next > end && (long)(next - end) * 8 > count;

    /// <summary>
    /// Tops the accumulator up to at least 56 bits: a single load in the fast loop, which guarantees
    /// the input; byte by byte near the end, with zeros past it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Refill<TMode>(ReadOnlySpan<byte> src, ref ulong bits, ref int count, ref int next)
        where TMode : struct, IMode
    {
        if (typeof(TMode) == typeof(FastMode) || src.Length - next >= 8)
        {
            bits |= BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(next, 8)) << count;
            next += (63 - count) >> 3;
            count |= 56;
        }
        else
        {
            RefillCareful(src, ref bits, ref count, ref next);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RefillCareful(ReadOnlySpan<byte> src, ref ulong bits, ref int count, ref int next)
    {
        while (count <= 56)
        {
            if (next < src.Length)
            {
                bits |= (ulong)src[next] << count;
            }

            next++;
            count += 8;
        }
    }

    /// <summary>
    /// Decodes a symbol of the code whose table starts <paramref name="table"/>, sliced at its tree once, so that
    /// the lookup adds nothing to the bits; at least 15 bits must be there.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeSymbol(ReadOnlySpan<uint> table, ref ulong bits, ref int count, int rootBits)
    {
        uint entry = table[(int)(bits & ((1UL << rootBits) - 1))];
        if ((entry & 0xFF) > (uint)rootBits)
        {
            int extra = (int)(entry & 0xFF) - rootBits;
            bits >>= rootBits;
            count -= rootBits;
            entry = table[(int)(entry >> 16) + (int)(bits & ((1UL << extra) - 1))];
        }

        int length = (int)(entry & 0xFF);
        bits >>= length;
        count -= length;
        return (int)(entry >> 16);
    }

    /// <summary>
    /// Decodes a symbol of the code whose table starts at <paramref name="tree"/> in
    /// <paramref name="tables"/>; at least 15 bits must be there.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeSymbol(uint[] tables, int tree, ref ulong bits, ref int count, int rootBits = Huffman.RootBits)
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
