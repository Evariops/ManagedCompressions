using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.DotNetCli;
using BenchmarkDotNet.Loggers;

namespace ManagedCompressions.Benchmarks;

/// <summary>
/// Two jobs, out of process: the JIT with its dynamic PGO, the runtime's default, and Native AOT, its
/// code for this machine's instruction set. Allocations measured, the cases grouped by job and category,
/// each group's NativeCompressions case the baseline of the Ratio column. Every operation compresses or
/// decompresses 202 MiB: three warm-up rounds, then ten measured rounds give the mean.
/// </summary>
public sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        Job measure = Job.Default.WithWarmupCount(3).WithIterationCount(10);
        AddJob(measure.WithId("JIT, dynamic PGO"));
        // The native link asks Xcode for its version to choose a linker flag; with the command line tools
        // alone that question fails, so the flag is decided here, as ManagedCompressions.Zstd.Perf
        // decides it.
        AddJob(measure.WithRuntime(NativeAotRuntime.Net11_0)
            .WithArguments([new MsBuildArgument("/p:UseLdClassicXCodeLinker=false")])
            .WithId("Native AOT"));
        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddColumn(new PayloadColumn());
        AddExporter(MarkdownExporter.GitHub);
        AddAnalyser(DefaultConfig.Instance.GetAnalysers().ToArray());
        AddValidator(DefaultConfig.Instance.GetValidators().ToArray());
        AddDiagnoser(MemoryDiagnoser.Default);
        AddLogicalGroupRules(BenchmarkLogicalGroupRule.ByJob, BenchmarkLogicalGroupRule.ByCategory);
        HideColumns(Column.Error, Column.StdDev, Column.Median, Column.RatioSD, Column.Gen0, Column.Gen1, Column.Gen2, Column.AllocRatio, Column.Arguments, Column.Runtime);
        WithOrderer(new BenchmarkDotNet.Order.DefaultOrderer(BenchmarkDotNet.Order.SummaryOrderPolicy.Declared, BenchmarkDotNet.Order.MethodOrderPolicy.Declared));
    }
}
