using System;
using ManagedCompressions.Internal;
using Xunit;

namespace ManagedCompressions.Zstd.Tests;

/// <summary>
/// The Checked build's guard, which the tests and the fuzzing rely on: the memory the library reads
/// and writes without bounds checks lies against an inaccessible page, so that the first byte past
/// it faults. A fault cannot be caught, so the placement is what is checked here.
/// </summary>
public sealed unsafe class GuardTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(4096)]
    [InlineData(70_000)]
    public void A_buffer_lies_against_its_guard_page(int length)
    {
        Assert.SkipUnless(TestBuild.Checked, "the guard is the Checked build's");
        PinnedBuffer<ulong> buffer = PinnedBuffer<ulong>.Allocate(length);
        nuint page = (nuint)Environment.SystemPageSize;
        bool atStart = string.Equals(Environment.GetEnvironmentVariable("MANAGEDCOMPRESSIONS_GUARD"), "start", StringComparison.OrdinalIgnoreCase);
        nuint edge = atStart ? (nuint)buffer.Pointer : (nuint)(buffer.Pointer + buffer.Length);
        Assert.Equal(0u, edge % page);
        Assert.Equal(length, buffer.Length);
        Assert.True(buffer.Span.IndexOfAnyExcept(0UL) < 0);
    }

    [Fact]
    public void An_uncleared_buffer_holds_a_pattern_a_read_before_a_write_shows()
    {
        Assert.SkipUnless(TestBuild.Checked, "the pattern is the Checked build's");
        PinnedBuffer<byte> buffer = PinnedBuffer<byte>.Allocate(1000, clear: false);
        Assert.True(buffer.Span.IndexOfAnyExcept((byte)0xCD) < 0);
    }
}
