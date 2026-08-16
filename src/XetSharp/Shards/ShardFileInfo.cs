namespace XetSharp.Shards;

/// <summary>
/// One file's reconstruction as carried in a shard's file-info section: the file's Xet ID and the
/// ordered terms whose decompressed chunks concatenate to the file's contents.
/// </summary>
/// <param name="FileHash">The file hash, which is also the file ID used in CAS API paths.</param>
/// <param name="Terms">The reconstruction terms, in file byte order, with no gaps between them.</param>
public sealed record ShardFileInfo(MerkleHash FileHash, IReadOnlyList<ShardFileTerm> Terms)
{
    /// <summary>
    /// SHA-256 of the whole file, from the optional file-metadata record. REQUIRED when uploading
    /// to a Git-based Hub repo, whose LFS pointer references it; optional for storage buckets.
    /// Its <see cref="MerkleHash.ToString"/> is the familiar SHA-256 hex — build one from a raw
    /// digest with <see cref="MerkleHash.FromHexOrder"/>.
    /// </summary>
    public MerkleHash? Sha256 { get; init; }

    /// <summary>
    /// Whether every term carries a verification hash. Shard uploads require them, and the format
    /// only allows all-or-nothing per file.
    /// </summary>
    public bool HasVerification => Terms.Count > 0 && Terms[0].VerificationHash is not null;
}

/// <summary>
/// One reconstruction term: a contiguous half-open chunk range within a single xorb.
/// </summary>
/// <param name="XorbHash">The xorb holding the term's chunks.</param>
/// <param name="UnpackedLength">Total decompressed length of the term's chunks; MUST be validated on download.</param>
/// <param name="ChunkIndexStart">First chunk index in the xorb, inclusive.</param>
/// <param name="ChunkIndexEnd">Last chunk index in the xorb, exclusive.</param>
/// <param name="VerificationHash">
/// The term's range hash over its chunk hashes, proving the uploader holds the data. Required in
/// uploaded shards; absent in shards that predate the verification section.
/// </param>
public sealed record ShardFileTerm(
    MerkleHash XorbHash,
    uint UnpackedLength,
    uint ChunkIndexStart,
    uint ChunkIndexEnd,
    MerkleHash? VerificationHash = null)
{
    /// <summary>Number of chunks the term covers.</summary>
    public uint ChunkCount => ChunkIndexEnd - ChunkIndexStart;
}
