using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpFuzz;

namespace ManagedCompressions.Fuzz;

/// <summary>
/// Coverage-guided fuzzing of both libraries: libFuzzer generates the inputs, SharpFuzz reports which
/// branches each one took in the instrumented libraries, and a target checks what the libraries do
/// with it against the native libraries. See README.md.
/// </summary>
/// <remarks>
/// <code>
/// ManagedCompressions.Fuzz &lt;target&gt;                    under libfuzzer-dotnet: --target_path=&lt;this&gt; --target_arg=&lt;target&gt;
/// ManagedCompressions.Fuzz replay &lt;target&gt; &lt;path&gt;...  each input once, without libFuzzer: files, or directories of them
/// ManagedCompressions.Fuzz seed &lt;target&gt; &lt;directory&gt;  writes the inputs the target starts from
/// ManagedCompressions.Fuzz list                        the targets and what their inputs encode
/// </code>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        switch (args)
        {
            case ["list"]:
                foreach (Target target in Targets.All)
                {
                    Console.WriteLine($"{target.Name,-24} {target.Input}");
                }

                return 0;

            case ["seed", string name, string directory] when Targets.Find(name) is { } target:
                Directory.CreateDirectory(directory);
                int count = 0;
                foreach (byte[] input in target.Seeds())
                {
                    File.WriteAllBytes(Path.Combine(directory, $"seed-{count++:D4}"), input);
                }

                Console.WriteLine($"{name}: {count} seeds in {directory}");
                return 0;

            case ["replay", string name, .. string[] paths] when Targets.Find(name) is { } target:
                return Replay(target, paths);

            case ["smoke", string name, string directory, string iterations] when Targets.Find(name) is { } target:
                return Smoke(target, directory, int.Parse(iterations, System.Globalization.CultureInfo.InvariantCulture));

            case [string name] when Targets.Find(name) is { } target:
                Fuzzer.LibFuzzer.Run(target.Run);
                return 0;

            default:
                Console.Error.WriteLine("usage: ManagedCompressions.Fuzz <target> | replay <target> <path>... | seed <target> <directory> | list");
                Console.Error.WriteLine("targets: " + string.Join(", ", Targets.All.Select(t => t.Name)));
                return 2;
        }
    }

    /// <summary>Runs each input once; a failure is printed with its file, and fails the run.</summary>
    private static int Replay(Target target, string[] paths)
    {
        var files = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(path).Order(StringComparer.Ordinal));
            }
            else
            {
                files.Add(path);
            }
        }

        int failures = 0;
        foreach (string file in files)
        {
            try
            {
                target.Run(File.ReadAllBytes(file));
            }
            catch (Exception e)
            {
                failures++;
                Console.Error.WriteLine($"{file}: {e.GetType().Name}: {e.Message}");
            }
        }

        Console.WriteLine($"{target.Name}: {files.Count} inputs, {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Random mutations of the inputs in <paramref name="directory"/>, without coverage: a quick check
    /// where libFuzzer is not at hand. Each failing input is written next to them as <c>smoke-*</c>.
    /// </summary>
    private static int Smoke(Target target, string directory, int iterations)
    {
        byte[][] inputs = Directory.EnumerateFiles(directory, "seed-*").Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToArray();
        var random = new Random(1);
        int failures = 0;
        for (int i = 0; i < iterations && failures < 20; i++)
        {
            byte[] input = Mutate(random, inputs[random.Next(inputs.Length)]);
            try
            {
                target.Run(input);
            }
            catch (Exception e)
            {
                string path = Path.Combine(directory, $"smoke-{i:D6}");
                File.WriteAllBytes(path, input);
                Console.Error.WriteLine($"{path}: {e.GetType().Name}: {e.Message}");
                failures++;
            }
        }

        Console.WriteLine($"{target.Name}: {iterations} mutations, {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>A few bytes flipped, overwritten, inserted or removed, or the input cut short.</summary>
    private static byte[] Mutate(Random random, byte[] input)
    {
        var bytes = new List<byte>(input);
        for (int edits = random.Next(1, 5); edits > 0; edits--)
        {
            int at = bytes.Count == 0 ? 0 : random.Next(bytes.Count);
            switch (random.Next(5))
            {
                case 0 when bytes.Count > 0: bytes[at] ^= (byte)(1 << random.Next(8)); break;
                case 1 when bytes.Count > 0: bytes[at] = (byte)random.Next(256); break;
                case 2: bytes.Insert(at, (byte)random.Next(256)); break;
                case 3 when bytes.Count > 0: bytes.RemoveAt(at); break;
                case 4 when bytes.Count > 1: bytes.RemoveRange(at, bytes.Count - at); break;
            }
        }

        return bytes.ToArray();
    }
}
