using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using XetSharp.Chunking;

namespace XetSharp.Benchmarks;

/// <summary>
/// The boundary scan on its own, with no chunk buffers or allocation in the way: the one-byte-at-a-
/// time loop the protocol describes against the four-at-a-time form the chunker actually runs.
/// Both are written out here rather than called into, so the comparison stays honest if the library
/// changes — and so the reason the shipping loop is shaped the way it is can be re-measured.
/// </summary>
/// <remarks>
/// Worth running with <c>--inProcess</c> on a machine with heterogeneous cores (any Apple Silicon
/// Mac): BenchmarkDotNet gives each benchmark its own process, and a process scheduled onto an
/// efficiency core reads as a four-fold regression that has nothing to do with the code.
/// </remarks>
[Config(typeof(XetBenchmarkConfig))]
public class GearhashScanBenchmarks
{
    private const int PayloadSize = 32 * 1024 * 1024;
    private const ulong Mask = 0xFFFF_0000_0000_0000;

    private byte[] _data = [];

    [GlobalSetup]
    public void Setup() => _data = BenchmarkData.Random(0x5EED, PayloadSize);

    /// <summary>
    /// <c>h = (h &lt;&lt; 1) + table[b]</c>, one byte at a time: two operations deep per byte, and the
    /// next byte cannot start until this one finishes.
    /// </summary>
    [Benchmark(Baseline = true), Bytes(PayloadSize)]
    public int ByteAtATime()
    {
        ref var table = ref MemoryMarshal.GetReference(GearTable.Table);
        var data = _data.AsSpan();
        var hash = 0ul;
        var boundaries = 0;

        for (var index = 0; index < data.Length; index++)
        {
            hash = unchecked((hash << 1) + Unsafe.Add(ref table, data[index]));
            if ((hash & Mask) == 0)
            {
                boundaries++;
                hash = 0;
            }
        }

        return boundaries;
    }

    /// <summary>
    /// The same loop reading the table through the span, so the bounds check the byte index cannot
    /// fail is left in. Measures what taking a reference to the table is worth.
    /// </summary>
    [Benchmark, Bytes(PayloadSize)]
    public int ByteAtATimeBoundsChecked()
    {
        var table = GearTable.Table;
        var data = _data.AsSpan();
        var hash = 0ul;
        var boundaries = 0;

        for (var index = 0; index < data.Length; index++)
        {
            hash = unchecked((hash << 1) + table[data[index]]);
            if ((hash & Mask) == 0)
            {
                boundaries++;
                hash = 0;
            }
        }

        return boundaries;
    }

    /// <summary>
    /// The same arithmetic with four bytes' worth written out, so only the <c>h &lt;&lt; n</c> terms
    /// depend on the group before.
    /// </summary>
    [Benchmark, Bytes(PayloadSize)]
    public int FourAtATime()
    {
        ref var table = ref MemoryMarshal.GetReference(GearTable.Table);
        var data = _data.AsSpan();
        var hash = 0ul;
        var boundaries = 0;
        var index = 0;

        while (true)
        {
            while (index + 4 <= data.Length)
            {
                var t0 = Unsafe.Add(ref table, data[index]);
                var t1 = Unsafe.Add(ref table, data[index + 1]);
                var t2 = Unsafe.Add(ref table, data[index + 2]);
                var t3 = Unsafe.Add(ref table, data[index + 3]);

                var h0 = unchecked((hash << 1) + t0);
                var h1 = unchecked((hash << 2) + (t0 << 1) + t1);
                var h2 = unchecked((hash << 3) + (t0 << 2) + (t1 << 1) + t2);
                var h3 = unchecked((hash << 4) + (t0 << 3) + (t1 << 2) + (t2 << 1) + t3);

                if ((((h0 & Mask) == 0) | ((h1 & Mask) == 0)) | (((h2 & Mask) == 0) | ((h3 & Mask) == 0)))
                {
                    break;
                }

                hash = h3;
                index += 4;
            }

            if (index >= data.Length)
            {
                return boundaries;
            }

            // The group that matched, and the tail, byte at a time — as the chunker does.
            var groupEnd = Math.Min(index + 4, data.Length);
            for (; index < groupEnd; index++)
            {
                hash = unchecked((hash << 1) + Unsafe.Add(ref table, data[index]));
                if ((hash & Mask) == 0)
                {
                    boundaries++;
                    hash = 0;
                }
            }

            if (index >= data.Length)
            {
                return boundaries;
            }
        }
    }
}
