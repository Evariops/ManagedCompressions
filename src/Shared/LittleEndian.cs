using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Internal;

/// <summary>
/// The codecs read their formats' little-endian fields, and write them, in the processor's byte order,
/// as unaligned loads and stores of whole words: on a big-endian processor (IBM Z) every block, frame
/// or stream would come out wrong. They refuse to start there instead.
/// </summary>
internal static class LittleEndian
{
    /// <summary>
    /// Throws on a big-endian processor. <see cref="BitConverter.IsLittleEndian"/> is a constant to
    /// the JIT and to Native AOT, so elsewhere this compiles to nothing.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The processor is big-endian.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Require()
    {
        if (!BitConverter.IsLittleEndian)
        {
            ThrowBigEndian();
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBigEndian() =>
        throw new PlatformNotSupportedException(
            $"{typeof(LittleEndian).Assembly.GetName().Name} runs on little-endian processors only: it reads and writes its format in the processor's byte order.");
}
