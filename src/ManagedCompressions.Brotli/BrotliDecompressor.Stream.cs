using System;
using System.Runtime.CompilerServices;
using ManagedCompressions.Brotli.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// The stream and its meta-blocks (RFC 7932, sections 9.1 and 9.2), decoded straight into the
/// destination: the whole content is there, so the window is the content written so far, and nothing
/// goes through a ring buffer.
/// </summary>
public sealed partial class BrotliDecompressor
{
    /// <summary>The bit reader, between the methods that are not inlined into the command loop.</summary>
    private BitReader _br;

    /// <summary>The content written so far: the position in the destination.</summary>
    private int _pos;

    /// <summary>The bytes the current meta-block has yet to produce.</summary>
    private int _metaBlockRemaining;

    /// <summary>The stream's window, as a number of bits: 10 to 24.</summary>
    private int _windowBits;

    /// <summary>The largest distance of the stream: its window, less the 16 bytes of the gap.</summary>
    private int _maxBackwardDistance;

    /// <summary>The four last distances, and the index of the next one to write.</summary>
    private Fixed _s;

    /// <summary>The small arrays of the state, inline.</summary>
    private struct Fixed
    {
        public Array4<int> DistRb;
        public Array3<int> NumBlockTypes;
        public Array3<int> BlockLength;
        public Array6<int> BlockTypeRb;
        public Array8<uint> TrivialLiteralContexts;
    }

    [InlineArray(3)]
    private struct Array3<T>
    {
        private T _element;
    }

    [InlineArray(4)]
    private struct Array4<T>
    {
        private T _element;
    }

    [InlineArray(6)]
    private struct Array6<T>
    {
        private T _element;
    }

    [InlineArray(8)]
    private struct Array8<T>
    {
        private T _element;
    }

    private int _distRbIndex;

    private BrotliError DecodeStream(ReadOnlySpan<byte> src, Span<byte> dst, out int consumed, out int written)
    {
        consumed = 0;
        written = 0;
        _br = new BitReader(0, src.Length);
        _pos = 0;
        _s.DistRb[0] = 16;
        _s.DistRb[1] = 15;
        _s.DistRb[2] = 11;
        _s.DistRb[3] = 4;
        _distRbIndex = 0;

        BrotliError error = DecodeWindowBits(src);
        if (error != BrotliError.None)
        {
            return error;
        }

        _maxBackwardDistance = (1 << _windowBits) - Prefix.WindowGap;
        bool last;
        do
        {
            error = DecodeMetaBlockHeader(src, out last, out bool uncompressed, out bool metadata);
            if (error != BrotliError.None)
            {
                return error;
            }

            if (metadata || uncompressed)
            {
                if (_br.JumpToByteBoundary() != 0)
                {
                    return Fail(BrotliError.Padding);
                }
            }

            if (metadata)
            {
                error = SkipBytes(_metaBlockRemaining);
                if (error != BrotliError.None)
                {
                    return error;
                }

                continue;
            }

            if (_metaBlockRemaining == 0)
            {
                continue;
            }

            if (_metaBlockRemaining > dst.Length - _pos)
            {
                return _br.Overrun ? BrotliError.Truncated : BrotliError.DestinationTooSmall;
            }

            error = uncompressed ? CopyUncompressed(src, dst) : DecodeCompressedMetaBlock(src, dst);
            if (error != BrotliError.None)
            {
                return error;
            }
        }
        while (!last);

        if (_br.JumpToByteBoundary() != 0)
        {
            return Fail(BrotliError.Padding);
        }

        if (_br.Overrun)
        {
            return BrotliError.Truncated;
        }

        _br.Unload();
        consumed = (int)(_br.Next - _br.Start);
        written = _pos;
        return BrotliError.None;
    }

    /// <summary>
    /// <paramref name="error"/>, unless the bits that led to it were past the end of the source: brotli
    /// would have stopped before, for want of input.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private BrotliError Fail(BrotliError error) => _br.Overrun ? BrotliError.Truncated : error;

