using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Xunit;

namespace ManagedCompressions.Zstd.Tests.Unit;

/// <summary>
/// The instruction sets the run has, written to the log, and the knobs CI turns them off with checked
/// to have taken effect. A knob the runtime no longer reads changes nothing silently:
/// <c>DOTNET_EnableAVX512F</c> does nothing from .NET 10 on, where <c>DOTNET_EnableAVX512</c> took its
/// place, and a leg that sets the old name tests the AVX-512 paths it means to avoid.
/// </summary>
public sealed class InstructionSetTests
{
    [Fact]
    public void The_knobs_of_the_run_are_in_effect()
    {
        Console.WriteLine(
            $"{RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}: " +
            $"X86Base {X86Base.IsSupported}, Avx2 {Avx2.IsSupported}, Bmi2 {Bmi2.IsSupported}, Avx512BW {Avx512BW.IsSupported}, " +
            $"AdvSimd {AdvSimd.IsSupported}, Vector128 {Vector128.IsHardwareAccelerated}, " +
            $"Vector256 {Vector256.IsHardwareAccelerated}, Vector512 {Vector512.IsHardwareAccelerated}");

        if (Knob("DOTNET_EnableHWIntrinsic"))
        {
            Assert.False(Vector128.IsHardwareAccelerated);
            Assert.False(X86Base.IsSupported);
            Assert.False(AdvSimd.IsSupported);
        }

        if (Knob("DOTNET_EnableAVX2"))
        {
            Assert.False(Avx2.IsSupported);
            Assert.False(Avx512F.IsSupported);
        }

        if (Knob("DOTNET_EnableAVX512"))
        {
            Assert.False(Avx512F.IsSupported);
            Assert.False(Vector512.IsHardwareAccelerated);
        }
    }

    /// <summary>Whether <paramref name="name"/> is set to 0, which turns its instruction sets off.</summary>
    private static bool Knob(string name) => Environment.GetEnvironmentVariable(name) == "0";
}
