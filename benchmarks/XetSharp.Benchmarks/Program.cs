using BenchmarkDotNet.Running;
using XetSharp.Benchmarks;

// The sweep is not a BenchmarkDotNet suite — it measures a network, not a loop — so it takes the
// process before the switcher sees the arguments.
if (args is ["sweep", .. var sweepArguments])
{
    return await LiveTransferSweep.RunAsync(sweepArguments);
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;

/// <summary>Marker for the implicit entry-point class the switcher scans from.</summary>
public partial class Program;
