using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// The state of qualities 0 and 1 (<c>c/enc/encode.c</c>): the arena of the quality, the hash table
/// cleared for each fragment, and the storage of fragments that do not fit the destination.
/// </summary>
internal sealed unsafe class FastEncoder
{
    private readonly PinnedBuffer<byte> _storage = new();
    private OnePassArena? _onePass;
    private TwoPassArena? _twoPass;
    private int[] _hashTableArray = [];
    private int _quality;

    /// <summary>A new stream at quality 0 or 1: brotli's <c>EnsureInitialized</c>, for the fast qualities.</summary>
    public void Reset(int quality)
    {
        _quality = quality;
        if (quality == 0)
        {
            (_onePass ??= new OnePassArena()).InitCommandPrefixCodes();
        }
        else
        {
            _twoPass ??= new TwoPassArena();
        }
    }

    /// <summary>brotli's <c>GetBrotliStorage</c>: at least <paramref name="size"/> bytes, kept.</summary>
    public byte* Storage(nuint size) => _storage.Ensure(size);

    /// <summary>
    /// <c>BrotliCompressFragmentFast</c> or <c>BrotliCompressFragmentTwoPass</c>: <paramref name="blockSize"/>
    /// bytes of <paramref name="input"/> appended at the bit <paramref name="storageIx"/>.
    /// </summary>
    public void CompressFragment(byte* input, int blockSize, bool isLast, ref int storageIx, byte* storage)
    {
        int* table = HashTable(blockSize, out int tableSize);
        if (_quality == 0)
        {
            Internal.CompressFragment.Compress(_onePass!, input, blockSize, isLast, table, tableSize, ref storageIx, storage);
        }
        else
        {
            CompressFragmentTwoPass.Compress(_twoPass!, input, blockSize, isLast, table, tableSize, ref storageIx, storage);
        }
    }

    /// <summary>
    /// brotli's <c>GetHashTable</c>: a table of zeros, the smallest power of two from 256 that holds the
    /// block or the quality's largest; at quality 0, an odd power.
    /// </summary>
    private int* HashTable(int inputSize, out int tableSize)
    {
        int maxTableSize = _quality == 0 ? 1 << 15 : 1 << 17;
        int size = 256;
        while (size < maxTableSize && size < inputSize)
        {
            size <<= 1;
        }

        if (_quality == 0 && (size & 0xAAAAA) == 0)
        {
            size <<= 1;
        }

        if (_hashTableArray.Length < size)
        {
            _hashTableArray = GC.AllocateUninitializedArray<int>(1 << 17, pinned: true);
        }

        int* table = (int*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_hashTableArray));
        new Span<int>(table, size).Clear();
        tableSize = size;
        return table;
    }
}
