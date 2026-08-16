using System.Runtime.InteropServices;
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
/// Accumulates chunks into a xorb, compressing each one as it arrives so that the size limit is
/// measured rather than estimated. <see cref="BuildAsync"/> seals the current xorb and leaves the
/// builder ready for the next.
/// </summary>
/// <remarks>
/// Compression is the upload path's dominant per-byte cost — several times what chunking and hashing
/// cost together — and every chunk compresses independently, so with a
/// <paramref name="compressionParallelism"/> above one the work goes to the thread pool and the
/// records are appended in the order the chunks were added. That ordering is what keeps the output
/// byte-identical whatever the setting: parallelism changes when a chunk is compressed, never how.
/// A bounded window of chunks is in flight at a time, so packing holds a few chunks' worth of
/// memory rather than the whole xorb's.
/// </remarks>
internal sealed class XorbBuilder(int maxUncompressedBytes, int compressionParallelism = 1) : IDisposable
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

    /// <summary>
    /// Chunks compressing on the thread pool, oldest first, each paired with the chunk it came from
    /// so a record that turns out to be stored uncompressed can still find its payload. Empty when
    /// compression runs inline.
    /// </summary>
    private readonly Queue<(ReadOnlyMemory<byte> Chunk, Task<CompressedChunk> Work)> _inFlight = new();

    /// <summary>
    /// How many chunks may be outstanding at once. Exactly the requested parallelism, and enforced
    /// by refusing to accept another chunk until the oldest is appended: a queued compression can
    /// only ever be <em>less</em> parallel than a running one, so a window of n is a true cap on how
    /// many run together, and one that keeps n of them busy.
    /// </summary>
    private readonly int _maxInFlight = compressionParallelism;

    private uint _uncompressedBytes;

    public int ChunkCount => _chunks.Count;

    public bool IsEmpty => _chunks.Count == 0;

    /// <summary>Raw bytes packed so far — what the shard records as <c>num_bytes_in_cas</c>.</summary>
    public uint UncompressedBytes => _uncompressedBytes;

    /// <summary>Whether a chunk of <paramref name="chunkLength"/> raw bytes still fits.</summary>
    public bool CanAdd(int chunkLength) =>
        _chunks.Count < MaxChunks && _uncompressedBytes + (long)chunkLength <= maxUncompressedBytes;

    /// <summary>
    /// Appends a chunk, compressing it with whichever scheme suits it, and returns the index it was
    /// given in this xorb. The chunk's memory must stay unmodified until the next
    /// <see cref="BuildAsync"/>: with compression parallelism above one it is read after this
    /// returns.
    /// </summary>
    public async ValueTask<int> AddAsync(ReadOnlyMemory<byte> chunk, MerkleHash chunkHash, CancellationToken cancellationToken = default)
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

        if (_maxInFlight <= 1)
        {
            XorbSerializer.SerializeChunk(chunk.Span, _serialized);
            return index;
        }

        while (_inFlight.Count >= _maxInFlight)
        {
            await AppendOldestAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _inFlight.Enqueue((chunk, Task.Run(() => XorbSerializer.CompressChunk(chunk.Span), CancellationToken.None)));
        return index;
    }

    /// <summary>
    /// Seals the accumulated chunks into a xorb and resets the builder. Throws when nothing has
    /// been added: a xorb with no chunks has no meaningful hash.
    /// </summary>
    public async ValueTask<PackedXorb> BuildAsync()
    {
        if (IsEmpty)
        {
            throw new InvalidOperationException("A xorb must hold at least one chunk.");
        }

        while (_inFlight.Count > 0)
        {
            await AppendOldestAsync().ConfigureAwait(false);
        }

        var hash = XetHashes.XorbHash(CollectionsMarshal.AsSpan(_chunks));
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

    public void Dispose()
    {
        // Nothing left to write these into, but they are holding pooled buffers and, if one of them
        // failed, an exception nobody has looked at. Whatever brought us here is the failure worth
        // reporting, so theirs is dropped.
        while (_inFlight.TryDequeue(out var pending))
        {
            try
            {
                pending.Work.GetAwaiter().GetResult().Dispose();
            }
            catch (Exception)
            {
                // Deliberately discarded; see above.
            }
        }

        _serialized.Dispose();
    }

    /// <summary>
    /// Appends the record for the chunk that has been waiting longest, blocking on it if it is still
    /// compressing. Always the oldest, never whichever finished first: the order chunks were added
    /// in is the order they appear in the xorb.
    /// </summary>
    private async ValueTask AppendOldestAsync()
    {
        var pending = _inFlight.Dequeue();
        using var compressed = await pending.Work.ConfigureAwait(false);
        XorbSerializer.WriteRecord(compressed, pending.Chunk.Span, _serialized);
    }
}
