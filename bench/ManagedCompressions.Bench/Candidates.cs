using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeCompressions.Interop;
using ManagedCompressions.Brotli;
using ManagedCompressions.Lz4;
using ManagedCompressions.Zstd;

namespace ManagedCompressions.Bench;

/// <summary>A codec over one record: the bytes it wrote, or a negative number when it failed.</summary>
internal delegate int SpanCodec(ReadOnlySpan<byte> source, Span<byte> destination);

/// <summary>
/// One implementation at one level. <see cref="Compress"/> is null where the implementation has no
/// such level. <see cref="Native"/> marks the reference, against which every speedup is taken.
/// </summary>
internal sealed record Candidate(string Name, string Label, bool Native, SpanCodec? Compress, SpanCodec Decompress);

/// <summary>The native and the managed implementations of each codec, at a level, sharing nothing but the data.</summary>
internal static unsafe class Candidates
{
    /// <summary>
    /// The suffix of an A/A arm's name (<c>--aa 1</c>): a second, separate instance of the managed
    /// codec, timed in the same rounds. Its speedup over the managed candidate should be 1 within the
    /// noise; a run where it is not measured noise, whatever its other figures say.
    /// </summary>
    public const string AaSuffix = "-aa";

    /// <summary>
    /// <c>--checksum 1</c>: both Zstandard implementations write the content checksum (XXH64), which
    /// their decoders then verify; without it neither does, libzstd's default.
    /// </summary>
    public static bool Checksum { get; set; }

    private static string Runtime => RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT";

    public static int Bound(string codec, int length) => codec switch
    {
        "zstd" => Math.Max(ZstdCompressor.GetMaxCompressedLength(length), (int)ZstandardNativeMethods.ZSTD_compressBound((nuint)length)),
        "brotli" => System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(length),
        _ => Math.Max(Lz4Block.MaxCompressedLength(length), LZ4NativeMethods.LZ4_compressBound(length)),
    };

    /// <summary>
    /// The native reference first, then the managed codec, then with <paramref name="aa"/> a second
    /// managed instance, the A/A arm (<see cref="AaSuffix"/>).
    /// </summary>
    public static List<Candidate> For(string codec, DataSet set, int level, List<Action> release, bool aa = false)
    {
        List<Candidate> candidates = codec switch
        {
            "zstd" => Zstd(set, level, release),
            "lz4" => Lz4(level, release),
            "brotli" => Brotli(level),
            _ => throw new ArgumentException("unknown codec " + codec + " (zstd, lz4, brotli)", nameof(codec)),
        };
        if (aa)
        {
            // A fresh managed instance: its tables in other pages, as the A/B arms of another build are.
            Candidate twin = (codec switch { "zstd" => Zstd(set, level, release), "brotli" => Brotli(level), _ => Lz4(level, release) }).Single(c => !c.Native);
            candidates.Add(twin with { Name = twin.Name + AaSuffix, Label = twin.Label + " (A/A)" });
        }

        return candidates;
    }

    /// <summary>
    /// libzstd 1.5.7 as NativeCompressions ships it, on its fastest path: one compression and one
    /// decompression context reused for every record, the dictionary prepared once at the level
    /// (<c>ZSTD_createCDict</c>, <c>ZSTD_CCtx_refCDict</c>), as ManagedCompressions.Zstd prepares it.
    /// </summary>
    private static List<Candidate> Zstd(DataSet set, int level, List<Action> release)
    {
        ZSTD_CCtx_s* cctx = ZstandardNativeMethods.ZSTD_createCCtx();
        ZSTD_DCtx_s* dctx = ZstandardNativeMethods.ZSTD_createDCtx();
        ZSTD_CDict_s* cdict = null;
        ZSTD_DDict_s* ddict = null;
        byte[]? dictionary = set.Dictionary;
        if (dictionary is not null)
        {
            fixed (byte* d = dictionary)
            {
                cdict = ZstandardNativeMethods.ZSTD_createCDict(d, (nuint)dictionary.Length, level);
                ddict = ZstandardNativeMethods.ZSTD_createDDict(d, (nuint)dictionary.Length);
            }

            Check(ZstandardNativeMethods.ZSTD_CCtx_refCDict(cctx, cdict));
        }
        else
        {
            Check(ZstandardNativeMethods.ZSTD_CCtx_setParameter(cctx, ZSTD_cParameter.ZSTD_c_compressionLevel, level));
        }

        Check(ZstandardNativeMethods.ZSTD_CCtx_setParameter(cctx, ZSTD_cParameter.ZSTD_c_checksumFlag, Checksum ? 1 : 0));
        release.Add(() =>
        {
            ZstandardNativeMethods.ZSTD_freeCCtx(cctx);
            ZstandardNativeMethods.ZSTD_freeDCtx(dctx);
            if (cdict is not null)
            {
                ZstandardNativeMethods.ZSTD_freeCDict(cdict);
                ZstandardNativeMethods.ZSTD_freeDDict(ddict);
            }
        });

        var compressor = dictionary is null ? new ZstdCompressor(level) : new ZstdCompressor(level, dictionary);
        compressor.AppendChecksum = Checksum;
        var decompressor = dictionary is null ? new ZstdDecompressor() : new ZstdDecompressor(dictionary);
        return
        [
            new("native", $"zstd {Ansi(ZstandardNativeMethods.ZSTD_versionString())} (NativeCompressions)", true,
                (s, d) =>
                {
                    fixed (byte* src = s)
                    fixed (byte* dst = d)
                    {
                        // With --checksum 1 the flag is a parameter of the context, which
                        // ZSTD_compress2 applies and ZSTD_compressCCtx would reset; without it, the
                        // call the published results were measured with.
                        nuint r = cdict is null && !Checksum
                            ? ZstandardNativeMethods.ZSTD_compressCCtx(cctx, dst, (nuint)d.Length, src, (nuint)s.Length, level)
                            : ZstandardNativeMethods.ZSTD_compress2(cctx, dst, (nuint)d.Length, src, (nuint)s.Length);
                        return ZstandardNativeMethods.ZSTD_isError(r) != 0 ? -1 : (int)r;
                    }
                },
                (s, d) =>
                {
                    fixed (byte* src = s)
                    fixed (byte* dst = d)
                    {
                        nuint r = ddict is null
                            ? ZstandardNativeMethods.ZSTD_decompressDCtx(dctx, dst, (nuint)d.Length, src, (nuint)s.Length)
                            : ZstandardNativeMethods.ZSTD_decompress_usingDDict(dctx, dst, (nuint)d.Length, src, (nuint)s.Length, ddict);
                        return ZstandardNativeMethods.ZSTD_isError(r) != 0 ? -1 : (int)r;
                    }
                }),
            new("managed", $"ManagedCompressions.Zstd ({Runtime})", false,
                (s, d) => compressor.Compress(s, d, out _, out int w) == System.Buffers.OperationStatus.Done ? w : -1,
                (s, d) => decompressor.Decompress(s, d, out _, out int w) == System.Buffers.OperationStatus.Done ? w : -1),
        ];
    }

