using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Metrology;

namespace ManagedCompressions.Benchmarks;

/// <summary>
/// The bytes a case writes: the compressed size of a compression, the content's of a decompression.
/// Taken in the host process by running the case once, its parameters set and its setup run, as
/// NativeCompressions' Payload column does.
/// </summary>
public sealed class PayloadColumn : IColumn
{
    private readonly Dictionary<string, object> _instances = [];
    private readonly Dictionary<string, string> _values = [];

    public string Id => nameof(PayloadColumn);

    public string ColumnName => "Payload";

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 0;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Size;

    public string Legend => "Bytes written: the compressed size, or the content's";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        var descriptor = benchmarkCase.Descriptor;
        string instanceKey = descriptor.Type.FullName + "/" + benchmarkCase.Parameters.DisplayInfo;
        string valueKey = instanceKey + "/" + descriptor.WorkloadMethod.Name;
        if (_values.TryGetValue(valueKey, out string? value))
        {
            return value;
        }

        if (!_instances.TryGetValue(instanceKey, out object? instance))
        {
            _instances[instanceKey] = instance = BenchmarkInstances.Create(
                descriptor.Type, benchmarkCase.Parameters.Items.Select(p => (p.Name, p.Value)));
        }

        return _values[valueKey] = descriptor.WorkloadMethod.Invoke(instance, null) is int written
            ? new SizeValue(written).ToString()
            : "-";
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) => GetValue(summary, benchmarkCase);

    public bool IsAvailable(Summary summary) => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
}
