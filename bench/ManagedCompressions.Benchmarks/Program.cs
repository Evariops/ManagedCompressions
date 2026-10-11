using System;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace ManagedCompressions.Benchmarks;

/// <summary>
/// BenchmarkDotNet's view of the codecs, as NativeCompressions' README presents its own:
/// <c>dotnet run -c Release --project bench/ManagedCompressions.Benchmarks -- --filter '*'</c>.
/// <c>--check</c> runs every case once instead, and checks that every decompression gives back the source.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--check"))
        {
            return Check();
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, new BenchmarkConfig());
        return 0;
    }

    private static int Check()
    {
        byte[] source = Silesia.Tar;
        int failures = 0;
        foreach (Type type in BenchmarkInstances.Types())
        {
            foreach ((string Name, object? Value)[] parameters in BenchmarkInstances.Parameters(type))
            {
                object instance = BenchmarkInstances.Create(type, parameters);
                byte[] destination = (byte[])typeof(SilesiaBenchmarkBase).GetField("Destination", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
                string level = string.Join(", ", parameters.Select(p => $"{p.Name} {p.Value}"));
                foreach (MethodInfo method in type.GetMethods().Where(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null))
                {
                    string description = method.GetCustomAttribute<BenchmarkAttribute>()!.Description!;
                    bool decompression = method.GetCustomAttribute<BenchmarkCategoryAttribute>()!.Categories[0] == "Decompress";
                    Array.Clear(destination);
                    int written = (int)method.Invoke(instance, null)!;
                    bool ok = written > 0 && (!decompression || (written == source.Length && destination.AsSpan(0, written).SequenceEqual(source)));
                    failures += ok ? 0 : 1;
                    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {type.Name,-14} {level,-9} {description,-50} {written,12:N0} bytes");
                }
            }
        }

        return failures;
    }
}
