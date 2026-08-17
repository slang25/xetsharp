using BenchmarkDotNet.Attributes;
using XetSharp.Chunking;
using XetSharp.Hashing;
using XetSharp.Upload;

namespace XetSharp.Benchmarks;

/// <summary>
/// Packing a xorb — the upload path's dominant per-byte cost — at each degree of compression
/// parallelism, and the same thing with the chunk hashing the upload loop does alongside it. The
/// second is the one that decides the default: hashing stays on the calling thread, so it is what
/// the compression workers eventually run out of road against.
/// </summary>
/// <remarks>
/// Weights rather than random bytes, because that is the shape this client exists to move and the
/// shape where byte grouping — the expensive attempt — earns its keep. The output is byte-identical
/// at every parallelism, so these cases are measuring the same work, just spread differently.
/// </remarks>
[Config(typeof(XetBenchmarkConfig))]
public class PackingBenchmarks
{
    private const int ChunkSize = 64 * 1024;
    private const int ChunkCount = 256;
    private const int PayloadSize = ChunkSize * ChunkCount;

    /// <summary>What the upload pipeline reads at a time, so the chunker sees the same block shape.</summary>
    private const int ReadSize = 4 * 1024 * 1024;

    private byte[] _payload = [];
    private ReadOnlyMemory<byte>[] _chunks = [];
    private MerkleHash[] _hashes = [];

    [Params(1, 2, 4, 8)]
    public int Parallelism { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = BenchmarkData.Weights(0xF10A7, PayloadSize);
        _chunks = new ReadOnlyMemory<byte>[ChunkCount];
        _hashes = new MerkleHash[ChunkCount];
        for (var i = 0; i < ChunkCount; i++)
        {
            _chunks[i] = _payload.AsMemory(i * ChunkSize, ChunkSize);
            _hashes[i] = XetHashes.ChunkHash(_chunks[i].Span);
        }
    }

    /// <summary>Compression alone: what the extra threads have to work with.</summary>
    [Benchmark, Bytes(PayloadSize)]
    public async Task<int> Pack()
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes, Parallelism);
        for (var i = 0; i < _chunks.Length; i++)
        {
            await builder.AddAsync(_chunks[i], _hashes[i]);
        }

        return (await builder.BuildAsync()).Serialized.Length;
    }

    /// <summary>
    /// Packing with the per-chunk BLAKE3 the upload loop does before it. Hashing does not move off
    /// the calling thread, but it does overlap with the chunks already compressing.
    /// </summary>
    [Benchmark, Bytes(PayloadSize)]
    public async Task<int> HashAndPack()
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes, Parallelism);
        foreach (var chunk in _chunks)
        {
            await builder.AddAsync(chunk, XetHashes.ChunkHash(chunk.Span));
        }

        return (await builder.BuildAsync()).Serialized.Length;
    }

    /// <summary>
    /// Everything an uploaded byte costs in CPU: chunked out of read-sized blocks, hashed, and
    /// packed — the upload pipeline with the network, the deduplication queries and the file I/O
    /// taken out. The one figure to quote for how fast this client can push a file.
    /// </summary>
    [Benchmark, Bytes(PayloadSize)]
    public async Task<int> ChunkHashAndPack()
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes, Parallelism);
        var chunker = new Chunker();
        var chunks = new List<byte[]>();

        for (var offset = 0; offset < _payload.Length; offset += ReadSize)
        {
            var read = Math.Min(ReadSize, _payload.Length - offset);
            var isFinal = offset + read >= _payload.Length;

            chunks.Clear();
            chunker.NextBlock(_payload.AsSpan(offset, read), isFinal, chunks);
            foreach (var chunk in chunks)
            {
                await builder.AddAsync(chunk, XetHashes.ChunkHash(chunk));
            }
        }

        return (await builder.BuildAsync()).Serialized.Length;
    }
}
