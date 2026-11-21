using System;
using System.IO.Hashing;
using System.Threading;

namespace ManagedCompressions.Zstd.Internal;

/// <summary>
/// A frame's content checksum (XXH64) computed on a thread of the pool while the frame decodes on
/// another: the decoder publishes how much content it has written after each block, and the helper
/// hashes it as it comes, a chunk at a time. If no thread has taken the helper's work by the time the
/// frame is decoded, the decoding thread hashes the content itself, as it would without the helper: a
/// busy pool delays nothing.
/// </summary>
/// <remarks>
/// XXH64 is bound by computation, not by memory: hashing the content block by block while it is still
/// in the caches did not shorten a frame's decoding (the audit's X2), where moving the hash to another
/// core takes it off the decoding thread altogether. A 211 MB frame of silesia at level 3 decoded 8.7%
/// faster without its checksum on a Ryzen 9 7950X, the share this recovers.
/// </remarks>
internal sealed unsafe class ParallelChecksum : IThreadPoolWorkItem
{
    private const int Pending = 0;
    private const int Running = 1;
    private const int Reclaimed = 2;

    /// <summary>The helper hashes at least this much at a time, but the frame's last bytes.</summary>
    private const int Chunk = 1 << 20;

    private readonly XxHash64 _hash = new();
    private readonly ManualResetEventSlim _progress = new(initialState: false);
    private readonly ManualResetEventSlim _done = new(initialState: false);
    private byte* _content;
    private long _published;
    private long _hashed;
    private int _state;
    private bool _final;
    private bool _abandoned;

    /// <summary>Queues the helper for the content that will be written from <paramref name="content"/>, which must stay pinned until <see cref="Finish"/> or <see cref="Abandon"/>.</summary>
    public void Start(byte* content)
    {
        _content = content;
        _published = 0;
        _hashed = 0;
        _final = false;
        _abandoned = false;
        _hash.Reset();
        _progress.Reset();
        _done.Reset();
        Volatile.Write(ref _state, Pending);
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    /// <summary>The decoder has written <paramref name="written"/> bytes of the content.</summary>
    public void Publish(long written)
    {
        Volatile.Write(ref _published, written);
        if (written - Volatile.Read(ref _hashed) >= Chunk)
        {
            _progress.Set();
        }
    }

    /// <summary>
    /// The checksum of the content, <paramref name="length"/> bytes in all: the helper's, once it has
    /// hashed the last bytes, or the decoding thread's own when the helper never started.
    /// </summary>
    public ulong Finish(long length)
    {
        Volatile.Write(ref _published, length);
        Volatile.Write(ref _final, true);
        _progress.Set();
        if (Interlocked.CompareExchange(ref _state, Reclaimed, Pending) == Running)
        {
            _done.Wait();
            return _hash.GetCurrentHashAsUInt64();
        }

        return XxHash64.HashToUInt64(new ReadOnlySpan<byte>(_content, checked((int)length)));
    }

    /// <summary>
    /// The frame failed before its checksum: stops the helper and waits until it no longer reads the
    /// content. Nothing happens once <see cref="Finish"/> has returned.
    /// </summary>
    public void Abandon()
    {
        Volatile.Write(ref _abandoned, true);
        _progress.Set();

        // Only a helper that took the work has a done to wait for: a pending one is reclaimed, and a
        // reclaimed one (by Finish, or by an earlier Abandon) never runs.
        if (Interlocked.CompareExchange(ref _state, Reclaimed, Pending) == Running)
        {
            _done.Wait();
        }
    }

    void IThreadPoolWorkItem.Execute()
    {
        if (Interlocked.CompareExchange(ref _state, Running, Pending) != Pending)
        {
            return;
        }

        try
        {
            while (!Volatile.Read(ref _abandoned))
            {
                bool final = Volatile.Read(ref _final);
                long available = Volatile.Read(ref _published) - _hashed;
                if (available >= Chunk || (final && available > 0))
                {
                    _hash.Append(new ReadOnlySpan<byte>(_content + _hashed, (int)available));
                    Volatile.Write(ref _hashed, _hashed + available);
                    continue;
                }

                if (final)
                {
                    break;
                }

                // Reset, then look again: a Publish between the two would otherwise be lost.
                _progress.Reset();
                if (Volatile.Read(ref _published) - _hashed < Chunk && !Volatile.Read(ref _final) && !Volatile.Read(ref _abandoned))
                {
                    _progress.Wait();
                }
            }
        }
        finally
        {
            _done.Set();
        }
    }
}
