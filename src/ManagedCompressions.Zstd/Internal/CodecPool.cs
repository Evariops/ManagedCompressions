using System;
using System.Collections.Generic;
using System.Threading;

namespace ManagedCompressions.Zstd.Internal;

/// <summary>
/// The encoders and decoders of the <see cref="ZstdStream"/>s that create their own, kept for the next
/// streams of the same settings: an encoder's tables and buffers take some megabytes, pinned, which a
/// stream per request or per file would otherwise allocate, clear and leave to the garbage collector
/// each time. A codec comes back reset (a reused one writes the frames a new one writes, and reads
/// what it reads); one that holds more than <see cref="MaxRetainedBytes"/> is left to the collector.
/// </summary>
/// <remarks>
/// Each setting keeps up to <see cref="PerSetting"/> codecs. A codec not taken again between two gen 2
/// collections is released at the second: an application that stops streaming gets its memory back.
/// </remarks>
internal static class CodecPool
{
    /// <summary>The most a codec may hold to be kept: the level 3 encoder of a long stream holds some 4 MiB.</summary>
    public const long MaxRetainedBytes = 16L << 20;

    private static readonly int PerSetting = Math.Clamp(Environment.ProcessorCount, 2, 16);
    private static readonly Lock Gate = new();
    private static readonly Dictionary<EncoderKey, List<Entry<ZstdEncoder>>> Encoders = [];
    private static readonly Dictionary<DecoderKey, List<Entry<ZstdDecoder>>> Decoders = [];

    /// <summary>The gen 2 collections seen, counted by the trimming callback.</summary>
    private static int s_epoch;
    private static bool s_trimming;

    /// <summary>
    /// The settings of an encoder: what <see cref="ZstdCompressionOptions"/> holds, a prepared dictionary
    /// by reference; none given as bytes, which each encoder would prepare again.
    /// </summary>
    public readonly record struct EncoderKey(
        int Level, int WindowLog, bool AppendChecksum, bool? LongDistanceMatching, bool WriteContentSize, bool WriteDictionaryId,
        ZstdExperimentalOptions Experimental, ZstdDictionary? Dictionary)
    {
        /// <summary>An encoder at <paramref name="level"/>, every other option at its default.</summary>
        public static EncoderKey Of(int level) =>
            new(level == 0 ? ZstdCompressor.DefaultLevel : level, 0, false, null, true, true, ZstdExperimentalOptions.None, null);

        /// <summary>The options' settings; null with a dictionary given as bytes, whose encoders are not kept.</summary>
        public static EncoderKey? Of(ZstdCompressionOptions options) => options.Dictionary.IsEmpty
            ? new(options.PreparedDictionary?.Level ?? (options.Level == 0 ? ZstdCompressor.DefaultLevel : options.Level), options.WindowLog,
                options.AppendChecksum, options.LongDistanceMatching, options.WriteContentSize, options.WriteDictionaryId, options.Experimental,
                options.PreparedDictionary)
            : null;
    }

    /// <summary>The settings of a decoder: its largest window, a prepared dictionary by reference.</summary>
    public readonly record struct DecoderKey(int MaxWindowLog, ZstdDictionary? Dictionary)
    {
        /// <summary>The options' settings; null with a dictionary given as bytes, whose decoders are not kept.</summary>
        public static DecoderKey? Of(ZstdDecompressionOptions options) =>
            options.Dictionary.IsEmpty ? new(options.MaxWindowLog, options.PreparedDictionary) : null;
    }

    private readonly record struct Entry<T>(T Codec, int Epoch);

    /// <summary>An encoder of these settings: a kept one, or a new one, of <paramref name="options"/> when given.</summary>
    public static ZstdEncoder RentEncoder(EncoderKey key, ZstdCompressionOptions? options)
    {
        lock (Gate)
        {
            if (Encoders.TryGetValue(key, out List<Entry<ZstdEncoder>>? kept) && kept.Count > 0)
            {
                ZstdEncoder encoder = kept[^1].Codec;
                kept.RemoveAt(kept.Count - 1);
                return encoder;
            }
        }

        return options is null ? new ZstdEncoder(key.Level) : new ZstdEncoder(options);
    }

    /// <summary>An encoder a stream is done with, reset and kept unless it holds too much or enough are kept.</summary>
    public static void Return(EncoderKey key, ZstdEncoder encoder)
    {
        if (encoder.RetainedBytes > MaxRetainedBytes)
        {
            return;
        }

        encoder.Reset();
        lock (Gate)
        {
            if (!Encoders.TryGetValue(key, out List<Entry<ZstdEncoder>>? kept))
            {
                Encoders[key] = kept = [];
            }

            Keep(kept, encoder);
        }
    }

    /// <summary>A decoder of these settings: a kept one, or a new one.</summary>
    public static ZstdDecoder RentDecoder(DecoderKey key)
    {
        lock (Gate)
        {
            if (Decoders.TryGetValue(key, out List<Entry<ZstdDecoder>>? kept) && kept.Count > 0)
            {
                ZstdDecoder decoder = kept[^1].Codec;
                kept.RemoveAt(kept.Count - 1);
                return decoder;
            }
        }

        return new ZstdDecoder(new ZstdDecompressionOptions { MaxWindowLog = key.MaxWindowLog, PreparedDictionary = key.Dictionary });
    }

    /// <summary>A decoder a stream is done with, reset and kept unless it holds too much or enough are kept.</summary>
    public static void Return(DecoderKey key, ZstdDecoder decoder)
    {
        if (decoder.RetainedBytes > MaxRetainedBytes)
        {
            return;
        }

        decoder.Reset();
        lock (Gate)
        {
            if (!Decoders.TryGetValue(key, out List<Entry<ZstdDecoder>>? kept))
            {
                Decoders[key] = kept = [];
            }

            Keep(kept, decoder);
        }
    }

    /// <summary>Called under the lock.</summary>
    private static void Keep<T>(List<Entry<T>> kept, T codec)
    {
        if (kept.Count < PerSetting)
        {
            kept.Add(new Entry<T>(codec, s_epoch));
        }

        if (!s_trimming)
        {
            s_trimming = true;
            _ = new Gen2Callback();
        }
    }

    /// <summary>
    /// At a gen 2 collection: the codecs kept since before the last one, and not taken since, released.
    /// They are at the bottom of their lists, which are taken from the top.
    /// </summary>
    private static void Trim()
    {
        lock (Gate)
        {
            foreach (List<Entry<ZstdEncoder>> kept in Encoders.Values)
            {
                kept.RemoveAll(entry => entry.Epoch < s_epoch);
            }

            foreach (List<Entry<ZstdDecoder>> kept in Decoders.Values)
            {
                kept.RemoveAll(entry => entry.Epoch < s_epoch);
            }

            s_epoch++;
        }
    }

    /// <summary>
    /// The runtime's Gen2GcCallback pattern: an object that resurrects itself from its finalizer, which
    /// then runs at every collection of the generation it lives in, gen 2 once it gets there.
    /// </summary>
    private sealed class Gen2Callback
    {
        ~Gen2Callback()
        {
            if (Environment.HasShutdownStarted)
            {
                return;
            }

            Trim();
            GC.ReRegisterForFinalize(this);
        }
    }
}
