using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Brotli.Tests.Support;

/// <summary>
/// brotli 1.1.1, the oracle, as .NET ships it: <c>System.IO.Compression.Native</c> exports brotli's
/// whole C API, which <c>BrotliEncoder</c>, <c>BrotliDecoder</c> and <c>BrotliStream</c> call. The
/// tests call it directly, for the parameters and the error codes the managed API does not expose.
/// </summary>
internal static unsafe class NativeBrotli
{
    /// <summary><c>BROTLI_DECODER_RESULT_*</c>.</summary>
    public const int ResultError = 0;
    public const int ResultSuccess = 1;
    public const int ResultNeedsMoreInput = 2;
    public const int ResultNeedsMoreOutput = 3;

    /// <summary><c>BrotliEncoderParameter</c>.</summary>
    public const int ParamMode = 0;
    public const int ParamQuality = 1;
    public const int ParamLgwin = 2;
    public const int ParamLgblock = 3;
    public const int ParamDisableLiteralContextModeling = 4;
    public const int ParamSizeHint = 5;
    public const int ParamLargeWindow = 6;

    /// <summary><c>BrotliEncoderOperation</c>.</summary>
    public const int OperationProcess = 0;
    public const int OperationFlush = 1;
    public const int OperationFinish = 2;

    private static readonly nint Library = NativeLibrary.Load(Path.Combine(
        RuntimeEnvironment.GetRuntimeDirectory(),
        OperatingSystem.IsWindows() ? "System.IO.Compression.Native.dll"
        : OperatingSystem.IsMacOS() ? "libSystem.IO.Compression.Native.dylib"
        : "libSystem.IO.Compression.Native.so"));

    private static readonly delegate* unmanaged<uint> EncoderVersionFn = (delegate* unmanaged<uint>)Export("BrotliEncoderVersion");
    private static readonly delegate* unmanaged<uint> DecoderVersionFn = (delegate* unmanaged<uint>)Export("BrotliDecoderVersion");
    private static readonly delegate* unmanaged<int, int, int, nuint, byte*, nuint*, byte*, int> EncoderCompressFn =
        (delegate* unmanaged<int, int, int, nuint, byte*, nuint*, byte*, int>)Export("BrotliEncoderCompress");
    private static readonly delegate* unmanaged<nuint, nuint> EncoderMaxCompressedSizeFn =
        (delegate* unmanaged<nuint, nuint>)Export("BrotliEncoderMaxCompressedSize");
    private static readonly delegate* unmanaged<nint, nint, nint, nint> EncoderCreateFn =
        (delegate* unmanaged<nint, nint, nint, nint>)Export("BrotliEncoderCreateInstance");
    private static readonly delegate* unmanaged<nint, void> EncoderDestroyFn = (delegate* unmanaged<nint, void>)Export("BrotliEncoderDestroyInstance");
    private static readonly delegate* unmanaged<nint, int, uint, int> EncoderSetParameterFn =
        (delegate* unmanaged<nint, int, uint, int>)Export("BrotliEncoderSetParameter");
    private static readonly delegate* unmanaged<nint, int, nuint*, byte**, nuint*, byte**, nuint*, int> EncoderCompressStreamFn =
        (delegate* unmanaged<nint, int, nuint*, byte**, nuint*, byte**, nuint*, int>)Export("BrotliEncoderCompressStream");
    private static readonly delegate* unmanaged<nint, int> EncoderHasMoreOutputFn = (delegate* unmanaged<nint, int>)Export("BrotliEncoderHasMoreOutput");
    private static readonly delegate* unmanaged<nint, nint, nint, nint> DecoderCreateFn =
        (delegate* unmanaged<nint, nint, nint, nint>)Export("BrotliDecoderCreateInstance");
    private static readonly delegate* unmanaged<nint, void> DecoderDestroyFn = (delegate* unmanaged<nint, void>)Export("BrotliDecoderDestroyInstance");
    private static readonly delegate* unmanaged<nint, int, uint, int> DecoderSetParameterFn =
        (delegate* unmanaged<nint, int, uint, int>)Export("BrotliDecoderSetParameter");
    private static readonly delegate* unmanaged<nint, nuint*, byte**, nuint*, byte**, nuint*, int> DecoderDecompressStreamFn =
        (delegate* unmanaged<nint, nuint*, byte**, nuint*, byte**, nuint*, int>)Export("BrotliDecoderDecompressStream");
    private static readonly delegate* unmanaged<nint, int> DecoderGetErrorCodeFn = (delegate* unmanaged<nint, int>)Export("BrotliDecoderGetErrorCode");

