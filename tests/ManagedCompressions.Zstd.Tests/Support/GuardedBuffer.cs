using System;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Zstd.Tests.Support;

/// <summary>
/// Memory with an inaccessible page on each side: a span placed against one of them faults on the
/// first byte read or written past its end (or before its start), where a managed buffer would let an
/// out-of-bounds access through silently. The fuzzing hands the decoder its source and destination
/// this way. macOS, Linux and Windows; elsewhere it degrades to an ordinary array.
/// </summary>
internal sealed unsafe class GuardedBuffer : IDisposable
{
    private const int ProtNone = 0;
    private const int ProtRead = 1;
    private const int ProtWrite = 2;

    private const uint MemCommitReserve = 0x3000;
    private const uint MemRelease = 0x8000;
    private const uint PageNoAccess = 0x01;
    private const uint PageReadWrite = 0x04;

    private readonly byte* _base;
    private readonly nuint _total;
    private readonly byte* _usable;
    private readonly int _usableLength;
    private readonly byte[]? _fallback;

    public GuardedBuffer(int capacity)
    {
        if (OperatingSystem.IsWindows())
        {
            nuint windowsPage = (nuint)Environment.SystemPageSize;
            nuint windowsUsable = ((nuint)Math.Max(capacity, 1) + windowsPage - 1) / windowsPage * windowsPage;
            _total = windowsUsable + (2 * windowsPage);
            _base = (byte*)VirtualAlloc(null, _total, MemCommitReserve, PageReadWrite);
            if (_base == null)
            {
                throw new InvalidOperationException("VirtualAlloc failed");
            }

            _usable = _base + windowsPage;
            _usableLength = (int)windowsUsable;
            if (!VirtualProtect(_base, windowsPage, PageNoAccess, out _) || !VirtualProtect(_usable + windowsUsable, windowsPage, PageNoAccess, out _))
            {
                throw new InvalidOperationException("VirtualProtect failed");
            }

            return;
        }

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            _fallback = new byte[capacity];
            return;
        }

        nuint page = (nuint)Environment.SystemPageSize;
        nuint usable = ((nuint)Math.Max(capacity, 1) + page - 1) / page * page;
        _total = usable + (2 * page);
        int anonymous = OperatingSystem.IsMacOS() ? 0x1000 : 0x20;
        void* memory = mmap(null, _total, ProtRead | ProtWrite, 0x0002 /* MAP_PRIVATE */ | anonymous, -1, 0);
        if (memory == (void*)-1)
        {
            throw new InvalidOperationException("mmap failed");
        }

        _base = (byte*)memory;
        _usable = _base + page;
        _usableLength = (int)usable;
        if (mprotect(_base, page, ProtNone) != 0 || mprotect(_usable + usable, page, ProtNone) != 0)
        {
            throw new InvalidOperationException("mprotect failed");
        }
    }

    /// <summary><paramref name="length"/> bytes ending exactly at the upper guard page.</summary>
    public Span<byte> AtEnd(int length) =>
        _fallback is not null ? _fallback.AsSpan(_fallback.Length - length) : new Span<byte>(_usable + _usableLength - length, length);

    /// <summary><paramref name="length"/> bytes starting exactly at the lower guard page.</summary>
    public Span<byte> AtStart(int length) =>
        _fallback is not null ? _fallback.AsSpan(0, length) : new Span<byte>(_usable, length);

    public int Capacity => _fallback?.Length ?? _usableLength;

    public void Dispose()
    {
        if (_base != null)
        {
            if (OperatingSystem.IsWindows())
            {
                VirtualFree(_base, 0, MemRelease);
            }
            else
            {
                munmap(_base, _total);
            }
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern void* VirtualAlloc(void* address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(void* address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(void* address, nuint size, uint freeType);

    [DllImport("libc", SetLastError = true)]
    private static extern void* mmap(void* addr, nuint length, int prot, int flags, int fd, nint offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(void* addr, nuint length, int prot);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(void* addr, nuint length);
}
