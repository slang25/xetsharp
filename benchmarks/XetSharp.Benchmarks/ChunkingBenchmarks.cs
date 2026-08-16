using BenchmarkDotNet.Attributes;
using XetSharp.Chunking;

namespace XetSharp.Benchmarks;

/// <summary>
/// Content-defined chunking, the one pure-managed loop every uploaded byte passes through.
/// </summary>
[Config(typeof(XetBenchmarkConfig))]
public class ChunkingBenchmarks
{
    private const int PayloadSize = 32 * 1024 * 1024;

    /// <summary>The upload pipeline hands the chunker one read buffer at a time; this is its size.</summary>
    private const int ReadBlockSize = 4 * 1024 * 1024;

    private byte[] _random = [];
    private byte[] _weights = [];
    private readonly List<byte[]> _chunks = [];

    [GlobalSetup]
    public void Setup()
    {
        _random = BenchmarkData.Random(0x5EED, PayloadSize);
        _weights = BenchmarkData.Weights(0xF10A7, PayloadSize);
    }

    /// <summary>Incompressible data: boundaries land wherever the gearhash says, on average every 64 KiB.</summary>
    [Benchmark(Baseline = true), Bytes(PayloadSize)]
    public int ChunkRandomData() => ChunkAll(_random);

    /// <summary>
    /// Float32 weights, whose low-entropy high bytes make boundary hits rarer and chunks longer —
    /// the shape of data this client actually moves.
    /// </summary>
    [Benchmark, Bytes(PayloadSize)]
    public int ChunkWeights() => ChunkAll(_weights);

    /// <summary>Fed block by block, the way an upload reads a file, rather than as one buffer.</summary>
    [Benchmark, Bytes(PayloadSize)]
    public int ChunkStreamed()
    {
        var chunker = new Chunker();
        var count = 0;
        for (var offset = 0; offset < _random.Length; offset += ReadBlockSize)
        {
            var block = _random.AsSpan(offset, Math.Min(ReadBlockSize, _random.Length - offset));
            _chunks.Clear();
            chunker.NextBlock(block, isFinal: offset + ReadBlockSize >= _random.Length, _chunks);
            count += _chunks.Count;
        }

        return count;
    }

    private int ChunkAll(byte[] data)
    {
        _chunks.Clear();
        new Chunker().NextBlock(data, isFinal: true, _chunks);
        return _chunks.Count;
    }
}
