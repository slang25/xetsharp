using XetSharp.Cas;
using XetSharp.Hub;
using XetSharp.Shards;

namespace XetSharp.Upload;

/// <summary>
/// Where a chunk lives: a xorb, and the chunk's index within it. <see cref="Xorb"/> is null while
/// the chunk sits in the xorb currently being packed, whose hash is not known until it is sealed.
/// </summary>
internal readonly record struct ChunkPlacement(MerkleHash? Xorb, int Index);

/// <summary>
/// Answers "does this chunk already exist somewhere?" for one upload. Sources, cheapest first: the
/// xorb currently being packed, chunks this upload has already sealed or matched, chunks named by
/// global-deduplication responses already in hand, and — for the sampled minority of chunks that
/// are eligible — a fresh query to the global-deduplication API.
/// </summary>
internal sealed class UploadDeduplicator(
    CasClient casClient,
    XetRepository repository,
    XetUploadOptions options,
    TimeProvider timeProvider)
{
    /// <summary>Chunks in the xorb being packed, which has no hash yet.</summary>
    private readonly Dictionary<MerkleHash, int> _pending = [];

    /// <summary>
    /// Chunks whose xorb is settled: everything this upload has sealed, plus every hit found in a
    /// deduplication response. Hits are cached here so a repeat costs a dictionary lookup rather
    /// than an HMAC against every shard.
    /// </summary>
    private readonly Dictionary<MerkleHash, ChunkLocation> _known = [];

    /// <summary>
    /// The deduplication shards this upload has collected. They cannot be merged into
    /// <see cref="_known"/>: each protects its chunk hashes with its own HMAC key, so matching means
    /// re-hashing the chunk once per shard.
    /// </summary>
    private readonly List<MdbShard> _shards = [];

    /// <summary>Chunks already asked about, so a miss is not paid for twice.</summary>
    private readonly HashSet<MerkleHash> _queried = [];

    /// <summary>Global-deduplication queries this upload has made.</summary>
    public int QueryCount { get; private set; }

    /// <summary>Records a chunk just placed into the xorb being packed.</summary>
    public void RecordPending(MerkleHash chunkHash, int index) => _pending.TryAdd(chunkHash, index);

    /// <summary>Settles the pending chunks against the hash the sealed xorb turned out to have.</summary>
    public void Seal(MerkleHash xorbHash)
    {
        foreach (var (chunkHash, index) in _pending)
        {
            _known.TryAdd(chunkHash, new ChunkLocation(xorbHash, index));
        }

        _pending.Clear();
    }

    /// <summary>
    /// Finds an existing home for <paramref name="chunkHash"/>, or null when the chunk has to be
    /// packed and uploaded.
    /// </summary>
    public async ValueTask<ChunkPlacement?> FindAsync(MerkleHash chunkHash, CancellationToken cancellationToken)
    {
        if (_pending.TryGetValue(chunkHash, out var pendingIndex))
        {
            return new ChunkPlacement(null, pendingIndex);
        }

        if (_known.TryGetValue(chunkHash, out var known))
        {
            return new ChunkPlacement(known.Xorb, known.Index);
        }

        if (Search(chunkHash) is { } found)
        {
            return found;
        }

        if (!options.UseGlobalDeduplication || !IsEligibleForGlobalQuery(chunkHash) || !_queried.Add(chunkHash))
        {
            return null;
        }

        QueryCount++;
        var shard = await casClient
            .QueryChunkDeduplicationAsync(repository, chunkHash, XetTokenScope.Write, cancellationToken)
            .ConfigureAwait(false);
        if (shard is null || IsExpired(shard))
        {
            return null;
        }

        _shards.Add(shard);
        return Search(chunkHash);
    }

    /// <summary>
    /// Whether a chunk is one of the sampled few worth a round trip. The protocol leaves the sample
    /// to the client; the reference implementation takes the chunk hash modulo
    /// <see cref="XetUploadOptions.DefaultGlobalDeduplicationSampleRate"/>, so roughly one chunk per
    /// 64 MiB of a default-chunked file is queried. A hit brings back a whole xorb's chunk listing,
    /// so the chunks around it are matched offline.
    /// </summary>
    private bool IsEligibleForGlobalQuery(MerkleHash chunkHash) =>
        chunkHash.Mod(options.GlobalDeduplicationSampleRate) == 0;

    private ChunkPlacement? Search(MerkleHash chunkHash)
    {
        foreach (var shard in _shards)
        {
            if (shard.TryFindChunk(chunkHash, out var xorb, out var index))
            {
                _known[chunkHash] = new ChunkLocation(xorb.XorbHash, index);
                return new ChunkPlacement(xorb.XorbHash, index);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a deduplication response has passed its expiry. Past it the service may reject an
    /// upload that references the shard's xorbs, which would fail the whole upload rather than just
    /// the deduplication — so an expired shard is dropped rather than used.
    /// </summary>
    private bool IsExpired(MdbShard shard) =>
        shard.Footer?.ExpiresAt is { } expiry && expiry <= timeProvider.GetUtcNow();

    private readonly record struct ChunkLocation(MerkleHash Xorb, int Index);
}
