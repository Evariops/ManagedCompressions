using System;
using ManagedCompressions.Brotli.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// Qualities 2 to 11, as <c>BrotliEncoderCompressStream</c> runs them when it is given the whole input and
/// asked to finish: the input copied into the ring buffer a block at a time, each block processed, and the
/// meta-blocks copied into the destination as they are written.
/// </summary>
public sealed unsafe partial class BrotliCompressor
{
    private EncoderState? _state;

    /// <summary>The stream of qualities 2 to 11 into <paramref name="dst"/>: its size, or -1 when it does not fit.</summary>
    private int CompressStream(byte* src, int srcLength, byte* dst, int dstLength)
    {
        EncoderState state = _state ??= new EncoderState();
        state.Reset(Quality, WindowLog, Mode, (nuint)srcLength);
        byte* nextIn = src;
        nuint availableIn = (nuint)srcLength;
        int written = 0;
        while (true)
        {
            nuint remainingBlockSize = state.RemainingInputBlockSize;
            if (remainingBlockSize != 0 && availableIn != 0)
            {
                nuint copy = Math.Min(remainingBlockSize, availableIn);
                state.CopyInputToRingBuffer(nextIn, copy);
                nextIn += copy;
                availableIn -= copy;
                continue;
            }

            bool isLast = availableIn == 0;
            nuint outSize = state.EncodeData(isLast, false, out byte* output);
            if (outSize != 0)
            {
                if (outSize > (nuint)(dstLength - written))
                {
                    return -1;
                }

                Buffer.MemoryCopy(output, dst + written, outSize, outSize);
                written += (int)outSize;
            }

            if (isLast)
            {
                return written;
            }
        }
    }
}
