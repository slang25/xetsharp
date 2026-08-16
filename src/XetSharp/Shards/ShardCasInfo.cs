namespace XetSharp.Shards;

/// <summary>
/// One xorb's chunk listing as carried in a shard's CAS-info section.
/// </summary>
/// <param name="XorbHash">The xorb this block describes.</param>
/// <param name="TotalUncompressedBytes">Sum of the raw chunk lengths in the xorb (<c>num_bytes_in_cas</c>).</param>
/// <param name="SerializedLength">
/// Length of the xorb as uploaded (<c>num_bytes_on_disk</c>). Zero in the published reference
/// shards, which describe a xorb that had not been serialized when they were written.
/// </param>
/// <param name="Chunks">Every chunk in the xorb, in index order.</param>
public sealed record ShardCasInfo(
    MerkleHash XorbHash,
    uint TotalUncompressedBytes,
    uint SerializedLength,
    IReadOnlyList<ShardCasChunk> Chunks);

/// <summary>
/// One chunk within a xorb's CAS-info block.
/// </summary>
/// <param name="Hash">
/// The chunk hash — HMAC-transformed with the shard footer's key when that key is non-zero, as it
/// is in global-dedupe responses.
/// </param>
/// <param name="ByteRangeStart">
/// Where the chunk starts within the CAS block. The published reference shards use the chunk's
/// offset in the <em>uncompressed</em> stream, matching <see cref="ShardCasInfo.TotalUncompressedBytes"/>
/// rather than the serialized xorb.
/// </param>
/// <param name="UnpackedLength">The chunk's raw length.</param>
public sealed record ShardCasChunk(MerkleHash Hash, uint ByteRangeStart, uint UnpackedLength);
