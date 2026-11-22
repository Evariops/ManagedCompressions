#if CHECKED
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ManagedCompressions.Internal;

/// <summary>
/// The Checked configuration's memory for <see cref="PinnedBuffer{T}"/>: pages of their own, an
/// inaccessible page on each side, the block placed against one of them, so that the first byte
/// read or written past it faults. Freed pages are made inaccessible and kept so for a while, so that
/// a pointer kept past its block faults too. Never in the builds that ship: the packages are built in
/// Release, which has no P/Invoke.
/// </summary>
/// <remarks>
/// <para>
/// The block ends against the upper guard by default, where an overrun runs into it at once; with the
/// environment variable <c>MANAGEDCOMPRESSIONS_GUARD=start</c> it starts against the lower one, which
/// catches an underrun instead. A block whose content is not cleared is filled with 0xCD, so that a
/// read before a write shows in the output, which the tests compare with the native libraries'.
/// </para>
/// <para>
/// Windows, Linux and macOS; elsewhere a pinned array, unguarded.
/// </para>
/// </remarks>
internal sealed unsafe class GuardedMemory
{
    /// <summary>Where blocks are placed: against the upper guard page (the default), or the lower one.</summary>
    public static readonly bool AtStart =
        string.Equals(Environment.GetEnvironmentVariable("MANAGEDCOMPRESSIONS_GUARD"), "start", StringComparison.OrdinalIgnoreCase);

    /// <summary>The address space freed blocks keep, inaccessible, before the oldest is released.</summary>
    private const long QuarantineBytes = 8L << 30;

    private static readonly Queue<(nint Base, nuint Total)> s_quarantine = new();
    private static readonly Lock s_lock = new();
    private static long s_quarantined;

    private readonly byte* _base;
    private readonly nuint _total;
    private readonly byte* _usable;
    private readonly nuint _usableLength;
    private readonly byte[]? _fallback;

    /// <summary>The block's first byte.</summary>
    public readonly byte* Pointer;

    private GuardedMemory(nuint size, bool clear)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            _fallback = GC.AllocateArray<byte>(checked((int)size), pinned: true);
            fixed (byte* p = _fallback)
            {
                Pointer = p;
            }

            return;
        }

        nuint page = (nuint)Environment.SystemPageSize;
        _usableLength = Math.Max((size + page - 1) / page, 1) * page;
        _total = _usableLength + (2 * page);
        _base = Reserve(_total);
        _usable = _base + page;
        Commit(_usable, _usableLength);
        Pointer = AtStart ? _usable : _usable + _usableLength - size;
        if (!clear)
        {
            new Span<byte>(_usable, checked((int)_usableLength)).Fill(0xCD);
        }
    }

    /// <summary>A block of <paramref name="size"/> bytes, cleared unless <paramref name="clear"/> is false.</summary>
    public static GuardedMemory Allocate(nuint size, bool clear) => new(size, clear);

    ~GuardedMemory()
    {
        if (_base == null)
        {
            return;
        }

        // Inaccessible from now on, and kept so a while: a pointer kept past the block faults.
        Decommit(_usable, _usableLength);
        lock (s_lock)
        {
            s_quarantine.Enqueue(((nint)_base, _total));
            s_quarantined += (long)_total;
            while (s_quarantined > QuarantineBytes)
            {
                (nint oldBase, nuint oldTotal) = s_quarantine.Dequeue();
                s_quarantined -= (long)oldTotal;
                Release((byte*)oldBase, oldTotal);
            }
        }
    }

    private static byte* Reserve(nuint total)
    {
        if (OperatingSystem.IsWindows())
        {
            void* memory = VirtualAlloc(null, total, MemReserve, PageNoAccess);
            return memory != null ? (byte*)memory : throw new OutOfMemoryException("VirtualAlloc failed to reserve a guarded block.");
        }

        void* mapped = mmap(null, total, ProtNone, MapPrivate | MapAnonymous, -1, 0);
        return mapped != (void*)-1 ? (byte*)mapped : throw new OutOfMemoryException("mmap failed to reserve a guarded block.");
    }

    private static void Commit(byte* address, nuint length)
    {
        bool done = OperatingSystem.IsWindows()
            ? VirtualAlloc(address, length, MemCommit, PageReadWrite) != null
            : mprotect(address, length, ProtRead | ProtWrite) == 0;
        if (!done)
        {
            throw new OutOfMemoryException("Failed to commit a guarded block.");
        }
    }

    private static void Decommit(byte* address, nuint length)
    {
        // Windows decommits in place; elsewhere a fresh inaccessible mapping replaces the pages, which
        // returns them to the system where mprotect alone would keep them.
        if (OperatingSystem.IsWindows())
        {
            VirtualFree(address, length, MemDecommit);
        }
        else
        {
            mmap(address, length, ProtNone, MapPrivate | MapAnonymous | MapFixed, -1, 0);
        }
    }

    private static void Release(byte* address, nuint total)
    {
        if (OperatingSystem.IsWindows())
        {
            VirtualFree(address, 0, MemRelease);
        }
        else
        {
            munmap(address, total);
        }
    }

    /// <summary>Memory over a block, for the buffers the libraries hand out as <see cref="Memory{T}"/>: it keeps the block alive.</summary>
    internal sealed class Manager<T>(GuardedMemory owner, T* pointer, int length) : System.Buffers.MemoryManager<T>
        where T : unmanaged
    {
        private readonly GuardedMemory _owner = owner;

        public override Span<T> GetSpan() => new(pointer, length);

        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => new(pointer + elementIndex, default, this);

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing) => GC.KeepAlive(_owner);
    }

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemDecommit = 0x4000;
    private const uint MemRelease = 0x8000;
    private const uint PageNoAccess = 0x01;
    private const uint PageReadWrite = 0x04;
    private const int ProtNone = 0;
    private const int ProtRead = 1;
    private const int ProtWrite = 2;
    private const int MapPrivate = 0x02;
    private const int MapFixed = 0x10;
    private static readonly int MapAnonymous = OperatingSystem.IsMacOS() ? 0x1000 : 0x20;

    [DllImport("kernel32")]
    private static extern void* VirtualAlloc(void* address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32")]
    private static extern int VirtualFree(void* address, nuint size, uint freeType);

    [DllImport("libc")]
    private static extern void* mmap(void* address, nuint length, int protection, int flags, int fd, nint offset);

    [DllImport("libc")]
    private static extern int mprotect(void* address, nuint length, int protection);

    [DllImport("libc")]
    private static extern int munmap(void* address, nuint length);
}
#endif