    public static uint EncoderVersion => EncoderVersionFn();

    public static uint DecoderVersion => DecoderVersionFn();

    public static int MaxCompressedSize(int length) => (int)EncoderMaxCompressedSizeFn((nuint)length);

    /// <summary><c>BrotliEncoderCompress</c>: what <c>BrotliEncoder.TryCompress</c> calls.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, int quality, int window, int mode = 0)
    {
        byte[] output = new byte[Math.Max(MaxCompressedSize(data.Length), 16)];
        nuint size = (nuint)output.Length;
        fixed (byte* src = data)
        fixed (byte* dst = output)
        {
            if (EncoderCompressFn(quality, window, mode, (nuint)data.Length, src, &size, dst) == 0)
            {
                throw new InvalidOperationException("BrotliEncoderCompress failed");
            }
        }

        return output.AsSpan(0, (int)size).ToArray();
    }

    /// <summary>
    /// The streaming encoder, given the whole input at once and finished: the one-shot output of
    /// <c>BrotliEncoderCompress</c> but for quality 10, and the way to set the parameters it does not take.
    /// </summary>
    public static byte[] CompressStream(ReadOnlySpan<byte> data, params ReadOnlySpan<(int Parameter, uint Value)> parameters)
    {
        nint state = EncoderCreateFn(0, 0, 0);
        try
        {
            foreach ((int parameter, uint value) in parameters)
            {
                if (EncoderSetParameterFn(state, parameter, value) == 0)
                {
                    throw new InvalidOperationException("BrotliEncoderSetParameter refused " + parameter);
                }
            }

            var result = new MemoryStream();
            byte[] buffer = new byte[1 << 16];
            fixed (byte* src = data)
            fixed (byte* dst = buffer)
            {
                nuint availableIn = (nuint)data.Length;
                byte* nextIn = src;
                while (true)
                {
                    nuint availableOut = (nuint)buffer.Length;
                    byte* nextOut = dst;
                    if (EncoderCompressStreamFn(state, OperationFinish, &availableIn, &nextIn, &availableOut, &nextOut, null) == 0)
                    {
                        throw new InvalidOperationException("BrotliEncoderCompressStream failed");
                    }

                    result.Write(buffer, 0, buffer.Length - (int)availableOut);
                    if (availableIn == 0 && EncoderHasMoreOutputFn(state) == 0 && availableOut != 0)
                    {
                        break;
                    }
                }
            }

            return result.ToArray();
        }
        finally
        {
            EncoderDestroyFn(state);
        }
    }

    /// <summary>
    /// <c>BrotliDecoderDecompressStream</c> given the whole source and a destination of
    /// <paramref name="capacity"/> bytes: its result, its error code, and what it consumed and wrote.
    /// </summary>
    public static (int Result, int ErrorCode, int Consumed, byte[] Output) Decompress(ReadOnlySpan<byte> source, int capacity, bool largeWindow = false)
    {
        nint state = DecoderCreateFn(0, 0, 0);
        try
        {
            if (largeWindow)
            {
                DecoderSetParameterFn(state, 1, 1);
            }

            byte[] output = new byte[capacity];
            fixed (byte* src = source)
            fixed (byte* dst = output)
            {
                nuint availableIn = (nuint)source.Length;
                byte* nextIn = src;
                nuint availableOut = (nuint)capacity;
                byte* nextOut = dst;
                nuint total = 0;
                int result = DecoderDecompressStreamFn(state, &availableIn, &nextIn, &availableOut, capacity == 0 ? null : &nextOut, &total);
                return (result, DecoderGetErrorCodeFn(state), source.Length - (int)availableIn, output.AsSpan(0, (int)total).ToArray());
            }
        }
        finally
        {
            DecoderDestroyFn(state);
        }
    }

    private static nint Export(string name) => NativeLibrary.GetExport(Library, name);
}
