using System;
using System.IO;
using System.Runtime.InteropServices;
using NativeCompressions.Interop;

namespace ManagedCompressions.Fuzz;

/// <summary>
/// zstd 1.5.7 and lz4 1.10.0 as NativeCompressions ships them, through its raw bindings: the oracles.
/// A process runs one target on one thread, so each keeps one context, created on first use, but for
/// the streaming decoder's.
/// </summary>
internal static unsafe class Native
{
    private static ZSTD_DCtx_s* s_decompression;
    private static ZSTD_CCtx_s* s_compression;
    private static void* s_lz4State;
    private static void* s_lz4StateHC;

    /// <summary>
    /// libzstd's one-shot decoder on the first frame of <paramref name="source"/>, as libzstd delimits
    /// it (<c>ZSTD_findFrameCompressedSize</c>, then <c>ZSTD_decompress_usingDDict</c>): the semantics
    /// ManagedCompressions.Zstd mirrors.
    /// </summary>
    /// <returns>The content size, or -1 when libzstd refuses the frame.</returns>
    public static int ZstdDecompress(ReadOnlySpan<byte> source, Span<byte> destination, ZSTD_DDict_s* dictionary, out int frameSize)
    {
        frameSize = 0;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            nuint size = ZstandardNativeMethods.ZSTD_findFrameCompressedSize(src, (nuint)source.Length);
            if (ZstandardNativeMethods.ZSTD_isError(size) != 0)
            {
                return -1;
            }

            frameSize = (int)size;
            if (s_decompression == null)
            {
                s_decompression = ZstandardNativeMethods.ZSTD_createDCtx();
            }

            nuint result = ZstandardNativeMethods.ZSTD_decompress_usingDDict(s_decompression, dst, (nuint)destination.Length, src, size, dictionary);
            return ZstandardNativeMethods.ZSTD_isError(result) != 0 ? -1 : (int)result;
        }
    }

    /// <summary>How <see cref="ZstdDecompressStream"/> ends.</summary>
    public enum StreamEnd
    {
        /// <summary>Every frame decoded, and the stream ends where the last one does.</summary>
        Decoded,

        /// <summary>No error, but no more progress: the stream ends inside a frame, or the destination is full.</summary>
        Incomplete,

        /// <summary>An error.</summary>
        Refused,
    }

    /// <summary>
    /// libzstd's streaming decoder, <c>ZSTD_decompressStream</c>, handed the whole stream and the whole
    /// destination, call after call, as long as it makes progress: the semantics
    /// <see cref="Zstd.ZstdDecoder"/> mirrors. Each stream gets a new context: one that decoded a larger
    /// window keeps its larger buffer, and a buffer larger than the declared content lets that content
    /// overflow.
    /// </summary>
    /// <param name="written">The content written, however the stream ends.</param>
    public static StreamEnd ZstdDecompressStream(ReadOnlySpan<byte> stream, Span<byte> destination, ZSTD_DDict_s* dictionary, int windowLogMax, out int written)
    {
        ZSTD_DCtx_s* context = ZstandardNativeMethods.ZSTD_createDCtx();
        try
        {
            Check(ZstandardNativeMethods.ZSTD_DCtx_setParameter(context, ZSTD_dParameter.ZSTD_d_windowLogMax, windowLogMax));
            if (dictionary != null)
            {
                Check(ZstandardNativeMethods.ZSTD_DCtx_refDDict(context, dictionary));
            }

            return DecompressStream(context, stream, destination, out written);
        }
        finally
        {
            ZstandardNativeMethods.ZSTD_freeDCtx(context);
        }
    }

    /// <summary>
    /// Whether libzstd's streaming decoder refuses the window of the frame <paramref name="frame"/>
    /// starts with, when the frame arrives a byte at a time: never whole in one call, it is never
    /// decoded in one pass, which skips that check.
    /// </summary>
    public static bool ZstdRefusesWindow(ReadOnlySpan<byte> frame, ZSTD_DDict_s* dictionary, int windowLogMax)
    {
        // The largest frame header, after which libzstd has checked the window.
        const int MaxHeaderSize = 18;
        ZSTD_DCtx_s* context = ZstandardNativeMethods.ZSTD_createDCtx();
        try
        {
            Check(ZstandardNativeMethods.ZSTD_DCtx_setParameter(context, ZSTD_dParameter.ZSTD_d_windowLogMax, windowLogMax));
            if (dictionary != null)
            {
                Check(ZstandardNativeMethods.ZSTD_DCtx_refDDict(context, dictionary));
            }

            byte sink = 0;
            var output = new ZSTD_outBuffer_s { dst = &sink, size = 1, pos = 0 };
            fixed (byte* src = frame)
            {
                for (int i = 0; i < Math.Min(frame.Length, MaxHeaderSize); i++)
                {
                    var input = new ZSTD_inBuffer_s { src = src + i, size = 1, pos = 0 };
                    nuint result = ZstandardNativeMethods.ZSTD_decompressStream(context, &output, &input);
                    if (ZstandardNativeMethods.ZSTD_isError(result) != 0)
                    {
                        return ZstandardNativeMethods.ZSTD_getErrorCode(result) == ZSTD_ErrorCode.ZSTD_error_frameParameter_windowTooLarge;
                    }
                }
            }

            return false;
        }
        finally
        {
            ZstandardNativeMethods.ZSTD_freeDCtx(context);
        }
    }

    private static StreamEnd DecompressStream(ZSTD_DCtx_s* context, ReadOnlySpan<byte> stream, Span<byte> destination, out int written)
    {
        fixed (byte* src = stream)
        fixed (byte* dst = destination)
        {
            var input = new ZSTD_inBuffer_s { src = src, size = (nuint)stream.Length, pos = 0 };
            var output = new ZSTD_outBuffer_s { dst = dst, size = (nuint)destination.Length, pos = 0 };
            bool atBoundary;
            while (true)
            {
                nuint read = input.pos;
                nuint wrote = output.pos;
                nuint result = ZstandardNativeMethods.ZSTD_decompressStream(context, &output, &input);
                if (ZstandardNativeMethods.ZSTD_isError(result) != 0)
                {
                    written = (int)output.pos;
                    return StreamEnd.Refused;
                }

                atBoundary = result == 0;
                if ((input.pos == input.size && atBoundary) || (input.pos == read && output.pos == wrote))
                {
                    break;
                }
            }

            written = (int)output.pos;
            return atBoundary && input.pos == input.size ? StreamEnd.Decoded : StreamEnd.Incomplete;
        }
    }

    /// <summary>
    /// <c>ZSTD_compress2</c> at <paramref name="level"/>, with the dictionary prepared at that level when
    /// given; without the content size in the header unless <paramref name="contentSize"/>, and with
    /// a window of 2^<paramref name="windowLog"/> bytes when it is not 0.
    /// </summary>
    public static byte[] ZstdCompress(ReadOnlySpan<byte> content, int level, bool checksum, ZSTD_CDict_s* dictionary, bool contentSize = true, int windowLog = 0)
    {
        if (s_compression == null)
        {
            s_compression = ZstandardNativeMethods.ZSTD_createCCtx();
        }

        ZSTD_CCtx_s* context = s_compression;
        Check(ZstandardNativeMethods.ZSTD_CCtx_reset(context, ZSTD_ResetDirective.ZSTD_reset_session_and_parameters));
        Check(ZstandardNativeMethods.ZSTD_CCtx_setParameter(context, ZSTD_cParameter.ZSTD_c_compressionLevel, level));
        if (checksum)
        {
            Check(ZstandardNativeMethods.ZSTD_CCtx_setParameter(context, ZSTD_cParameter.ZSTD_c_checksumFlag, 1));
        }

        if (!contentSize)
        {
            Check(ZstandardNativeMethods.ZSTD_CCtx_setParameter(context, ZSTD_cParameter.ZSTD_c_contentSizeFlag, 0));
        }

        if (windowLog != 0)
        {
            Check(ZstandardNativeMethods.ZSTD_CCtx_setParameter(context, ZSTD_cParameter.ZSTD_c_windowLog, windowLog));
        }

        if (dictionary != null)
        {
            Check(ZstandardNativeMethods.ZSTD_CCtx_refCDict(context, dictionary));
        }

        byte[] frame = new byte[(int)ZstandardNativeMethods.ZSTD_compressBound((nuint)content.Length)];
        fixed (byte* src = content)
        fixed (byte* dst = frame)
        {
            nuint size = Check(ZstandardNativeMethods.ZSTD_compress2(context, dst, (nuint)frame.Length, src, (nuint)content.Length));
            return frame.AsSpan(0, (int)size).ToArray();
        }
    }

    /// <summary><c>ZSTD_createCDict</c>: the dictionary prepared for compression at <paramref name="level"/>.</summary>
    public static ZSTD_CDict_s* ZstdCompressionDictionary(byte[] dictionary, int level)
    {
        fixed (byte* bytes = dictionary)
        {
            ZSTD_CDict_s* prepared = ZstandardNativeMethods.ZSTD_createCDict(bytes, (nuint)dictionary.Length, level);
            return prepared != null ? prepared : throw new InvalidDataException("libzstd refuses the dictionary.");
        }
    }

    /// <summary><c>ZSTD_createDDict</c>: the dictionary prepared for decompression.</summary>
    public static ZSTD_DDict_s* ZstdDecompressionDictionary(byte[] dictionary)
    {
        fixed (byte* bytes = dictionary)
        {
            ZSTD_DDict_s* prepared = ZstandardNativeMethods.ZSTD_createDDict(bytes, (nuint)dictionary.Length);
            return prepared != null ? prepared : throw new InvalidDataException("libzstd refuses the dictionary.");
        }
    }

    /// <summary><c>ZDICT_trainFromBuffer</c>, or null when libzstd finds nothing to learn.</summary>
    public static byte[]? ZstdTrain(byte[] samples, int sampleSize, int capacity)
    {
        int count = samples.Length / sampleSize;
        nuint[] sizes = new nuint[count];
        Array.Fill(sizes, (nuint)sampleSize);
        byte[] dictionary = new byte[capacity];
        fixed (byte* dst = dictionary)
        fixed (byte* src = samples)
        fixed (nuint* size = sizes)
        {
            nuint trained = ZstandardNativeMethods.ZDICT_trainFromBuffer(dst, (nuint)capacity, src, size, (uint)count);
            return ZstandardNativeMethods.ZDICT_isError(trained) != 0 ? null : dictionary.AsSpan(0, (int)trained).ToArray();
        }
    }

    /// <summary><c>LZ4_decompress_safe</c>: the content size, or a negative value when lz4 refuses the block.</summary>
    public static int Lz4Decompress(ReadOnlySpan<byte> block, Span<byte> destination)
    {
        // Never null pointers, even for empty spans: lz4 dereferences them before checking the sizes.
        byte dummy = 0;
        fixed (byte* src = block)
        fixed (byte* dst = destination)
        {
            return LZ4NativeMethods.LZ4_decompress_safe(src == null ? &dummy : src, dst == null ? &dummy : dst, block.Length, destination.Length);
        }
    }

    /// <summary>
    /// What lz4 writes at <paramref name="level"/>, through the entry points the tests use: level 1 and
    /// below <c>LZ4_compress_fast_extState</c> at acceleration 1 - level, 2 to 12
    /// <c>LZ4_compress_HC_extStateHC</c>. Both reset their state, so it is reused.
    /// </summary>
    public static byte[] Lz4Compress(ReadOnlySpan<byte> content, int level)
    {
        bool fast = level <= 1;
        if (fast && s_lz4State == null)
        {
            s_lz4State = NativeMemory.AlignedAlloc((nuint)LZ4NativeMethods.LZ4_sizeofState(), 64);
        }
        else if (!fast && s_lz4StateHC == null)
        {
            s_lz4StateHC = NativeMemory.AlignedAlloc((nuint)LZ4NativeMethods.LZ4_sizeofStateHC(), 64);
        }

        byte[] block = new byte[Lz4.Lz4Compressor.GetMaxCompressedLength(content.Length) + 1];
        byte[] source = content.ToArray();
        int size;

        // Never a null source, even when empty: lz4's optimal parser reads through it.
        fixed (byte* src = &MemoryMarshal.GetArrayDataReference(source))
        fixed (byte* dst = &MemoryMarshal.GetArrayDataReference(block))
        {
            size = fast
                ? LZ4NativeMethods.LZ4_compress_fast_extState(s_lz4State, src, dst, source.Length, block.Length, level < 1 ? 1 - level : 1)
                : LZ4NativeMethods.LZ4_compress_HC_extStateHC(s_lz4StateHC, src, dst, source.Length, block.Length, level);
        }

        return block.AsSpan(0, size).ToArray();
    }

    private static nuint Check(nuint result) =>
        ZstandardNativeMethods.ZSTD_isError(result) != 0
            ? throw new InvalidOperationException("libzstd: " + Marshal.PtrToStringAnsi((nint)ZstandardNativeMethods.ZSTD_getErrorName(result)))
            : result;
}
