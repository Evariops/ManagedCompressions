using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
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
            .WithArguments([new MsBuildArgument("/p:UseLdClassicXCodeLinker=false"), .. X64InstructionSet()])
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

    /// <summary>
    /// On x64, the level of this machine's instruction set rather than <c>native</c>, which
    /// BenchmarkDotNet's project asks for. ILC 11.0.0-rc.1 targeting <c>native</c> on a CPU with
    /// AVX-512 and no AVX-VNNI, as a Zen 4, compiles <c>AvxVnni.IsSupported</c> in a branch into a
    /// jump to itself, and BenchmarkDotNet's CPU detection, which tests it, never returns
    /// (dotnet/runtime#135603). With the level, AVX-VNNI and the other extensions light up at run time.
    /// The codecs use nothing past AVX-512 F, BW, CD, DQ and VL.
    /// </summary>
    private static MsBuildArgument[] X64InstructionSet()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return [];
        }

        string level = Avx512F.VL.IsSupported && Avx512BW.VL.IsSupported && Avx512CD.VL.IsSupported && Avx512DQ.VL.IsSupported
            ? "x86-64-v4"
            : Avx2.IsSupported && Bmi1.IsSupported && Bmi2.IsSupported && Fma.IsSupported && Lzcnt.IsSupported ? "x86-64-v3" : "x86-64-v2";
        return [new MsBuildArgument("/p:IlcInstructionSet=" + level)];
    }
}
