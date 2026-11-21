using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ManagedCompressions.Zstd.Internal;

/// <summary>A frame found by <see cref="FrameScan"/>: where it lies in the source, and what its header declares.</summary>
/// <param name="Offset">Where the frame starts in the source.</param>
/// <param name="Length">The frame's size, checksum included.</param>
/// <param name="ContentSize">The content size the header declares; -1 when it declares none.</param>
/// <param name="Skippable">A skippable frame, which has no content.</param>
internal readonly record struct FrameSpan(int Offset, int Length, long ContentSize, bool Skippable);

/// <summary>
/// The frames of a source, found without decoding them: a zstd frame's header, then its blocks' 3-byte
/// headers, which give each block's size in the source; a skippable frame's declared size. This is
/// what lets frames be decoded on several threads at once: each one's place in the source, and in the
/// destination when every frame declares its content size, is known before any is decoded.
/// </summary>
internal static class FrameScan
{
    /// <summary>
    /// The frames of <paramref name="source"/>, from its start, up to the first that does not hold
    /// together (an unknown magic number, an invalid header, a reserved block type, a block larger than
    /// the frame allows, a frame that the source ends inside of), which is left out: decoding it then
    /// reports its error, exactly as the decoder's frame-by-frame loop would.
    /// </summary>
    /// <param name="source">The frames, one after the other.</param>
    /// <param name="complete">Whether every byte of the source belongs to a frame found.</param>
    public static List<FrameSpan> Scan(ReadOnlySpan<byte> source, out bool complete)
    {
        var frames = new List<FrameSpan>();
        int at = 0;
        while (at < source.Length)
        {
            ReadOnlySpan<byte> rest = source.Slice(at);
            if (rest.Length < 4)
            {
                break;
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            if ((magic & FrameFormat.SkippableMagicMask) == FrameFormat.SkippableMagic)
            {
                if (rest.Length < FrameFormat.SkippableHeaderSize)
                {
                    break;
                }

                ulong size = FrameFormat.SkippableHeaderSize + (ulong)BinaryPrimitives.ReadUInt32LittleEndian(rest.Slice(4));
                if (size > (ulong)rest.Length)
                {
                    break;
                }

                frames.Add(new FrameSpan(at, (int)size, 0, Skippable: true));
                at += (int)size;
                continue;
            }

            if (magic != FrameFormat.Magic || FrameHeader.Parse(rest, out FrameHeader header) != ZstdError.None)
            {
                break;
            }

            int length = Blocks(rest, header);
            if (length < 0)
            {
                break;
            }

            long contentSize = header.HasContentSize && header.ContentSize <= int.MaxValue ? (long)header.ContentSize : -1;
            frames.Add(new FrameSpan(at, length, contentSize, Skippable: false));
            at += length;
        }

        complete = at == source.Length;
        return frames;
    }

    /// <summary>The frame's size, from its blocks' headers and its checksum; -1 when they do not hold together.</summary>
    private static int Blocks(ReadOnlySpan<byte> frame, in FrameHeader header)
    {
        int at = header.HeaderSize;
        while (true)
        {
            if (frame.Length - at < FrameFormat.BlockHeaderSize)
            {
                return -1;
            }

            int blockHeader = frame[at] | (frame[at + 1] << 8) | (frame[at + 2] << 16);
            int type = (blockHeader >> 1) & 3;
            int size = blockHeader >> 3;
            at += FrameFormat.BlockHeaderSize;
            if (type == 3 || size > header.BlockSizeMax)
            {
                return -1;
            }

            // A raw or compressed block holds its size in the source; an RLE block, one byte.
            int stored = type == 1 ? 1 : size;
            if (stored > frame.Length - at)
            {
                return -1;
            }

            at += stored;
            if ((blockHeader & 1) != 0)
            {
                break;
            }
        }

        if (header.HasChecksum)
        {
            if (frame.Length - at < FrameFormat.ChecksumSize)
            {
                return -1;
            }

            at += FrameFormat.ChecksumSize;
        }

        return at;
    }
}
