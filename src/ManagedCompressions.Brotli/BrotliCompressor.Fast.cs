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
    private OnePassArena? _onePass;
    private TwoPassArena? _twoPass;
    private int[] _hashTableArray = [];
    private byte[] _storageArray = [];

    /// <summary>
    /// The stream of qualities 0 and 1 into <paramref name="dst"/>: its size, or -1 when it does not fit.
    /// </summary>
    private int CompressFast(byte* src, int srcLength, byte* dst, int dstLength)
    {
        // The fast qualities declare a window of at least 256 KiB, the distances of their hash tables.
        EncodeWindowBits(Math.Max(WindowLog, 18), out ushort lastBytes, out int lastBytesBits);
        OnePassArena? onePass = null;
        TwoPassArena? twoPass = null;
        if (Quality == 0)
        {
            onePass = _onePass ??= new OnePassArena();
            onePass.InitCommandPrefixCodes();
        }
        else
        {
            twoPass = _twoPass ??= new TwoPassArena();
        }

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
            byte* storage = inPlace ? dst + written : Storage(maxOutSize);
            storage[0] = (byte)lastBytes;
            storage[1] = (byte)(lastBytes >> 8);
            int storageIx = lastBytesBits;
            int* table = HashTable(blockSize, out int tableSize);
            if (Quality == 0)
            {
                CompressFragment.Compress(onePass!, input, blockSize, isLast, table, tableSize, ref storageIx, storage);
            }
            else
            {
                CompressFragmentTwoPass.Compress(twoPass!, input, blockSize, isLast, table, tableSize, ref storageIx, storage);
            }

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

    /// <summary>
    /// brotli's <c>GetHashTable</c>: a table of zeros, the smallest power of two from 256 that holds the
    /// block or the quality's largest; at quality 0, an odd power.
    /// </summary>
    private int* HashTable(int inputSize, out int tableSize)
    {
        int maxTableSize = Quality == 0 ? 1 << 15 : 1 << 17;
        int size = 256;
        while (size < maxTableSize && size < inputSize)
        {
            size <<= 1;
        }

        if (Quality == 0 && (size & 0xAAAAA) == 0)
        {
            size <<= 1;
        }

        if (_hashTableArray.Length < size)
        {
            _hashTableArray = Pinned<int>(1 << 17);
        }

        int* table = Pointer(_hashTableArray);
        new Span<int>(table, size).Clear();
        tableSize = size;
        return table;
    }

    /// <summary>The compressor's storage, of at least <paramref name="size"/> bytes: brotli's <c>GetBrotliStorage</c>.</summary>
    private byte* Storage(int size)
    {
        if (_storageArray.Length < size)
        {
            _storageArray = Pinned<byte>(size);
        }

        return Pointer(_storageArray);
    }
}
