using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace ManagedCompressions.Benchmarks;

/// <summary>A benchmark class instantiated outside BenchmarkDotNet's engine: its parameters set, its global setup run.</summary>
internal static class BenchmarkInstances
{
    public static object Create(Type type, IEnumerable<(string Name, object? Value)> parameters)
    {
        object instance = Activator.CreateInstance(type)!;
        foreach ((string name, object? value) in parameters)
        {
            type.GetProperty(name)!.SetValue(instance, value);
        }

        foreach (MethodInfo setup in type.GetMethods().Where(m => m.GetCustomAttribute<GlobalSetupAttribute>() is not null))
        {
            setup.Invoke(instance, null);
        }

        return instance;
    }

    /// <summary>Every combination of a class's parameters: here, its one level, or none.</summary>
    public static IEnumerable<(string Name, object? Value)[]> Parameters(Type type)
    {
        PropertyInfo? level = type.GetProperties().FirstOrDefault(p => p.GetCustomAttribute<ParamsAttribute>() is not null);
        if (level is null)
        {
            return [[]];
        }

        return level.GetCustomAttribute<ParamsAttribute>()!.Values.Select(v => new (string, object?)[] { (level.Name, v) });
    }

    public static IEnumerable<Type> Types() =>
        typeof(BenchmarkInstances).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.GetMethods().Any(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null));
}
