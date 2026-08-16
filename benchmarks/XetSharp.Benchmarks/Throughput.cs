using System.Globalization;
using System.Reflection;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace XetSharp.Benchmarks;

/// <summary>
/// How many bytes of payload one invocation of a benchmark processes, so its result can be read as
/// a throughput rather than a duration.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class BytesAttribute(long bytes) : Attribute
{
    public long Bytes => bytes;
}

/// <summary>Turns a mean duration and a <see cref="BytesAttribute"/> into MB/s.</summary>
internal sealed class ThroughputColumn : IColumn
{
    public string Id => nameof(ThroughputColumn);

    public string ColumnName => "MB/s";

    public string Legend => "Payload bytes processed per second (1 MB = 1,000,000 bytes)";

    public UnitType UnitType => UnitType.Dimensionless;

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 0;

    public bool IsNumeric => true;

    public bool IsAvailable(Summary summary) => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        var bytes = benchmarkCase.Descriptor.WorkloadMethod.GetCustomAttribute<BytesAttribute>()?.Bytes;
        var meanNanoseconds = summary[benchmarkCase]?.ResultStatistics?.Mean;
        if (bytes is not { } payload || meanNanoseconds is not { } mean || mean <= 0)
        {
            return "-";
        }

        return (payload * 1000.0 / mean).ToString("N0", CultureInfo.InvariantCulture);
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) => GetValue(summary, benchmarkCase);
}

/// <summary>The configuration every benchmark class here runs under.</summary>
internal sealed class XetBenchmarkConfig : ManualConfig
{
    public XetBenchmarkConfig()
    {
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddColumn(new ThroughputColumn());
        AddDiagnoser(MemoryDiagnoser.Default);
        AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
        WithOptions(ConfigOptions.DisableLogFile);
    }
}
