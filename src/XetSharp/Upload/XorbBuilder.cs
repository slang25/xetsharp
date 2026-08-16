using XetSharp.Buffers;
using XetSharp.Hashing;
using XetSharp.Shards;
using XetSharp.Xorbs;

namespace XetSharp.Upload;

/// <summary>
/// A finished xorb: the bytes to POST, the hash to POST them under, and the chunk listing the
/// shard has to carry for it.
/// </summary>
internal sealed record PackedXorb(MerkleHash Hash, byte[] Serialized, ShardCasInfo Info);

/// <summary>
/// Accumulates chunks into a xorb, serializing each one as it arrives so that the size limit is
/// measured rather than estimated. <see cref="Build"/> seals the current xorb and leaves the
/// builder ready for the next.
/// </summary>
internal sealed class XorbBuilder(int maxUncompressedBytes) : IDisposable
{
    /// <summary>
    /// The most chunks packed into one xorb. The protocol sets no limit; this one exists so that
    /// <see cref="MaxUncompressedBytes"/> can be a fixed number — see its remarks.
    /// </summary>
    public const int MaxChunks = 8 * 1024;

    /// <summary>
    /// The most raw chunk data packed into one xorb.
    /// </summary>
    /// <remarks>
    /// The protocol's 64 MiB ceiling is on the <em>serialized</em> xorb, which is at most the raw
    /// bytes plus one 8-byte header per chunk — compression can only shrink a chunk, since a chunk
    /// that fails to compress is stored as-is. Leaving room for <see cref="MaxChunks"/> headers
    /// therefore makes it impossible to build a xorb the CAS service will reject, without having to
    /// unwind a chunk that turned out not to fit.
    /// </remarks>
    public const int MaxUncompressedBytes = XorbSerializer.MaxSerializedSize - (MaxChunks * XorbChunkHeader.Size);

    private readonly PooledBufferWriter _serialized = new(4 * 1024 * 1024);
    private readonly List<(MerkleHash Hash, ulong Length)> _chunks = [];
    private readonly List<ShardCasChunk> _listing = [];
    private uint _uncompressedBytes;

    public int ChunkCount => _chunks.Count;

    public bool IsEmpty => _chunks.Count == 0;

    /// <summary>Raw bytes packed so far — what the shard records as <c>num_bytes_in_cas</c>.</summary>
    public uint UncompressedBytes => _uncompressedBytes;

    /// <summary>Bytes the chunks packed so far serialize to.</summary>
    public int SerializedBytes => _serialized.WrittenCount;

    /// <summary>Whether a chunk of <paramref name="chunkLength"/> raw bytes still fits.</summary>
    public bool CanAdd(int chunkLength) =>
        _chunks.Count < MaxChunks && _uncompressedBytes + (long)chunkLength <= maxUncompressedBytes;

    /// <summary>
    /// Appends a chunk, compressing it with whichever scheme suits it, and returns the index it was
    /// given in this xorb.
    /// </summary>
    public int Add(ReadOnlySpan<byte> chunk, MerkleHash chunkHash)
    {
        if (!CanAdd(chunk.Length))
        {
            throw new InvalidOperationException(
                $"This xorb already holds {_chunks.Count} chunks and {_uncompressedBytes} bytes; seal it before adding more.");
        }

        var index = _chunks.Count;

        // Recorded before the length is added: the reference shards hold each chunk's offset in the
        // uncompressed stream, so the first chunk starts at zero.
        _listing.Add(new ShardCasChunk(chunkHash, _uncompressedBytes, (uint)chunk.Length));
        _chunks.Add((chunkHash, (ulong)chunk.Length));
        _uncompressedBytes += (uint)chunk.Length;
        XorbSerializer.SerializeChunk(chunk, _serialized);
        return index;
    }

    /// <summary>
    /// Seals the accumulated chunks into a xorb and resets the builder. Throws when nothing has
    /// been added: a xorb with no chunks has no meaningful hash.
    /// </summary>
    public PackedXorb Build()
    {
        if (IsEmpty)
        {
            throw new InvalidOperationException("A xorb must hold at least one chunk.");
        }

        var hash = XetHashes.XorbHash(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_chunks));
        var serialized = _serialized.WrittenSpan.ToArray();
        var packed = new PackedXorb(
            hash,
            serialized,
            new ShardCasInfo(hash, _uncompressedBytes, (uint)serialized.Length, [.. _listing]));

        _serialized.Reset();
        _chunks.Clear();
        _listing.Clear();
        _uncompressedBytes = 0;
        return packed;
    }

    public void Dispose() => _serialized.Dispose();
}
