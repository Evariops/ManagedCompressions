using System;
using System.Buffers;
using Xunit;

namespace ManagedCompressions.Brotli.Tests.Support;

/// <summary>
/// The decoder's verdict on a stream, against brotli's on the same stream with the same room for its
/// output: accepted or refused alike, the same content, the same error.
/// </summary>
internal static class Verdict
{
    /// <summary>Decodes <paramref name="stream"/> with both decoders, <paramref name="capacity"/> bytes of room each, and compares.</summary>
    public static void Check(BrotliDecompressor decompressor, ReadOnlySpan<byte> stream, int capacity, string what)
    {
        (int result, int code, int nativeConsumed, byte[] nativeOutput) = NativeBrotli.Decompress(stream, capacity);
        byte[] output = new byte[capacity];
        OperationStatus status = decompressor.Decompress(stream, output, out int consumed, out int written);
        BrotliError error = decompressor.LastError;
        string context = $"{what}: managed {status} ({error}), native result {result} (error {code})";
        switch (status)
        {
            case OperationStatus.Done:
                Assert.True(result == NativeBrotli.ResultSuccess, context);
                Assert.Equal(nativeConsumed, consumed);
                Assert.Equal(nativeOutput.Length, written);
                Assert.True(nativeOutput.AsSpan().SequenceEqual(output.AsSpan(0, written)), context + ": the content differs");
                break;

            case OperationStatus.DestinationTooSmall:
                // Refused at the header of a meta-block that does not fit, which brotli decodes into its
                // ring buffer: it may then find it invalid or truncated before it runs out of room.
                Assert.True(result != NativeBrotli.ResultSuccess, context);
                break;

            case OperationStatus.NeedMoreData:
                Assert.True(result == NativeBrotli.ResultNeedsMoreInput, context);
                break;

            default:
                Assert.True(result == NativeBrotli.ResultError, context);
                Assert.True(Equivalent(error, code), context + ": another error");
                break;
        }

        if (result == NativeBrotli.ResultNeedsMoreOutput)
        {
            Assert.True(status == OperationStatus.DestinationTooSmall, context);
        }
    }

    /// <summary>Whether <paramref name="error"/> is the managed name of brotli's <paramref name="code"/>.</summary>
    public static bool Equivalent(BrotliError error, int code) => code switch
    {
        -1 => error == BrotliError.ExuberantNibble,
        -2 => error == BrotliError.Reserved,
        -3 => error == BrotliError.ExuberantMetaNibble,
        -4 => error == BrotliError.SimpleHuffmanAlphabet,
        -5 => error == BrotliError.SimpleHuffmanSame,
        -6 => error == BrotliError.CodeLengthSpace,
        -7 => error == BrotliError.HuffmanSpace,
        -8 => error == BrotliError.ContextMapRepeat,
        -9 or -10 => error == BrotliError.BlockLength,
        -11 => error == BrotliError.Transform,
        -12 => error == BrotliError.Dictionary,
        -13 => error == BrotliError.WindowBits,
        -14 or -15 => error == BrotliError.Padding,
        -16 => error == BrotliError.Distance,

        // A command that inserts past its meta-block's length is refused before its literals are
        // decoded; brotli decodes them, and may run out of a category's single block type first.
        -17 => error is BrotliError.BlockSwitch or BrotliError.BlockLength,
        _ => false,
    };
}
