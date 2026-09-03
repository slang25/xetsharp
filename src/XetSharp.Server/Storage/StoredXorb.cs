using System.Collections.Immutable;

namespace XetSharp.Server.Storage;

/// <summary>
/// What the server knows about a xorb it holds, worked out from the bytes themselves when the
/// xorb was uploaded rather than taken from any shard: the chunk hashes, each chunk's raw length,
/// and where each chunk record starts in the serialized xorb — which is what a reconstruction's
/// byte ranges are made of, and which a shard does not carry.
/// </summary>
/// <param name="Hash">The xorb hash, verified against the chunks when the xorb was indexed.</param>
/// <param name="SerializedLength">Length of the stored xorb, trailing bytes included.</param>
/// <param name="Chunks">Every chunk, in index order.</param>
public sealed record StoredXorb(MerkleHash Hash, long SerializedLength, ImmutableArray<StoredChunk> Chunks)
{
    // ImmutableArray compares by reference, which would make two identical indexes unequal.
    public bool Equals(StoredXorb? other) =>
        other is not null && Hash == other.Hash && SerializedLength == other.SerializedLength && Chunks.SequenceEqual(other.Chunks);

    public override int GetHashCode() => HashCode.Combine(Hash, SerializedLength, Chunks.Length);

    /// <summary>Sum of the chunks' raw lengths.</summary>
    public long UnpackedLength => Chunks.Sum(chunk => (long)chunk.UnpackedLength);

    /// <summary>
    /// The serialized byte range holding chunks <c>[start, end)</c>, with an inclusive end the way
    /// the CAS API and HTTP express it.
    /// </summary>
    public (long Start, long End) ByteRangeOf(int chunkStart, int chunkEnd)
    {
        var last = Chunks[chunkEnd - 1];
        return (Chunks[chunkStart].SerializedOffset, last.SerializedOffset + last.SerializedLength - 1);
    }

    /// <summary>Sum of the raw lengths of chunks <c>[start, end)</c>.</summary>
    public long UnpackedLengthOf(int chunkStart, int chunkEnd)
    {
        var total = 0L;
        for (var i = chunkStart; i < chunkEnd; i++)
        {
            total += Chunks[i].UnpackedLength;
        }

        return total;
    }
}

/// <summary>One chunk record within a stored xorb.</summary>
/// <param name="Hash">The chunk's hash, computed from its decompressed bytes.</param>
/// <param name="UnpackedLength">The chunk's raw length.</param>
/// <param name="SerializedOffset">Where the chunk's 8-byte header starts in the serialized xorb.</param>
/// <param name="SerializedLength">The record's length: header plus compressed data.</param>
public readonly record struct StoredChunk(MerkleHash Hash, int UnpackedLength, long SerializedOffset, int SerializedLength);

/// <summary>
/// A file the server has registered: its Xet file ID, the terms that rebuild it, and the SHA-256
/// a Git-backed repository's LFS pointer names it by.
/// </summary>
public sealed record StoredFile(MerkleHash FileId, long Size, MerkleHash? Sha256, ImmutableArray<StoredTerm> Terms)
{
    public bool Equals(StoredFile? other) =>
        other is not null && FileId == other.FileId && Size == other.Size && Sha256 == other.Sha256 && Terms.SequenceEqual(other.Terms);

    public override int GetHashCode() => HashCode.Combine(FileId, Size, Sha256, Terms.Length);

    /// <summary>The SHA-256 as the Hub reports it: 64 lowercase hex characters, or null when unknown.</summary>
    public string? Sha256Hex => Sha256?.ToString();
}

/// <summary>A run of chunks from one xorb; the file is the concatenation of its terms.</summary>
public readonly record struct StoredTerm(MerkleHash Xorb, int ChunkStart, int ChunkEnd, long UnpackedLength);

/// <summary>Where a chunk lives, for answering global-deduplication queries.</summary>
public readonly record struct ChunkLocation(MerkleHash Xorb, int ChunkIndex);
