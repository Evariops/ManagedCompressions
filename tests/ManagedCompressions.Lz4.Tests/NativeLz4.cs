using System;
using System.Runtime.InteropServices;
using NativeCompressions.Interop;

namespace ManagedCompressions.Lz4.Tests;

/// <summary>lz4 1.10.0, as NativeCompressions ships it: the oracle of the tests.</summary>
internal static unsafe class NativeLz4
{
    /// <summary>
    /// What lz4 writes at a level, through the entry points the benchmark measures: level 1 and below
    /// <c>LZ4_compress_fast_extState</c> at acceleration 1 - level, 2 to 12 <c>LZ4_compress_HC_extStateHC</c>.
    /// Empty when the block does not fit in <paramref name="capacity"/>.
    /// </summary>
    public static byte[] Compress(byte[] data, int level, int capacity = -1)
    {
        byte[] compressed = new byte[capacity < 0 ? LZ4NativeMethods.LZ4_compressBound(data.Length) : capacity];
        int size;
        bool fast = level <= 1;
        void* state = NativeMemory.AlignedAlloc((nuint)(fast ? LZ4NativeMethods.LZ4_sizeofState() : LZ4NativeMethods.LZ4_sizeofStateHC()), 64);
        try
        {
            // Never a null source, even when empty: lz4's optimal parser reads through it.
            fixed (byte* src = &MemoryMarshal.GetArrayDataReference(data))
            fixed (byte* dst = &MemoryMarshal.GetArrayDataReference(compressed))
            {
                size = fast
                    ? LZ4NativeMethods.LZ4_compress_fast_extState(state, src, dst, data.Length, compressed.Length, level < 1 ? 1 - level : 1)
                    : LZ4NativeMethods.LZ4_compress_HC_extStateHC(state, src, dst, data.Length, compressed.Length, level);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(state);
        }

        return compressed.AsSpan(0, size).ToArray();
    }
}
