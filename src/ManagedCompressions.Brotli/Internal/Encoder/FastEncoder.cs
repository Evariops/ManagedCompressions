using System;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// The state of qualities 0 and 1 (<c>c/enc/encode.c</c>): the arena of the quality, the hash table
/// cleared for each fragment, and the storage of fragments that do not fit the destination.
/// </summary>
internal sealed class FastEncoder
{
    private readonly GrowableBuffer<byte> _storage = new();
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
    public byte[] Storage(nuint size) => _storage.Ensure(size);

    /// <summary>
    /// <c>BrotliCompressFragmentFast</c> or <c>BrotliCompressFragmentTwoPass</c>: <paramref name="input"/>
    /// appended at the bit <paramref name="storageIx"/>.
    /// </summary>
    public void CompressFragment(ReadOnlySpan<byte> input, bool isLast, ref int storageIx, Span<byte> storage)
    {
        Span<int> table = HashTable(input.Length, out int tableSize);
        if (_quality == 0)
        {
            Internal.CompressFragment.Compress(_onePass!, input, isLast, table, tableSize, ref storageIx, storage);
        }
        else
        {
            CompressFragmentTwoPass.Compress(_twoPass!, input, isLast, table, tableSize, ref storageIx, storage);
        }
    }

    /// <summary>
    /// brotli's <c>GetHashTable</c>: a table of zeros, the smallest power of two from 256 that holds the
    /// block or the quality's largest; at quality 0, an odd power.
    /// </summary>
    private Span<int> HashTable(int inputSize, out int tableSize)
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
            _hashTableArray = new int[1 << 17];
        }

        Span<int> table = _hashTableArray.AsSpan(0, size);
        table.Clear();
        tableSize = size;
        return table;
    }
}
