using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using NativeCompressions.Interop;

namespace ManagedCompressions.Zstd.Tests.Support;

/// <summary>
/// libzstd 1.5.7 as NativeCompressions ships it, through its raw bindings: the oracle. It is the same
/// library whichever .NET runs the tests, where System.IO.Compression's Zstandard exists from .NET 11
/// only. Every call takes a context of its own, as the platform's encoder and decoder did before it.
/// </summary>
internal static unsafe class NativeZstd
{
    /// <summary><c>ZSTD_versionNumber</c>: 10507 for zstd 1.5.7, the version the compressor reproduces.</summary>
    public static uint VersionNumber => ZstandardNativeMethods.ZSTD_versionNumber();

    public static IEnumerable<string> Levels => ["-5", "-1", "1", "3", "9", "19", "22"];

    /// <summary>
    /// One frame of <paramref name="data"/>: <c>ZSTD_compress2</c> with the parameters of
    /// <paramref name="options"/>, or, with <paramref name="noContentSize"/>, <c>ZSTD_compressStream2</c>
    /// fed 64 KiB at a time, so that the header declares no content size.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, NativeZstdOptions options, bool noContentSize = false)
    {
        ZSTD_CCtx_s* context = ZstandardNativeMethods.ZSTD_createCCtx();
        try
        {
            // A dictionary prepared at a level compresses at that level: a CDict's level takes
            // precedence over the one the context is given.
            int level = options.Dictionary?.Level ?? options.Quality;
            Set(context, ZSTD_cParameter.ZSTD_c_compressionLevel, level);
            if (options.AppendChecksum)
            {
                Set(context, ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            }

            // Asked for, kept off, or left to libzstd, which turns it on by itself for some levels and sizes.
            if (options.EnableLongDistanceMatching || options.DisableLongDistanceMatching)
            {
                Set(context, ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, options.EnableLongDistanceMatching ? 1 : 2);
            }

            if (options.WindowLog > 0)
            {
                Set(context, ZSTD_cParameter.ZSTD_c_windowLog, options.WindowLog);
            }

            if (options.TargetBlockSize > 0)
            {
                Set(context, ZSTD_cParameter.ZSTD_c_targetCBlockSize, options.TargetBlockSize);
            }

            if (!options.ContentSizeFlag)
            {
                Set(context, ZSTD_cParameter.ZSTD_c_contentSizeFlag, 0);
            }

            if (!options.DictionaryIdFlag)
            {
                Set(context, ZSTD_cParameter.ZSTD_c_dictIDFlag, 0);
            }

            if (options.Dictionary is { } dictionary)
            {
                Check(ZstandardNativeMethods.ZSTD_CCtx_refCDict(context, dictionary.ForCompression(level)));
            }

            int capacity = Math.Max(
                (int)ZstandardNativeMethods.ZSTD_compressBound((nuint)data.Length), data.Length + (data.Length >> 7) + 1024);
            byte[] output = new byte[capacity];
            fixed (byte* source = data)
            fixed (byte* destination = output)
            {
                nuint size = noContentSize
                    ? CompressInChunks(context, source, data.Length, destination, capacity)
                    : Check(ZstandardNativeMethods.ZSTD_compress2(context, destination, (nuint)capacity, source, (nuint)data.Length));
                return output.AsSpan(0, (int)size).ToArray();
            }
        }
        finally
        {
            ZstandardNativeMethods.ZSTD_freeCCtx(context);
        }
    }

    /// <summary>
    /// Decodes the frame that starts <paramref name="frame"/> with <c>ZSTD_decompressStream</c>, the
    /// streaming decoder System.IO.Compression's <c>ZstandardDecoder</c> wraps.
    /// </summary>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the frame is decoded; <see cref="OperationStatus.InvalidData"/>
    /// when libzstd refuses it; otherwise <see cref="OperationStatus.DestinationTooSmall"/> when the
    /// destination is full, and <see cref="OperationStatus.NeedMoreData"/> when the source ends first.
    /// </returns>
    public static OperationStatus Decompress(
        ReadOnlySpan<byte> frame, Span<byte> destination, NativeZstdDictionary? dictionary, out int consumed, out int written)
    {
        ZSTD_DCtx_s* context = ZstandardNativeMethods.ZSTD_createDCtx();
        try
        {
            if (dictionary is not null)
            {
                Check(ZstandardNativeMethods.ZSTD_DCtx_refDDict(context, dictionary.ForDecompression()));
            }

            fixed (byte* source = frame)
            fixed (byte* target = destination)
            {
                var input = new ZSTD_inBuffer_s { src = source, size = (nuint)frame.Length, pos = 0 };
                var output = new ZSTD_outBuffer_s { dst = target, size = (nuint)destination.Length, pos = 0 };
                OperationStatus status;
                while (true)
                {
                    nuint read = input.pos;
                    nuint wrote = output.pos;
                    nuint result = ZstandardNativeMethods.ZSTD_decompressStream(context, &output, &input);
                    if (ZstandardNativeMethods.ZSTD_isError(result) != 0)
                    {
                        status = OperationStatus.InvalidData;
                        break;
                    }

                    if (result == 0)
                    {
                        status = OperationStatus.Done;
                        break;
                    }

                    if (input.pos == read && output.pos == wrote)
                    {
                        status = output.pos == output.size ? OperationStatus.DestinationTooSmall : OperationStatus.NeedMoreData;
                        break;
                    }
                }

                consumed = (int)input.pos;
                written = (int)output.pos;
                return status;
            }
        }
        finally
        {
            ZstandardNativeMethods.ZSTD_freeDCtx(context);
        }
    }

    /// <summary>
    /// <c>ZSTD_compressStream2</c> fed <paramref name="chunk"/> bytes at a time, each piece followed by
    /// <c>ZSTD_e_flush</c> when <paramref name="flush"/>: a frame whose blocks end where the caller
    /// flushed. Its header declares the content size only when <paramref name="pledge"/>.
    /// </summary>
    public static byte[] CompressStream(ReadOnlySpan<byte> data, NativeZstdOptions options, int chunk, bool flush, bool pledge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunk, 1);
        ZSTD_CCtx_s* context = ZstandardNativeMethods.ZSTD_createCCtx();
        try
        {
            int level = options.Dictionary?.Level ?? options.Quality;
            Set(context, ZSTD_cParameter.ZSTD_c_compressionLevel, level);
            if (options.AppendChecksum)
            {
                Set(context, ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            }

            if (options.EnableLongDistanceMatching)
            {
                Set(context, ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
            }

            if (options.WindowLog > 0)
            {
                Set(context, ZSTD_cParameter.ZSTD_c_windowLog, options.WindowLog);
            }

            if (options.Dictionary is { } dictionary)
            {
                Check(ZstandardNativeMethods.ZSTD_CCtx_refCDict(context, dictionary.ForCompression(level)));
            }

            if (pledge)
            {
                Check(ZstandardNativeMethods.ZSTD_CCtx_setPledgedSrcSize(context, (ulong)data.Length));
            }

            // Each flush may close a block of its own, three bytes of header and a byte of content more.
            int pieces = (data.Length / Math.Max(chunk, 1)) + 2;
            int capacity = (int)ZstandardNativeMethods.ZSTD_compressBound((nuint)data.Length) + (pieces * 8) + 1024;
            byte[] output = new byte[capacity];
            fixed (byte* source = data)
            fixed (byte* destination = output)
            {
                var buffer = new ZSTD_outBuffer_s { dst = destination, size = (nuint)capacity, pos = 0 };
                int offset = 0;
                do
                {
                    int size = Math.Min(chunk, data.Length - offset);
                    bool final = offset + size == data.Length;
                    var input = new ZSTD_inBuffer_s { src = source + offset, size = (nuint)size, pos = 0 };
                    ZSTD_EndDirective directive = final ? ZSTD_EndDirective.ZSTD_e_end : flush ? ZSTD_EndDirective.ZSTD_e_flush : ZSTD_EndDirective.ZSTD_e_continue;
                    nuint remaining;
                    do
                    {
                        remaining = Check(ZstandardNativeMethods.ZSTD_compressStream2(context, &buffer, &input, directive));
                    }
                    while (directive == ZSTD_EndDirective.ZSTD_e_continue ? input.pos < input.size : remaining != 0);

                    offset += size;
                }
                while (offset < data.Length);

                return output.AsSpan(0, (int)buffer.pos).ToArray();
            }
        }
        finally
        {
            ZstandardNativeMethods.ZSTD_freeCCtx(context);
        }
    }

    /// <summary>
    /// A whole stream through libzstd's streaming decoder, <c>ZSTD_decompressStream</c>, frame after
    /// frame, each call handed all the input left and all the room left in a destination of
    /// <paramref name="capacity"/> bytes, the window capped at <paramref name="windowLogMax"/>.
    /// </summary>
    /// <returns>
    /// Whether libzstd decodes the stream: every frame whole, and nothing left. The content is what it
    /// wrote, also when it refuses.
    /// </returns>
    public static bool DecompressStream(
        ReadOnlySpan<byte> stream, int capacity, NativeZstdDictionary? dictionary, int windowLogMax, out byte[] content)
    {
        ZSTD_DCtx_s* context = ZstandardNativeMethods.ZSTD_createDCtx();
        try
        {
            Check(ZstandardNativeMethods.ZSTD_DCtx_setParameter(context, ZSTD_dParameter.ZSTD_d_windowLogMax, windowLogMax));
            if (dictionary is not null)
            {
                Check(ZstandardNativeMethods.ZSTD_DCtx_refDDict(context, dictionary.ForDecompression()));
            }

            byte[] output = new byte[Math.Max(capacity, 1)];
            fixed (byte* source = stream)
            fixed (byte* target = output)
            {
                var input = new ZSTD_inBuffer_s { src = source, size = (nuint)stream.Length, pos = 0 };
                var buffer = new ZSTD_outBuffer_s { dst = target, size = (nuint)capacity, pos = 0 };
                bool atBoundary = true;
                while (true)
                {
                    nuint read = input.pos;
                    nuint wrote = buffer.pos;
                    nuint result = ZstandardNativeMethods.ZSTD_decompressStream(context, &buffer, &input);
                    if (ZstandardNativeMethods.ZSTD_isError(result) != 0)
                    {
                        content = output.AsSpan(0, (int)buffer.pos).ToArray();
                        return false;
                    }

                    atBoundary = result == 0;
                    if (input.pos == input.size && atBoundary)
                    {
                        break;
                    }

                    if (input.pos == read && buffer.pos == wrote)
                    {
                        break;
                    }
                }

                content = output.AsSpan(0, (int)buffer.pos).ToArray();
                return atBoundary && input.pos == input.size;
            }
        }
        finally
        {
            ZstandardNativeMethods.ZSTD_freeDCtx(context);
        }
    }

    /// <summary>Fails with libzstd's name for the error, when <paramref name="result"/> is one.</summary>
    internal static nuint Check(nuint result) =>
        ZstandardNativeMethods.ZSTD_isError(result) != 0
            ? throw new InvalidOperationException("libzstd: " + Marshal.PtrToStringAnsi((nint)ZstandardNativeMethods.ZSTD_getErrorName(result)))
            : result;

    private static void Set(ZSTD_CCtx_s* context, ZSTD_cParameter parameter, int value) =>
        Check(ZstandardNativeMethods.ZSTD_CCtx_setParameter(context, parameter, value));

    /// <summary>
    /// The frame fed 64 KiB at a time, the last piece with <c>ZSTD_e_end</c>: the encoder cannot know
    /// the size when it writes the header.
    /// </summary>
    private static nuint CompressInChunks(ZSTD_CCtx_s* context, byte* source, int length, byte* destination, int capacity)
    {
        const int Chunk = 64 * 1024;
        var output = new ZSTD_outBuffer_s { dst = destination, size = (nuint)capacity, pos = 0 };
        int offset = 0;
        do
        {
            int size = Math.Min(Chunk, length - offset);
            bool final = offset + size == length;
            var input = new ZSTD_inBuffer_s { src = source + offset, size = (nuint)size, pos = 0 };
            ZSTD_EndDirective directive = final ? ZSTD_EndDirective.ZSTD_e_end : ZSTD_EndDirective.ZSTD_e_continue;
            nuint remaining;
            do
            {
                remaining = Check(ZstandardNativeMethods.ZSTD_compressStream2(context, &output, &input, directive));
            }
            while (final ? remaining != 0 : input.pos < input.size);

            offset += size;
        }
        while (offset < length);

        return output.pos;
    }
}

/// <summary>
/// The parameters of a frame <see cref="NativeZstd.Compress"/> writes, named as System.IO.Compression's
/// <c>ZstandardCompressionOptions</c> names them.
/// </summary>
internal sealed class NativeZstdOptions
{
    /// <summary>The level, 0 for libzstd's default; a dictionary prepared at a level overrides it.</summary>
    public int Quality { get; set; }

    /// <summary><c>ZSTD_c_checksumFlag</c>.</summary>
    public bool AppendChecksum { get; set; }

    /// <summary><c>ZSTD_c_enableLongDistanceMatching</c> turned on; otherwise libzstd decides.</summary>
    public bool EnableLongDistanceMatching { get; set; }

    /// <summary><c>ZSTD_c_enableLongDistanceMatching</c> kept off (<c>ZSTD_ps_disable</c>).</summary>
    public bool DisableLongDistanceMatching { get; set; }

    /// <summary><c>ZSTD_c_contentSizeFlag</c>: whether the header declares the content's size; true by default.</summary>
    public bool ContentSizeFlag { get; set; } = true;

    /// <summary><c>ZSTD_c_dictIDFlag</c>: whether the header names the dictionary; true by default.</summary>
    public bool DictionaryIdFlag { get; set; } = true;

    /// <summary><c>ZSTD_c_windowLog</c>; 0 for the level's.</summary>
    public int WindowLog { get; set; }

    /// <summary><c>ZSTD_c_targetCBlockSize</c>; 0 for none.</summary>
    public int TargetBlockSize { get; set; }

    public NativeZstdDictionary? Dictionary { get; set; }
}

/// <summary>
/// A dictionary for <see cref="NativeZstd"/>, as System.IO.Compression's <c>ZstandardDictionary</c> is
/// one: its bytes, digested on first use for compression (a CDict a level) and for decompression (a
/// DDict). Both are read-only once built, which lets the tests that share one run side by side.
/// </summary>
internal sealed unsafe class NativeZstdDictionary : IDisposable
{
    private readonly Dictionary<int, nint> _compression = [];
    private nint _decompression;

    private NativeZstdDictionary(byte[] bytes, int? level)
    {
        Bytes = bytes;
        Level = level;
    }

    /// <summary>The dictionary, as the decoder receives it.</summary>
    public byte[] Bytes { get; }

    /// <summary>The level the dictionary is prepared at, which its frames take; null for the frame's own.</summary>
    public int? Level { get; }

    public static NativeZstdDictionary Create(ReadOnlySpan<byte> bytes) => new(bytes.ToArray(), null);

    public static NativeZstdDictionary Create(ReadOnlySpan<byte> bytes, int level) => new(bytes.ToArray(), level);

    /// <summary>
    /// <c>ZDICT_trainFromBuffer</c>: a dictionary of at most <paramref name="capacity"/> bytes trained on
    /// the samples, or null when libzstd finds nothing to learn from them.
    /// </summary>
    public static NativeZstdDictionary? Train(ReadOnlySpan<byte> samples, ReadOnlySpan<int> lengths, int capacity)
    {
        byte[] buffer = new byte[capacity];
        nuint[] sizes = new nuint[lengths.Length];
        for (int i = 0; i < lengths.Length; i++)
        {
            sizes[i] = (nuint)lengths[i];
        }

        fixed (byte* dictionary = buffer)
        fixed (byte* source = samples)
        fixed (nuint* size = sizes)
        {
            nuint trained = ZstandardNativeMethods.ZDICT_trainFromBuffer(dictionary, (nuint)capacity, source, size, (uint)lengths.Length);
            return ZstandardNativeMethods.ZDICT_isError(trained) != 0 ? null : Create(buffer.AsSpan(0, (int)trained));
        }
    }

    internal ZSTD_CDict_s* ForCompression(int level)
    {
        lock (_compression)
        {
            if (!_compression.TryGetValue(level, out nint digested))
            {
                fixed (byte* bytes = Bytes)
                {
                    digested = (nint)ZstandardNativeMethods.ZSTD_createCDict(bytes, (nuint)Bytes.Length, level);
                }

                if (digested == 0)
                {
                    throw new InvalidDataException("libzstd refuses the dictionary.");
                }

                _compression.Add(level, digested);
            }

            return (ZSTD_CDict_s*)digested;
        }
    }

    internal ZSTD_DDict_s* ForDecompression()
    {
        lock (_compression)
        {
            if (_decompression == 0)
            {
                fixed (byte* bytes = Bytes)
                {
                    _decompression = (nint)ZstandardNativeMethods.ZSTD_createDDict(bytes, (nuint)Bytes.Length);
                }

                if (_decompression == 0)
                {
                    throw new InvalidDataException("libzstd refuses the dictionary.");
                }
            }

            return (ZSTD_DDict_s*)_decompression;
        }
    }

    public void Dispose()
    {
        lock (_compression)
        {
            foreach (nint digested in _compression.Values)
            {
                ZstandardNativeMethods.ZSTD_freeCDict((ZSTD_CDict_s*)digested);
            }

            _compression.Clear();
            if (_decompression != 0)
            {
                ZstandardNativeMethods.ZSTD_freeDDict((ZSTD_DDict_s*)_decompression);
                _decompression = 0;
            }
        }
    }
}