    /// <summary>WBITS (RFC 7932, section 9.1): 1 to 7 bits.</summary>
    private BrotliError DecodeWindowBits(ReadOnlySpan<byte> src)
    {
        if (_br.Read(src, 1) == 0)
        {
            _windowBits = 16;
            return BrotliError.None;
        }

        uint n = _br.Read(src, 3);
        if (n != 0)
        {
            _windowBits = 17 + (int)n;
            return BrotliError.None;
        }

        n = _br.Read(src, 3);
        if (n == 1)
        {
            // 0x11: the large window extension, which brotli refuses unless asked for it.
            return Fail(BrotliError.WindowBits);
        }

        _windowBits = n != 0 ? 8 + (int)n : 17;
        return BrotliError.None;
    }

    /// <summary>
    /// ISLAST, ISLASTEMPTY, MNIBBLES, MLEN and ISUNCOMPRESSED, or a metadata block's MSKIPBYTES and
    /// MSKIPLEN (RFC 7932, section 9.2), into <see cref="_metaBlockRemaining"/>.
    /// </summary>
    private BrotliError DecodeMetaBlockHeader(ReadOnlySpan<byte> src, out bool last, out bool uncompressed, out bool metadata)
    {
        uncompressed = false;
        metadata = false;
        _metaBlockRemaining = 0;
        last = _br.Read(src, 1) != 0;
        if (last && _br.Read(src, 1) != 0)
        {
            // ISLASTEMPTY.
            return BrotliError.None;
        }

        int nibbles = (int)_br.Read(src, 2) + 4;
        if (nibbles == 7)
        {
            metadata = true;
            if (_br.Read(src, 1) != 0)
            {
                return Fail(BrotliError.Reserved);
            }

            int bytes = (int)_br.Read(src, 2);
            if (bytes == 0)
            {
                return BrotliError.None;
            }

            int length = 0;
            for (int i = 0; i < bytes; i++)
            {
                uint b = _br.Read(src, 8);
                if (i + 1 == bytes && bytes > 1 && b == 0)
                {
                    return Fail(BrotliError.ExuberantMetaNibble);
                }

                length |= (int)(b << (i * 8));
            }

            _metaBlockRemaining = length + 1;
            return BrotliError.None;
        }

        int mlen = 0;
        for (int i = 0; i < nibbles; i++)
        {
            uint nibble = _br.Read(src, 4);
            if (i + 1 == nibbles && nibbles > 4 && nibble == 0)
            {
                return Fail(BrotliError.ExuberantNibble);
            }

            mlen |= (int)(nibble << (i * 4));
        }

        if (!last)
        {
            uncompressed = _br.Read(src, 1) != 0;
        }

        _metaBlockRemaining = mlen + 1;
        return BrotliError.None;
    }

    /// <summary>Skips a metadata block's bytes, once at a byte boundary.</summary>
    private BrotliError SkipBytes(int length)
    {
        if (_br.Overrun)
        {
            return BrotliError.Truncated;
        }

        _br.Unload();
        if (_br.End - _br.Next < length)
        {
            return BrotliError.Truncated;
        }

        _br.Next += length;
        return BrotliError.None;
    }

    /// <summary>An uncompressed meta-block's bytes, once at a byte boundary, copied to the destination.</summary>
    private BrotliError CopyUncompressed(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (_br.Overrun)
        {
            return BrotliError.Truncated;
        }

        _br.Unload();
        int length = _metaBlockRemaining;
        if (_br.End - _br.Next < length)
        {
            return BrotliError.Truncated;
        }

        src.Slice(_br.Next, length).CopyTo(dst.Slice(_pos));
        _br.Next += length;
        _pos += length;
        _metaBlockRemaining = 0;
        return BrotliError.None;
    }
}
