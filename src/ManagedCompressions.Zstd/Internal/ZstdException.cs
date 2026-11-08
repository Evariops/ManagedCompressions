using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

namespace ManagedCompressions.Zstd.Internal;

/// <summary>
/// Raised inside the decoder with the <see cref="ZstdError"/> that refuses the frame, and caught at
/// the API boundary, which turns it into a status and records the error.
/// </summary>
internal sealed class ZstdException : Exception
{
    public ZstdException(ZstdError error)
        : base($"Invalid zstd data: {error}.")
    {
        Error = error;
    }

    public ZstdError Error { get; }
}

/// <summary>Cold throw sites: kept out of line so that the hot paths only carry a compare and a call.</summary>
internal static class Throw
{
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Error(ZstdError error) => throw new ZstdException(error);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T Error<T>(ZstdError error) => throw new ZstdException(error);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Dictionary(string reason) =>
        throw new InvalidDataException($"Invalid zstd dictionary: {reason}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ArgumentNull(string name) => throw new ArgumentNullException(name);
}
