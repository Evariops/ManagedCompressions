using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// brotli's <c>RingBuffer</c> (<c>c/enc/ringbuffer.h</c>): the window, which the input is copied into,
/// with a copy of its first bytes past its end, the tail, and of its last two bytes before its start.
/// </summary>
/// <remarks>
/// brotli allocates the buffer at the first write's size when that write is smaller than the tail, and
/// the whole buffer otherwise; the bytes past the written ones, but seven zeros, are never read. The
/// buffer here lives on the pinned heap and is kept from one stream to the next.
/// </remarks>
internal sealed unsafe class RingBuffer
{
    private const int SlackForEightByteHashingEverywhere = 7;

    private byte[] _array = [];
    private uint _size;
    private uint _tailSize;
    private uint _totalSize;
    private uint _curSize;

    /// <summary>The window's mask.</summary>
    public uint Mask { get; private set; }

    /// <summary>The position to write at, its bit 31 set past the first lap.</summary>
    public uint Pos { get; private set; }

    /// <summary>The window's first byte; the two before it are the window's last two.</summary>
    public byte* Buffer { get; private set; }

    /// <summary>brotli's <c>RingBufferSetup</c>, and <c>RingBufferInit</c>: an empty window.</summary>
    public void Setup(int windowBits, int tailBits)
    {
        _size = 1u << windowBits;
        Mask = _size - 1;
        _tailSize = 1u << tailBits;
        _totalSize = _size + _tailSize;
        _curSize = 0;
        Pos = 0;
        Buffer = null;
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
            byte[] array = GC.AllocateUninitializedArray<byte>(needed, pinned: true);
            if (Buffer != null)
            {
                new ReadOnlySpan<byte>(Buffer - 2, 2 + (int)_curSize + SlackForEightByteHashingEverywhere).CopyTo(array);
            }

            _array = array;
        }

        _curSize = length;
        Buffer = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_array)) + 2;
        Buffer[-2] = Buffer[-1] = 0;
        new Span<byte>(Buffer + _curSize, SlackForEightByteHashingEverywhere).Clear();
    }

    /// <summary>brotli's <c>RingBufferWrite</c>.</summary>
    public void Write(byte* bytes, nuint n)
    {
        if (Pos == 0 && n < _tailSize)
        {
            // The first write, smaller than a block: a buffer of its size, without the tail.
            Pos = (uint)n;
            InitBuffer(Pos);
            System.Buffer.MemoryCopy(bytes, Buffer, n, n);
            return;
        }

        if (_curSize < _totalSize)
        {
            // The whole buffer, at last.
            InitBuffer(_totalSize);
            Buffer[_size - 2] = 0;
            Buffer[_size - 1] = 0;
            Buffer[_size] = 241;
        }

        nuint maskedPos = Pos & Mask;
        if (maskedPos < _tailSize)
        {
            // The tail: a copy of the window's beginning.
            nuint p = _size + maskedPos;
            nuint count = Math.Min(n, _tailSize - maskedPos);
            System.Buffer.MemoryCopy(bytes, Buffer + p, count, count);
        }

        if (maskedPos + n <= _size)
        {
            System.Buffer.MemoryCopy(bytes, Buffer + maskedPos, n, n);
        }
        else
        {
            nuint first = Math.Min(n, _totalSize - maskedPos);
            System.Buffer.MemoryCopy(bytes, Buffer + maskedPos, first, first);
            nuint rest = n - (_size - maskedPos);
            System.Buffer.MemoryCopy(bytes + (_size - maskedPos), Buffer, rest, rest);
        }

        bool notFirstLap = (Pos & (1u << 31)) != 0;
        const uint posMask = (1u << 31) - 1;
        Buffer[-2] = Buffer[_size - 2];
        Buffer[-1] = Buffer[_size - 1];
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
