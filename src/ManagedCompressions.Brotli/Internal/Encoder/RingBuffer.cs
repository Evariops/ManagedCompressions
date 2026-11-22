using System;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's <c>RingBuffer</c> (<c>c/enc/ringbuffer.h</c>): the window, which the input is copied into,
/// with a copy of its first bytes past its end, the tail, and of its last two bytes before its start.
/// </summary>
/// <remarks>
/// brotli allocates the buffer at the first write's size when that write is smaller than the tail, and
/// the whole buffer otherwise; the bytes past the written ones, but seven zeros, are never read. The
/// buffer here is kept from one stream to the next.
/// </remarks>
internal sealed class RingBuffer
{
    private const int SlackForEightByteHashingEverywhere = 7;

    private byte[] _array = [];
    private uint _size;
    private uint _tailSize;
    private uint _totalSize;
    private uint _curSize;
    private bool _hasBuffer;

    /// <summary>The window's mask.</summary>
    public uint Mask { get; private set; }

    /// <summary>The position to write at, its bit 31 set past the first lap.</summary>
    public uint Pos { get; private set; }

    /// <summary>Where the window starts in <see cref="_array"/>: the two bytes before it are the window's last two.</summary>
    private const int Start = 2;

    /// <summary>Whether the window has its buffer: not before the first write.</summary>
    public bool HasBuffer => _hasBuffer;

    /// <summary>The window, its tail and the seven bytes past it, from its first byte.</summary>
    public Span<byte> Buffer => _hasBuffer ? _array.AsSpan(Start, (int)_curSize + SlackForEightByteHashingEverywhere) : default;

    /// <summary>brotli's <c>RingBufferSetup</c>, and <c>RingBufferInit</c>: an empty window.</summary>
    public void Setup(int windowBits, int tailBits)
    {
        _size = 1u << windowBits;
        Mask = _size - 1;
        _tailSize = 1u << tailBits;
        _totalSize = _size + _tailSize;
        _curSize = 0;
        Pos = 0;
        _hasBuffer = false;
    }

    /// <summary>
    /// brotli's <c>RingBufferInitBuffer</c>: a buffer of <paramref name="length"/> bytes, which keeps
    /// those written, with zeros in the two bytes before and the seven after.
    /// </summary>
    private void InitBuffer(uint length)
    {
        int needed = 2 + (int)length + SlackForEightByteHashingEverywhere;
        if (_array.Length < needed)
        {
            byte[] array = new byte[needed];
            if (_hasBuffer)
            {
                _array.AsSpan(0, Start + (int)_curSize + SlackForEightByteHashingEverywhere).CopyTo(array);
            }

            _array = array;
        }

        _curSize = length;
        _hasBuffer = true;
        _array[0] = _array[1] = 0;
        _array.AsSpan(Start + (int)_curSize, SlackForEightByteHashingEverywhere).Clear();
    }

    /// <summary>brotli's <c>RingBufferWrite</c>.</summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        nuint n = (nuint)bytes.Length;
        Span<byte> buffer;
        if (Pos == 0 && n < _tailSize)
        {
            // The first write, smaller than a block: a buffer of its size, without the tail.
            Pos = (uint)n;
            InitBuffer(Pos);
            bytes.CopyTo(Buffer);
            return;
        }

        if (_curSize < _totalSize)
        {
            // The whole buffer, at last.
            InitBuffer(_totalSize);
            buffer = Buffer;
            buffer[(int)_size - 2] = 0;
            buffer[(int)_size - 1] = 0;
            buffer[(int)_size] = 241;
        }

        buffer = Buffer;

        nuint maskedPos = Pos & Mask;
        if (maskedPos < _tailSize)
        {
            // The tail: a copy of the window's beginning.
            nuint p = _size + maskedPos;
            nuint count = Math.Min(n, _tailSize - maskedPos);
            bytes.Slice(0, (int)count).CopyTo(buffer.Slice((int)p));
        }

        if (maskedPos + n <= _size)
        {
            bytes.CopyTo(buffer.Slice((int)maskedPos));
        }
        else
        {
            nuint first = Math.Min(n, _totalSize - maskedPos);
            bytes.Slice(0, (int)first).CopyTo(buffer.Slice((int)maskedPos));
            bytes.Slice((int)(_size - maskedPos)).CopyTo(buffer);
        }

        bool notFirstLap = (Pos & (1u << 31)) != 0;
        const uint posMask = (1u << 31) - 1;
        _array[0] = buffer[(int)_size - 2];
        _array[1] = buffer[(int)_size - 1];
        Pos = (Pos & posMask) + (uint)(n & posMask);
        if (notFirstLap)
        {
            Pos |= 1u << 31;
        }
    }

    /// <summary>
    /// Whether the window is still in its first lap: brotli then writes seven zeros past the input, which
    /// its hashing reads.
    /// </summary>
    public bool FirstLap => Pos <= Mask;
}
