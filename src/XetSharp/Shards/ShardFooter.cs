namespace XetSharp.Shards;

/// <summary>
/// The optional 200-byte shard footer. It MUST be omitted when a shard is serialized as the body of
/// a shard upload; it is present on shards read from disk and on global-dedupe responses, where it
/// carries the key that protects the CAS-info chunk hashes.
/// </summary>
/// <remarks>
/// The spec marks two regions of the footer as reserved, but the reference implementation writes
/// real fields into both: lookup-table pointers at offsets 24–71 and byte-count totals at 168–191.
/// The totals are surfaced below so that a shard round-trips byte for byte; the lookup pointers are
/// not, because this library never emits the lookup sections they would point at.
/// </remarks>
public sealed record ShardFooter
{
    /// <summary>
    /// The key the CAS-info chunk hashes were HMAC'd with, or <see cref="MerkleHash.Zero"/> when
    /// they are stored unprotected. See <see cref="MdbShard.TryFindChunk"/>.
    /// </summary>
    public MerkleHash ChunkHashHmacKey { get; init; }

    /// <summary>When the shard was created, or null when the field is unset (zero).</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// When the shard stops being usable for deduplication, or null when the field is unset (zero).
    /// Past this point clients SHOULD NOT dedupe against it, and uploads referencing its xorbs may
    /// be rejected.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Total serialized length of the xorbs this shard introduces. Zero throughout the published
    /// reference shards, whose xorb had not been serialized when they were written.
    /// </summary>
    public ulong StoredBytesOnDisk { get; init; }

    /// <summary>Total unpacked length of the files in the file-info section.</summary>
    public ulong MaterializedBytes { get; init; }

    /// <summary>Total unpacked length of the chunks in the CAS-info section.</summary>
    public ulong StoredBytes { get; init; }

    /// <summary>Whether the CAS-info chunk hashes are HMAC-protected.</summary>
    public bool IsHmacProtected => ChunkHashHmacKey != MerkleHash.Zero;
}
