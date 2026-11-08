using System;
using System.Buffers;
using System.Runtime.InteropServices;
using NativeCompressions.Interop;

namespace ManagedCompressions.Zstd.Tests.Support;

/// <summary>
/// libzstd's <c>ZSTD_compressStream2</c> behind <see cref="ZstdEncoder"/>'s methods, as System.IO.Compression's
/// <c>ZstandardEncoder</c> puts it: a call that is not final continues (<c>ZSTD_e_continue</c>), and does
/// nothing when its source is empty; a final call ends the frame (<c>ZSTD_e_end</c>); a flush flushes
/// (<c>ZSTD_e_flush</c>). A call that takes the frame past its pledged size is refused, and so is every
/// call after it, until <see cref="Reset"/>.
/// </summary>
internal sealed unsafe class NativeZstdEncoder : Streaming.IStreamEncoder, IDisposable
{
    private ZSTD_CCtx_s* _context;
    private bool _failed;

    public NativeZstdEncoder(NativeZstdOptions options)
    {
        _context = ZstandardNativeMethods.ZSTD_createCCtx();
        int level = options.Dictionary?.Level ?? options.Quality;
        Set(ZSTD_cParameter.ZSTD_c_compressionLevel, level);
        if (options.AppendChecksum)
        {
            Set(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        }

        if (options.EnableLongDistanceMatching || options.DisableLongDistanceMatching)
        {
            Set(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, options.EnableLongDistanceMatching ? 1 : 2);
        }

        if (options.WindowLog > 0)
        {
            Set(ZSTD_cParameter.ZSTD_c_windowLog, options.WindowLog);
        }

        if (!options.ContentSizeFlag)
        {
            Set(ZSTD_cParameter.ZSTD_c_contentSizeFlag, 0);
        }

        if (!options.DictionaryIdFlag)
        {
            Set(ZSTD_cParameter.ZSTD_c_dictIDFlag, 0);
        }

        if (options.Dictionary is { } dictionary)
        {
            NativeZstd.Check(ZstandardNativeMethods.ZSTD_CCtx_refCDict(_context, dictionary.ForCompression(level)));
        }
    }

    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
    {
        if (source.IsEmpty && !isFinalBlock)
        {
            bytesConsumed = 0;
            bytesWritten = 0;
            return _failed ? OperationStatus.InvalidData : OperationStatus.Done;
        }

        return Run(source, destination, isFinalBlock ? ZSTD_EndDirective.ZSTD_e_end : ZSTD_EndDirective.ZSTD_e_continue, out bytesConsumed, out bytesWritten);
    }

    public OperationStatus Flush(Span<byte> destination, out int bytesWritten) =>
        Run(default, destination, ZSTD_EndDirective.ZSTD_e_flush, out _, out bytesWritten);

    /// <summary><c>ZSTD_CCtx_setPledgedSrcSize</c>, which libzstd refuses once a frame has begun.</summary>
    public void SetSourceLength(long length)
    {
        nuint result = ZstandardNativeMethods.ZSTD_CCtx_setPledgedSrcSize(_context, (ulong)length);
        if (ZstandardNativeMethods.ZSTD_isError(result) != 0)
        {
            throw new InvalidOperationException("libzstd: " + Marshal.PtrToStringAnsi((nint)ZstandardNativeMethods.ZSTD_getErrorName(result)));
        }
    }

    /// <summary><c>ZSTD_CCtx_reset</c> with <c>ZSTD_reset_session_only</c>.</summary>
    public void Reset()
    {
        NativeZstd.Check(ZstandardNativeMethods.ZSTD_CCtx_reset(_context, ZSTD_ResetDirective.ZSTD_reset_session_only));
        _failed = false;
    }

    public void Dispose()
    {
        if (_context != null)
        {
            ZstandardNativeMethods.ZSTD_freeCCtx(_context);
            _context = null;
        }
    }

    private OperationStatus Run(ReadOnlySpan<byte> source, Span<byte> destination, ZSTD_EndDirective directive, out int bytesConsumed, out int bytesWritten)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        if (_failed)
        {
            return OperationStatus.InvalidData;
        }

        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            var input = new ZSTD_inBuffer_s { src = src, size = (nuint)source.Length, pos = 0 };
            var output = new ZSTD_outBuffer_s { dst = dst, size = (nuint)destination.Length, pos = 0 };
            nuint remaining = ZstandardNativeMethods.ZSTD_compressStream2(_context, &output, &input, directive);
            if (ZstandardNativeMethods.ZSTD_isError(remaining) != 0)
            {
                if (ZstandardNativeMethods.ZSTD_getErrorCode(remaining) != ZSTD_ErrorCode.ZSTD_error_srcSize_wrong)
                {
                    NativeZstd.Check(remaining);
                }

                _failed = true;
                return OperationStatus.InvalidData;
            }

            bytesConsumed = (int)input.pos;
            bytesWritten = (int)output.pos;
            return input.pos < input.size || remaining != 0 ? OperationStatus.DestinationTooSmall : OperationStatus.Done;
        }
    }

    private void Set(ZSTD_cParameter parameter, int value) =>
        NativeZstd.Check(ZstandardNativeMethods.ZSTD_CCtx_setParameter(_context, parameter, value));
}
