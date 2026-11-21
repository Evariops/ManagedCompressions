namespace ManagedCompressions.Zstd;

/// <summary>The frames <see cref="ZstdCompressor.CompressAsync"/> writes from its jobs.</summary>
public enum ZstdJobFrames
{
    /// <summary>
    /// One frame, as libzstd writes with workers (<c>ZSTD_c_nbWorkers</c>): each job starts from the end
    /// of the one before, which keeps the ratio of a frame compressed whole. Its blocks depend on one
    /// another: it decodes on one thread.
    /// </summary>
    Single,

    /// <summary>
    /// A frame per job, each compressed from its section alone, with its content size and, when asked
    /// for, its checksum: the frames <see cref="ZstdCompressor.Compress"/> writes of each section, one
    /// after the other, which any Zstandard decoder reads as one content. A little larger than a single
    /// frame, as each section starts without the window of the ones before (0.1% to 0.3% on silesia at
    /// levels 1 to 9); decoded on as many threads as there are frames by
    /// <see cref="ZstdDecompressor.DecompressAsync"/>, some 7x faster on 8 threads.
    /// </summary>
    Independent,
}
