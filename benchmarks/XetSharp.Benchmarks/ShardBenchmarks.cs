using System.Buffers;
using BenchmarkDotNet.Attributes;
using XetSharp.Hashing;
using XetSharp.Shards;

namespace XetSharp.Benchmarks;

/// <summary>
/// Shard writing and parsing, at roughly the size an upload of a few GB produces: a shard record is
/// 48 bytes per chunk, so this is a fixed cost per upload rather than a per-byte one.
/// </summary>
[Config(typeof(XetBenchmarkConfig))]
public class ShardBenchmarks
{
    private const int XorbCount = 16;
    private const int ChunksPerXorb = 1024;
    private const int FileCount = 32;
    private const int TermsPerFile = 16;
    private const int ChunkSize = 64 * 1024;

    private MdbShard _shard = null!;
    private byte[] _serialized = [];
    private ArrayBufferWriter<byte> _destination = new(1);

    [GlobalSetup]
    public void Setup()
    {
        var xorbs = new List<ShardCasInfo>(XorbCount);
        var hashes = new MerkleHash[XorbCount];
        for (var x = 0; x < XorbCount; x++)
        {
            var chunks = new List<ShardCasChunk>(ChunksPerXorb);
            for (var c = 0; c < ChunksPerXorb; c++)
            {
                chunks.Add(new ShardCasChunk(Hash(x, c), (uint)(c * ChunkSize), ChunkSize));
            }

            hashes[x] = Hash(x, -1);
            xorbs.Add(new ShardCasInfo(hashes[x], ChunksPerXorb * ChunkSize, ChunksPerXorb * ChunkSize, chunks));
        }

        var files = new List<ShardFileInfo>(FileCount);
        for (var f = 0; f < FileCount; f++)
        {
            var terms = new List<ShardFileTerm>(TermsPerFile);
            for (var t = 0; t < TermsPerFile; t++)
            {
                var start = (uint)(t * 8);
                terms.Add(new ShardFileTerm(hashes[(f + t) % XorbCount], 8 * ChunkSize, start, start + 8, Hash(f, t)));
            }

            files.Add(new ShardFileInfo(Hash(-1, f), terms) { Sha256 = Hash(f, f) });
        }

        _shard = new MdbShard { Files = files, Xorbs = xorbs };
        _serialized = _shard.ToByteArray();
        _destination = new ArrayBufferWriter<byte>(_serialized.Length);
    }

    [Benchmark, Bytes((long)XorbCount * ChunksPerXorb * ChunkSize)]
    public int Write()
    {
        _destination.ResetWrittenCount();
        return _shard.WriteTo(_destination);
    }

    [Benchmark, Bytes((long)XorbCount * ChunksPerXorb * ChunkSize)]
    public int Parse() => MdbShard.Parse(_serialized).Xorbs.Count;

    private static MerkleHash Hash(int a, int b) => XetHashes.ChunkHash([(byte)a, (byte)(a >> 8), (byte)b, (byte)(b >> 8)]);
}
