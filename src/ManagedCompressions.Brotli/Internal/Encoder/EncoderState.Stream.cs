using System;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>What <c>BrotliEncoderCompressStream</c> reads and writes of the state, for the streaming encoder.</summary>
internal sealed partial class EncoderState
{
    /// <summary>The bits of the stream's last byte not written yet: brotli's <c>last_bytes_</c>.</summary>
    public ushort LastBytes
    {
        get => _lastBytes;
        set => _lastBytes = value;
    }

    /// <summary>The number of those bits: brotli's <c>last_bytes_bits_</c>.</summary>
    public int LastBytesBits
    {
        get => _lastBytesBits;
        set => _lastBytesBits = value;
    }

    /// <summary>
    /// brotli's <c>UpdateSizeHint</c>: without a hint, the input not processed yet and the input still to
    /// come, up to 1 GiB, become it at the first block.
    /// </summary>
    public void UpdateSizeHint(nuint availableIn)
    {
        if (_sizeHint == 0)
        {
            ulong delta = _inputPos - _lastProcessedPos;
            ulong tail = availableIn;
            const uint limit = 1u << 30;
            _sizeHint = delta >= limit || tail >= limit || delta + tail >= limit ? limit : (nuint)(delta + tail);
        }
    }
}
