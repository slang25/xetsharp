namespace XetSharp.Server.Storage;

/// <summary>
/// Where a server keeps what the CAS API stores: xorbs and their chunk indexes, registered files,
/// and the sampled chunk index that answers global-deduplication queries. Implementations exist
/// for memory, a directory and S3; the endpoints are the same over all three.
/// </summary>
/// <remarks>
/// Every operation is content-addressed and idempotent, so a store never needs to reconcile
/// conflicting writes: two uploads of the same hash carry the same bytes.
/// </remarks>
public interface IXetStore
{
    /// <summary>The index of a stored xorb, or null when the server has no such xorb.</summary>
    ValueTask<StoredXorb?> GetXorbAsync(MerkleHash hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a xorb's bytes and index, returning false when the xorb was already present — the
    /// <c>was_inserted</c> the upload endpoint reports. The chunks eligible for global
    /// deduplication (see <see cref="GlobalDeduplication"/>) become findable through
    /// <see cref="FindChunkAsync"/>.
    /// </summary>
    ValueTask<bool> PutXorbAsync(StoredXorb xorb, ReadOnlyMemory<byte> serialized, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies bytes <c>[start, end]</c> (inclusive end) of a stored xorb to <paramref name="destination"/>.
    /// </summary>
    ValueTask CopyXorbRangeAsync(MerkleHash hash, long start, long end, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// A URL a client can fetch <c>[start, end]</c> of the xorb from directly, or null when the
    /// server should serve the bytes itself. An S3 store answers with a presigned URL so that xorb
    /// data never passes through the server; the others return null.
    /// </summary>
    ValueTask<Uri?> CreateDirectDownloadUrlAsync(MerkleHash hash, long start, long end, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>A registered file, or null.</summary>
    ValueTask<StoredFile?> GetFileAsync(MerkleHash fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A registered file by the SHA-256 of its contents, or null. This is how a Hub commit's LFS
    /// pointer, which names files by SHA-256, finds the Xet file it refers to.
    /// </summary>
    ValueTask<StoredFile?> GetFileBySha256Async(MerkleHash sha256, CancellationToken cancellationToken = default);

    /// <summary>Registers a file, returning false when it was already registered.</summary>
    ValueTask<bool> PutFileAsync(StoredFile file, CancellationToken cancellationToken = default);

    /// <summary>Where a chunk is stored, or null when it is not in the global-deduplication index.</summary>
    ValueTask<ChunkLocation?> FindChunkAsync(MerkleHash chunkHash, CancellationToken cancellationToken = default);
}

/// <summary>
/// The sampling rule of the global-deduplication index. Clients only ask about chunks whose hash
/// is 0 mod 1024, so indexing the rest would answer questions nobody asks; stores index exactly
/// the chunks this says to.
/// </summary>
public static class GlobalDeduplication
{
    /// <summary>The modulus the reference implementation samples chunks by.</summary>
    public const ulong SamplingModulus = 1024;

    public static bool IsEligible(MerkleHash chunkHash) => chunkHash.Mod(SamplingModulus) == 0;
}
