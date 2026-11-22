using System;
using ManagedCompressions.Brotli.Internal;

namespace ManagedCompressions.Brotli;

/// <summary>
/// Qualities 0 and 1, as <c>BrotliEncoderCompressStreamFast</c> runs them when it is given the whole
/// input and asked to finish: fragments of the window's size, each compressed from the input directly,
/// with a hash table cleared for it.
/// </summary>
public sealed unsafe partial class BrotliCompressor
{
    private FastEncoder? _fast;

    /// <summary>
    /// The stream of qualities 0 and 1 into <paramref name="dst"/>: its size, or -1 when it does not fit.
    /// </summary>
    private int CompressFast(byte* src, int srcLength, byte* dst, int dstLength)
    {
        // The fast qualities declare a window of at least 256 KiB, the distances of their hash tables.
        EncoderState.EncodeWindowBits(Math.Max(WindowLog, 18), out ushort lastBytes, out int lastBytesBits);
        FastEncoder fast = _fast ??= new FastEncoder();
        fast.Reset(Quality);
        int blockSizeLimit = 1 << WindowLog;
        byte* input = src;
        int remaining = srcLength;
        int written = 0;
        while (true)
        {
            int blockSize = Math.Min(blockSizeLimit, remaining);
            bool isLast = remaining == blockSize;

            // In place when the destination has the room brotli asks for, as brotli does.
            int maxOutSize = (2 * blockSize) + 503;
            bool inPlace = maxOutSize <= dstLength - written;
            byte* storage = inPlace ? dst + written : fast.Storage((nuint)maxOutSize);
            storage[0] = (byte)lastBytes;
            storage[1] = (byte)(lastBytes >> 8);
            int storageIx = lastBytesBits;
            fast.CompressFragment(input, blockSize, isLast, ref storageIx, storage);
            input += blockSize;
            remaining -= blockSize;
            int outBytes = storageIx >> 3;
            if (!inPlace)
            {
                if (outBytes > dstLength - written)
                {
                    return -1;
                }

                Buffer.MemoryCopy(storage, dst + written, outBytes, outBytes);
            }

            written += outBytes;
            lastBytes = storage[storageIx >> 3];
            lastBytesBits = storageIx & 7;
            if (isLast)
            {
                return written;
            }
        }
    }
}
