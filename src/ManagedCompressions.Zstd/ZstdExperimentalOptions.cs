using System;

namespace ManagedCompressions.Zstd;

/// <summary>
/// Experimental compression features, off unless asked for by name: each one trades libzstd's exact
/// bytes for speed or ratio on some data. The frames stay standard Zstandard (RFC 8878), which libzstd,
/// this library and any conforming decoder read back exactly; what changes is which matches the
/// compressor keeps, so the frames are no longer those libzstd writes at the same level, and they may
/// differ from one version of this library to the next.
/// </summary>
/// <remarks>
/// A feature applies where its remarks say, and leaves every other level and mode as libzstd's.
/// Experimental features may change or go away: pin the version of the library for frames that must be
/// reproduced bit for bit.
/// </remarks>
[Flags]
public enum ZstdExperimentalOptions
{
    /// <summary>libzstd's bytes, exactly.</summary>
    None = 0,

    /// <summary>
    /// For data of few distinct bytes and no structure, such as hexadecimal hashes or identifiers: the
    /// double-fast search (levels 3 and 4, and the levels that select it for small sources) keeps a
    /// match only when it costs fewer bits than the literals it replaces, its offset's extra bits and
    /// some eight bits of codes against its length times the entropy of the block's bytes. The bytes of
    /// a match it refuses stay literals, and the search starts again past them as past a match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// libzstd keeps every match it verifies. On hexadecimal digits, four bits of entropy a byte, the
    /// matches its levels 3 to 9 find are mostly five or six bytes matched by chance at random
    /// distances, which cost more than their literals: those levels then compress less than level 1,
    /// and slower. With this feature, 16 MiB of random hexadecimal compress at level 3 to a ratio of
    /// 1.985 instead of 1.922, 5 % slower, and at level 4 to 1.986 instead of 1.815, as fast, the frames
    /// of level 4 decoding twice as fast, having half the sequences (AMD Ryzen 9 7950X).
    /// </para>
    /// <para>
    /// It is no general-purpose setting: on text and mixed data a refused match hides better ones, and
    /// the Silesia corpus compresses 0.3 % less and 7 % slower with it, text-like data up to 4 % less.
    /// Blocks under 4 KiB, and frames with a dictionary, are compressed as without the feature.
    /// </para>
    /// </remarks>
    EntropyAwareMatches = 1 << 0,
}
