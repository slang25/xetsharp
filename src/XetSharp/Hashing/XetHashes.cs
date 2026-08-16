using System.Text;

namespace XetSharp.Hashing;

/// <summary>
/// The hash constructions defined by the Xet protocol. All are BLAKE3 in keyed mode (32-byte key,
/// 32-byte output) with fixed protocol keys; every conforming implementation must produce
/// bit-identical results.
/// </summary>
public static class XetHashes
{
    /// <summary>Key for chunk (Merkle leaf) hashes.</summary>
    private static ReadOnlySpan<byte> DataKey =>
    [
        102, 151, 245, 119, 91, 149, 80, 222, 49, 53, 203, 172, 165, 151, 24, 28,
        157, 228, 33, 16, 155, 235, 43, 88, 180, 208, 176, 75, 147, 173, 242, 41,
    ];

    /// <summary>Key for internal Merkle tree node hashes.</summary>
    private static ReadOnlySpan<byte> InternalNodeKey =>
    [
        1, 126, 197, 199, 165, 71, 41, 150, 253, 148, 102, 102, 180, 138, 2, 230,
        93, 221, 83, 111, 55, 199, 109, 210, 248, 99, 82, 230, 74, 83, 113, 63,
    ];

    /// <summary>Key for term verification (range) hashes in uploaded shards.</summary>
    private static ReadOnlySpan<byte> VerificationKey =>
    [
        127, 24, 87, 214, 206, 86, 237, 102, 18, 127, 249, 19, 231, 165, 195, 243,
        164, 205, 38, 213, 181, 219, 73, 230, 65, 36, 152, 127, 40, 251, 148, 195,
    ];

    /// <summary>
    /// The mean branching factor of the Merkle tree: nodes are grouped at a content-defined cut
    /// where a child hash mod 4 == 0, giving groups of 2–9 children averaging 4.
    /// </summary>
    private const ulong MeanTreeBranchingFactor = 4;

    private const int MaxGroupSize = (int)(2 * MeanTreeBranchingFactor + 1);

    /// <summary>Computes the hash of a chunk's contents (a Merkle tree leaf).</summary>
    public static MerkleHash ChunkHash(ReadOnlySpan<byte> chunkData) => KeyedHash(DataKey, chunkData);

    /// <summary>
    /// Computes the xorb hash: the Merkle tree root over the xorb's chunks, in order.
    /// A single chunk is its own root; an empty sequence hashes to zero.
    /// </summary>
    public static MerkleHash XorbHash(ReadOnlySpan<(MerkleHash Hash, ulong Length)> chunks) => AggregatedNodeHash(chunks);

    /// <summary>
    /// Computes the file hash (the file's Xet file ID): the Merkle tree root over all of the
    /// file's chunks, finalized with one more keyed hash using <paramref name="salt"/>
    /// (32 zero bytes — the default — outside salted repositories).
    /// </summary>
    public static MerkleHash FileHash(ReadOnlySpan<(MerkleHash Hash, ulong Length)> chunks, MerkleHash salt = default)
    {
        if (chunks.IsEmpty)
        {
            return MerkleHash.Zero;
        }

        return Hmac(AggregatedNodeHash(chunks), salt);
    }

    /// <summary>
    /// Computes the verification hash over a term's chunk hashes: BLAKE3 keyed over the raw
    /// 32-byte digests concatenated in order.
    /// </summary>
    public static MerkleHash VerificationHash(ReadOnlySpan<MerkleHash> chunkHashes)
    {
        using var hasher = Blake3.Hasher.NewKeyed(VerificationKey);
        Span<byte> buffer = stackalloc byte[MerkleHash.Size];
        foreach (var hash in chunkHashes)
        {
            hash.CopyTo(buffer);
            hasher.Update(buffer);
        }

        return Finalize(hasher);
    }

    /// <summary>
    /// Keyed-hashes <paramref name="value"/>'s raw bytes with <paramref name="key"/>'s raw bytes.
    /// Used for file-hash finalization and for matching HMAC-protected chunk hashes in
    /// global-deduplication shard responses.
    /// </summary>
    public static MerkleHash Hmac(MerkleHash value, MerkleHash key)
    {
        Span<byte> keyBytes = stackalloc byte[MerkleHash.Size];
        key.CopyTo(keyBytes);
        Span<byte> valueBytes = stackalloc byte[MerkleHash.Size];
        value.CopyTo(valueBytes);
        return KeyedHash(keyBytes, valueBytes);
    }

    /// <summary>
    /// Collapses (hash, length) nodes into a single root by repeatedly merging content-defined
    /// groups until one node remains, per the protocol's aggregated-hash construction.
    /// </summary>
    private static MerkleHash AggregatedNodeHash(ReadOnlySpan<(MerkleHash Hash, ulong Length)> nodes)
    {
        if (nodes.IsEmpty)
        {
            return MerkleHash.Zero;
        }

        var level = nodes.ToArray().AsSpan();
        while (level.Length > 1)
        {
            var write = 0;
            var read = 0;
            while (read < level.Length)
            {
                var cut = read + NextMergeCut(level[read..]);
                level[write++] = MergedHashOfSequence(level[read..cut]);
                read = cut;
            }

            level = level[..write];
        }

        return level[0].Hash;
    }

    /// <summary>
    /// Number of nodes in the next merge group: at least 2, at most 9, cutting after the first
    /// node (from the third onward) whose hash mod 4 == 0.
    /// </summary>
    private static int NextMergeCut(ReadOnlySpan<(MerkleHash Hash, ulong Length)> nodes)
    {
        if (nodes.Length <= 2)
        {
            return nodes.Length;
        }

        var end = Math.Min(MaxGroupSize, nodes.Length);
        for (var i = 2; i < end; i++)
        {
            if (nodes[i].Hash.Mod(MeanTreeBranchingFactor) == 0)
            {
                return i + 1;
            }
        }

        return end;
    }

    /// <summary>
    /// Merges a group of nodes into their parent: the internal-node keyed hash over one ASCII
    /// line per child of the form "{hash_hex} : {length}\n", paired with the summed length.
    /// </summary>
    private static (MerkleHash Hash, ulong Length) MergedHashOfSequence(ReadOnlySpan<(MerkleHash Hash, ulong Length)> group)
    {
        // 64 hex chars + " : " + at most 20 decimal digits + "\n" = 88 bytes per entry.
        Span<char> text = stackalloc char[MaxGroupSize * 88];
        var position = 0;
        var totalLength = 0ul;
        foreach (var (hash, length) in group)
        {
            hash.TryFormat(text[position..], out _, default, null);
            position += 64;
            text[position++] = ' ';
            text[position++] = ':';
            text[position++] = ' ';
            length.TryFormat(text[position..], out var digits);
            position += digits;
            text[position++] = '\n';
            totalLength += length;
        }

        Span<byte> ascii = stackalloc byte[position];
        Encoding.ASCII.GetBytes(text[..position], ascii);
        return (KeyedHash(InternalNodeKey, ascii), totalLength);
    }

    private static MerkleHash KeyedHash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        using var hasher = Blake3.Hasher.NewKeyed(key);
        hasher.Update(data);
        return Finalize(hasher);
    }

    private static MerkleHash Finalize(Blake3.Hasher hasher)
    {
        Span<byte> digest = stackalloc byte[MerkleHash.Size];
        hasher.Finalize(digest);
        return new MerkleHash(digest);
    }
}
