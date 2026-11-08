using System;
using System.Buffers.Binary;
using ManagedCompressions.Zstd.Internal;

namespace ManagedCompressions.Zstd;

/// <summary>What <see cref="ZstdDecoder"/> takes from the one-shot decoder: its blocks, one at a time.</summary>
public sealed partial class ZstdDecompressor
{
    /// <summary>The dictionary the decompressor was created with, if any: its content, tables and identifier.</summary>
    internal DecoderDictionary? CreatedDictionary => _dictionary;

    /// <summary>
    /// The state a frame starts from, for a caller that then decodes its blocks one at a time with
    /// <see cref="DecodeStreamBlock"/>: the dictionary's tables and repeat offsets when it has them,
    /// and room for the literals of the frame's largest block.
    /// </summary>
    internal void BeginStreamFrame(DictionaryEntropy? entropy, int blockSizeMax)
    {
        EnsureLiterals(ref _literals, blockSizeMax);
        BeginFrame(entropy);
    }

    /// <summary>
    /// One compressed block, <paramref name="block"/> its content without its header, decoded into
    /// <paramref name="destination"/> from <paramref name="op"/>: its matches reach back into the
    /// destination, then on into <paramref name="history"/>, which precedes the destination's start.
    /// Throws <see cref="ZstdException"/>.
    /// </summary>
    /// <returns>The size of the block's content.</returns>
    internal int DecodeStreamBlock(ReadOnlySpan<byte> block, Span<byte> destination, int op, int blockSizeMax, ReadOnlySpan<byte> history) =>
        DecodeCompressedBlock(block, 0, block.Length, destination, op, blockSizeMax, history);

    /// <summary>
    /// libzstd's <c>ZSTD_findFrameCompressedSize</c>: the size of the zstd or skippable frame that
    /// starts <paramref name="source"/>, from its headers alone.
    /// </summary>
    /// <returns>The size, or -1 when the source ends inside the frame or a header is invalid.</returns>
    internal static long FindFrameSize(ReadOnlySpan<byte> source)
    {
        if (source.Length < 4)
        {
            return -1;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if ((magic & FrameFormat.SkippableMagicMask) == FrameFormat.SkippableMagic)
        {
            if (source.Length < FrameFormat.SkippableHeaderSize)
            {
                return -1;
            }

            long skippable = FrameFormat.SkippableHeaderSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4));
            return skippable <= source.Length ? skippable : -1;
        }

        if (magic != FrameFormat.Magic || FrameHeader.Parse(source, out FrameHeader header) != ZstdError.None)
        {
            return -1;
        }

        long ip = header.HeaderSize;
        while (true)
        {
            if (source.Length - ip < FrameFormat.BlockHeaderSize)
            {
                return -1;
            }

            int blockHeader = source[(int)ip] | (source[(int)ip + 1] << 8) | (source[(int)ip + 2] << 16);
            ip += FrameFormat.BlockHeaderSize;
            int blockType = (blockHeader >> 1) & 3;
            if (blockType == 3)
            {
                return -1;
            }

            long size = blockType == 1 ? 1 : blockHeader >> 3;
            if (source.Length - ip < size)
            {
                return -1;
            }

            ip += size;
            if ((blockHeader & 1) != 0)
            {
                break;
            }
        }

        if (header.HasChecksum)
        {
            if (source.Length - ip < FrameFormat.ChecksumSize)
            {
                return -1;
            }

            ip += FrameFormat.ChecksumSize;
        }

        return ip;
    }
}
