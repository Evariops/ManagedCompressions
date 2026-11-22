using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Brotli.Internal;

/// <summary>
/// The codecs read and write the bit stream as unaligned little-endian words, in the processor's byte
/// order: on a big-endian processor (IBM Z) every stream would come out wrong. They refuse to start
/// there instead.
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
            "ManagedCompressions.Brotli runs on little-endian processors only: it reads the bit stream in the processor's byte order.");
}
