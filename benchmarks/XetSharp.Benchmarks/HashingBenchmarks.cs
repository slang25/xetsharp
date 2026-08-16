using BenchmarkDotNet.Attributes;
using XetSharp.Hashing;

namespace XetSharp.Benchmarks;

/// <summary>
/// The protocol's hash constructions. Chunk hashing is BLAKE3 over the data and should run at
/// native SIMD speed; the aggregations are ASCII formatting plus many small hashes, and are the
/// part worth watching.
/// </summary>
[Config(typeof(XetBenchmarkConfig))]
public class HashingBenchmarks
{
    private const int ChunkSize = 64 * 1024;
    private const int NodeCount = 4096;

    private byte[] _chunk = [];
    private (MerkleHash Hash, ulong Length)[] _nodes = [];
    private MerkleHash[] _hashes = [];

    [GlobalSetup]
    public void Setup()
    {
        _chunk = BenchmarkData.Random(0xC0FFEE, ChunkSize);
        _nodes = new (MerkleHash, ulong)[NodeCount];
        _hashes = new MerkleHash[NodeCount];
        for (var i = 0; i < NodeCount; i++)
        {
            var hash = XetHashes.ChunkHash(BenchmarkData.Random((ulong)i + 1, 64));
            _nodes[i] = (hash, (ulong)ChunkSize);
            _hashes[i] = hash;
        }
    }

    [Benchmark, Bytes(ChunkSize)]
    public MerkleHash ChunkHash() => XetHashes.ChunkHash(_chunk);

    /// <summary>Aggregating one 256 MiB file's worth of chunks into its file ID.</summary>
    [Benchmark, Bytes((long)NodeCount * ChunkSize)]
    public MerkleHash FileHash() => XetHashes.FileHash(_nodes);

    [Benchmark, Bytes((long)NodeCount * ChunkSize)]
    public MerkleHash XorbHash() => XetHashes.XorbHash(_nodes);

    [Benchmark, Bytes((long)NodeCount * ChunkSize)]
    public MerkleHash VerificationHash() => XetHashes.VerificationHash(_hashes);
}
