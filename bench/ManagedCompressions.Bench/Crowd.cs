using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace ManagedCompressions.Bench;

/// <summary>
/// The measure of one codec on one data set at one level, with <c>--crowd N</c> threads working at once
/// (a server compressing on all its cores, not one request on an idle machine). Each thread has its own
/// candidates (native contexts, managed instances) and its own output buffers; the input is shared and
/// read-only. A crowd pass starts every thread at a barrier on the same candidate and ends when the
/// last one is done; the candidates alternate pass by pass in rounds that rotate, as on one thread, so
/// that the speedups are paired round by round. Throughputs are the crowd's: N data sets over a pass.
/// </summary>
/// <remarks>
/// What a speedup measured alone does not show, a crowd does: candidates that contend for the shared
/// caches and memory, as mibz2's measures found ("fast alone, slow in a crowd"). Beside the rows, each
/// candidate's CPU time per GB of content, the bytes it allocated per pass and the process's peak
/// working set are printed.
/// </remarks>
internal sealed class Crowd : IDisposable
{
    private readonly Thread[] _threads;
    private readonly Barrier _start;
    private readonly Barrier _end;
    private readonly Action<int>[] _work;
    private Action<int>? _job;
    private bool _stopping;

    private Crowd(int threads, int[]? cpus)
    {
        _start = new Barrier(threads + 1);
        _end = new Barrier(threads + 1);
        _work = new Action<int>[threads];
        _threads = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int index = t;
            int? cpu = cpus is null ? null : cpus[t % cpus.Length];
            _threads[t] = new Thread(() => Loop(index, cpu)) { IsBackground = true, Name = "crowd-" + index.ToString(CultureInfo.InvariantCulture) };
            _threads[t].Start();
        }
    }

    public static List<Row> Measure(string codec, DataSet set, int level, int threads, int[]? cpus, double budget, bool compress, bool decompress, bool aa, string? only)
    {
        int records = set.Records;
        int[] bounds = Harness.Bounds(codec, set);
        var release = new List<Action>();
        var candidates = new List<Candidate>[threads];
        var scratch = new byte[threads][];
        var decoded = new byte[threads][];
        var written = new int[threads][];
        for (int t = 0; t < threads; t++)
        {
            candidates[t] = Candidates.For(codec, set, level, release, aa);
            scratch[t] = Harness.Touched(bounds[^1]);
            decoded[t] = Harness.Touched(set.Content.Length);
            written[t] = new int[records];
        }

        List<Candidate> first = candidates[0];
        int count = first.Count;
        byte[] reference = Harness.Touched(bounds[^1]);
        int[] sizes = new int[records];
        Harness.Pass(first[0].Compress!, set.Content, set.Offsets, reference, bounds, sizes);
        (byte[] frames, int[] frameOffsets) = Harness.Concatenate(reference, bounds, sizes);
        long referenceBytes = sizes.Sum(s => (long)s);

        using var crowd = new Crowd(threads, cpus);
        crowd.Run(t => Harness.Warm(codec, set, candidates[t]));

        List<int> timed = Enumerable.Range(0, count).Where(c => only is null || first[c].Name == only).ToList();
        List<int> compressing = timed.Where(c => first[c].Compress is not null).ToList();
        double bytes = (double)set.Content.Length * threads;
        var rows = new List<Row>();
        var ratio = new double[count];

        if (compress)
        {
            var differs = new bool[count];
            var cost = new Cost[count];
            double[][] samples = Harness.Time(count, compressing, budget, (c, round) =>
            {
                double seconds = crowd.Timed(t => Harness.Pass(candidates[t][c].Compress!, set.Content, set.Offsets, scratch[t], bounds, written[t]), ref cost[c]);
                if (round == 0)
                {
                    ratio[c] = (double)set.Content.Length / written[0].Sum(w => (long)w);
                    for (int t = 0; t < threads; t++)
                    {
                        differs[c] |= !Harness.RoundTrips(first[0], set, scratch[t], bounds, written[t], decoded[t])
                            || (c != 0 && !Harness.SameRecords(scratch[t], written[t], reference, sizes, bounds));
                    }
                }

                return seconds;
            });
            foreach (int c in compressing)
            {
                rows.Add(Harness.MakeRow(codec, Name(set, threads), level, "compression", first, samples, c, bytes, ratio[c], differs[c]));
                cost[c].Print(first[c].Name, "compression", bytes);
            }
        }

        if (decompress)
        {
            var differs = new bool[count];
            var cost = new Cost[count];
            double[][] samples = Harness.Time(count, timed, budget, (c, round) =>
            {
                if (round == 0)
                {
                    foreach (byte[] d in decoded)
                    {
                        Array.Fill(d, (byte)1);
                    }
                }

                double seconds = crowd.Timed(t => Harness.Pass(candidates[t][c].Decompress, frames, frameOffsets, decoded[t], set.Offsets, written[t]), ref cost[c]);
                if (round == 0)
                {
                    for (int t = 0; t < threads; t++)
                    {
                        differs[c] |= !Harness.Decoded(set, decoded[t], written[t]);
                    }
                }

                return seconds;
            });
            double referenceRatio = (double)set.Content.Length / referenceBytes;
            foreach (int c in timed)
            {
                rows.Add(Harness.MakeRow(codec, Name(set, threads), level, "decompression", first, samples, c, bytes, referenceRatio, differs[c]));
                cost[c].Print(first[c].Name, "decompression", bytes);
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MiB ({threads} threads{(cpus is null ? string.Empty : " on CPUs " + string.Join(',', cpus.Take(threads)))})"));
        release.ForEach(r => r());
        return rows;
    }

    /// <summary>The data set's name in the rows: <c>silesia x16</c> for sixteen threads.</summary>
    private static string Name(DataSet set, int threads) => string.Create(CultureInfo.InvariantCulture, $"{set.Name} x{threads}");

    /// <summary>
    /// <c>--crowd-cpus</c>: logical processors as <c>0-15</c>, <c>0,2,4</c> or <c>0-31:2</c> (every second
    /// one), thread t kept on the t-th of them (Windows).
    /// </summary>
    public static int[] ParseCpus(string text)
    {
        var cpus = new List<int>();
        foreach (string part in text.Split(','))
        {
            string[] stepped = part.Split(':');
            int step = stepped.Length > 1 ? int.Parse(stepped[1], CultureInfo.InvariantCulture) : 1;
            string[] range = stepped[0].Split('-');
            int from = int.Parse(range[0], CultureInfo.InvariantCulture);
            int to = range.Length > 1 ? int.Parse(range[1], CultureInfo.InvariantCulture) : from;
            for (int cpu = from; cpu <= to; cpu += step)
            {
                cpus.Add(cpu);
            }
        }

        return cpus.ToArray();
    }

    public void Dispose()
    {
        _stopping = true;
        _start.SignalAndWait();
        foreach (Thread thread in _threads)
        {
            thread.Join();
        }

        _start.Dispose();
        _end.Dispose();
    }

    /// <summary>Every thread runs <paramref name="job"/>(its index) once; returns when the last is done.</summary>
    private void Run(Action<int> job)
    {
        _job = job;
        _start.SignalAndWait();
        _end.SignalAndWait();
    }

    /// <summary>
    /// <see cref="Run"/>, timed from the start barrier to the last thread's end, its process CPU time
    /// and its allocations added to <paramref name="cost"/>.
    /// </summary>
    private double Timed(Action<int> job, ref Cost cost)
    {
        TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
        long allocated = GC.GetTotalAllocatedBytes(precise: false);
        long start = Stopwatch.GetTimestamp();
        Run(job);
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        cost.Add((Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds, GC.GetTotalAllocatedBytes(precise: false) - allocated);
        return seconds;
    }

    private void Loop(int index, int? cpu)
    {
        NativeThreads.Raise();
        if (cpu is int processor)
        {
            NativeThreads.PinCurrentThread(processor);
        }

        while (true)
        {
            _start.SignalAndWait();
            if (_stopping)
            {
                return;
            }

            _job!(index);
            _end.SignalAndWait();
        }
    }

    /// <summary>A candidate's process CPU time and allocations over its timed passes.</summary>
    private struct Cost
    {
        private double _cpuSeconds;
        private long _allocated;
        private int _passes;

        public void Add(double cpuSeconds, long allocated)
        {
            _cpuSeconds += cpuSeconds;
            _allocated += allocated;
            _passes++;
        }

        public readonly void Print(string candidate, string direction, double bytesPerPass) =>
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {candidate,-12} {direction,-13} CPU {_cpuSeconds / (_passes * bytesPerPass / 1e9):F3} s/GB, allocated {_allocated / (double)_passes:F0} B a pass ({_passes} passes)"));
    }
}
