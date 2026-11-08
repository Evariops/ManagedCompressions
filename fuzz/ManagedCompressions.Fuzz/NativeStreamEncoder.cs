using System;
using System.Buffers;
using System.Runtime.InteropServices;
using NativeCompressions.Interop;

namespace ManagedCompressions.Fuzz;

/// <summary>
/// libzstd's <c>ZSTD_compressStream2</c> behind <see cref="Zstd.ZstdEncoder"/>'s methods, as System.IO.Compression's
/// <c>ZstandardEncoder</c> puts it: a call that is not final continues, and does nothing when its source
/// is empty; a final call ends the frame; a flush flushes. A call that takes the frame past its pledged
/// size is refused, and so is every call after it, until <see cref="Reset"/>. One context, kept.
/// </summary>
internal sealed unsafe class NativeStreamEncoder
{
    private readonly ZSTD_CCtx_s* _context = ZstandardNativeMethods.ZSTD_createCCtx();
    private bool _failed;

    /// <param name="level">The level, or the dictionary's when one is given.</param>
    /// <param name="checksum"><c>ZSTD_c_checksumFlag</c>.</param>
    /// <param name="windowLog"><c>ZSTD_c_windowLog</c>.</param>
    /// <param name="dictionary">A dictionary prepared at <paramref name="level"/>, or null.</param>
    public NativeStreamEncoder(int level, bool checksum, int windowLog, ZSTD_CDict_s* dictionary)
    {
        Set(ZSTD_cParameter.ZSTD_c_compressionLevel, level);
        Set(ZSTD_cParameter.ZSTD_c_checksumFlag, checksum ? 1 : 0);
        Set(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
        if (dictionary != null)
        {
            Check(ZstandardNativeMethods.ZSTD_CCtx_refCDict(_context, dictionary));
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

    /// <summary><c>ZSTD_CCtx_setPledgedSrcSize</c>.</summary>
    public void SetSourceLength(long length) =>
        Check(ZstandardNativeMethods.ZSTD_CCtx_setPledgedSrcSize(_context, (ulong)length));

    /// <summary><c>ZSTD_CCtx_reset</c> with <c>ZSTD_reset_session_only</c>.</summary>
    public void Reset()
    {
        Check(ZstandardNativeMethods.ZSTD_CCtx_reset(_context, ZSTD_ResetDirective.ZSTD_reset_session_only));
        _failed = false;
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
                    Check(remaining);
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
        Check(ZstandardNativeMethods.ZSTD_CCtx_setParameter(_context, parameter, value));

    private static void Check(nuint result)
    {
        if (ZstandardNativeMethods.ZSTD_isError(result) != 0)
        {
            throw new InvalidOperationException("libzstd: " + Marshal.PtrToStringAnsi((nint)ZstandardNativeMethods.ZSTD_getErrorName(result)));
        }
    }
}