    /// <summary>
    /// lz4 1.10.0 as NativeCompressions ships it, the block format, its state allocated once: level 1
    /// is <c>LZ4_compress_fast</c> at acceleration 1 (<c>LZ4_compress_default</c>), a level below 1
    /// <c>LZ4_compress_fast</c> at acceleration 1 - level (lz4frame's convention), levels 2 to 12
    /// <c>LZ4_compress_HC</c>.
    /// </summary>
    private static List<Candidate> Lz4(int level, List<Action> release)
    {
        bool fast = level <= 1;
        int acceleration = level < 1 ? 1 - level : 1;
        var compressor = new Lz4Compressor(level);
        void* state = NativeMemory.AlignedAlloc((nuint)(fast ? LZ4NativeMethods.LZ4_sizeofState() : LZ4NativeMethods.LZ4_sizeofStateHC()), 64);
        release.Add(() => NativeMemory.AlignedFree(state));
        return
        [
            new("native", $"lz4 {Ansi(LZ4NativeMethods.LZ4_versionString())} (NativeCompressions)", true,
                (s, d) =>
                {
                    fixed (byte* src = s)
                    fixed (byte* dst = d)
                    {
                        int r = fast
                            ? LZ4NativeMethods.LZ4_compress_fast_extState(state, src, dst, s.Length, d.Length, acceleration)
                            : LZ4NativeMethods.LZ4_compress_HC_extStateHC(state, src, dst, s.Length, d.Length, level);
                        return r <= 0 && s.Length > 0 ? -1 : r;
                    }
                },
                (s, d) =>
                {
                    fixed (byte* src = s)
                    fixed (byte* dst = d)
                    {
                        return LZ4NativeMethods.LZ4_decompress_safe(src, dst, s.Length, d.Length);
                    }
                }),
            new("managed", $"ManagedCompressions.Lz4 ({Runtime})", false,
                (s, d) => compressor.TryCompress(s, d, out int w) ? w : -1,
                (s, d) =>
                {
                    Lz4Block.Decompress(s, d);
                    return d.Length;
                }),
        ];
    }

    /// <summary>
    /// brotli 1.1.1 as .NET ships it in System.IO.Compression, through the calls applications make:
    /// <c>BrotliEncoder.TryCompress</c> (<c>BrotliEncoderCompress</c>) at the quality and .NET's default
    /// window, 22, and <c>BrotliDecoder.TryDecompress</c> (<c>BrotliDecoderDecompress</c>). Each creates
    /// and frees brotli's state, as these calls always do.
    /// </summary>
    private static List<Candidate> Brotli(int quality)
    {
        var decompressor = new BrotliDecompressor();
        return
        [
            new("native", "brotli 1.1.1 (System.IO.Compression)", true,
                (s, d) => System.IO.Compression.BrotliEncoder.TryCompress(s, d, out int w, quality, 22) ? w : -1,
                (s, d) => System.IO.Compression.BrotliDecoder.TryDecompress(s, d, out int w) ? w : -1),
            new("managed", $"ManagedCompressions.Brotli ({Runtime})", false,
                null,
                (s, d) => decompressor.Decompress(s, d, out _, out int w) == System.Buffers.OperationStatus.Done ? w : -1),
        ];
    }


    private static string Ansi(byte* text) => Marshal.PtrToStringAnsi((nint)text) ?? "?";

    private static void Check(nuint result)
    {
        if (ZstandardNativeMethods.ZSTD_isError(result) != 0)
        {
            throw new InvalidOperationException("libzstd failed: " + Ansi(ZstandardNativeMethods.ZSTD_getErrorName(result)));
        }
    }
}
