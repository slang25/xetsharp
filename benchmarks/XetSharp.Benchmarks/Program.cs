using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Marker for the implicit entry-point class the switcher scans from.</summary>
public partial class Program;
