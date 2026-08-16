using System.Buffers;
using BenchmarkDotNet.Attributes;
using XetSharp.Xorbs;

namespace XetSharp.Benchmarks;

/// <summary>
/// Xorb serialization. Every chunk is compressed twice on the way out — plain LZ4 and byte-grouped
/// LZ4 — because the format stores whichever came out smaller, so this is the upload path's second
/// biggest cost after chunking.
/// </summary>
[Config(typeof(XetBenchmarkConfig))]
public class XorbBenchmarks
{
    private const int ChunkSize = 64 * 1024;
    private const int ChunkCount = 128;
    private const int PayloadSize = ChunkSize * ChunkCount;

    private ReadOnlyMemory<byte>[] _randomChunks = [];
    private ReadOnlyMemory<byte>[] _weightChunks = [];
    private byte[] _serializedWeights = [];
    private ArrayBufferWriter<byte> _destination = new(1);

    [GlobalSetup]
    public void Setup()
    {
        _randomChunks = Split(BenchmarkData.Random(0x5EED, PayloadSize));
        _weightChunks = Split(BenchmarkData.Weights(0xF10A7, PayloadSize));
        _destination = new ArrayBufferWriter<byte>(PayloadSize + (ChunkCount * XorbChunkHeader.Size));

        var weights = new ArrayBufferWriter<byte>(PayloadSize);
        XorbSerializer.Serialize(_weightChunks, weights);
        _serializedWeights = weights.WrittenSpan.ToArray();
    }

    /// <summary>Incompressible chunks: both schemes are tried and both are discarded.</summary>
    [Benchmark, Bytes(PayloadSize)]
    public int SerializeRandomData()
    {
        _destination.ResetWrittenCount();
        return XorbSerializer.Serialize(_randomChunks, _destination);
    }

    /// <summary>Weights, where byte grouping earns its keep and the output is much smaller.</summary>
    [Benchmark, Bytes(PayloadSize)]
    public int SerializeWeights()
    {
        _destination.ResetWrittenCount();
        return XorbSerializer.Serialize(_weightChunks, _destination);
    }

    /// <summary>The download side: decompress a whole xorb's chunk records.</summary>
    [Benchmark, Bytes(PayloadSize)]
    public int DeserializeWeights() => XorbSerializer.Deserialize(_serializedWeights).Count;

    private static ReadOnlyMemory<byte>[] Split(byte[] data)
    {
        var chunks = new ReadOnlyMemory<byte>[ChunkCount];
        for (var i = 0; i < ChunkCount; i++)
        {
            chunks[i] = data.AsMemory(i * ChunkSize, ChunkSize);
        }

        return chunks;
    }
}
