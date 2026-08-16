using XetSharp.Hashing;

namespace XetSharp.Tests;

/// <summary>
/// The aggregate hashes for a real 63 MB file, over all 796 of its chunks. The existing hash tests
/// use synthetic vectors of a handful of chunks; these exercise the Merkle aggregation deep enough
/// to catch mistakes that only appear after several levels of grouping.
/// </summary>
public class ReferenceFileHashTests
{
    [Test]
    public async Task Xorb_hash_matches_the_reference()
    {
        await Assert.That(XetHashes.XorbHash(ReferenceFiles.Chunks).ToString()).IsEqualTo(ReferenceFiles.XorbHash);
    }

    [Test]
    public async Task File_hash_matches_the_reference()
    {
        await Assert.That(XetHashes.FileHash(ReferenceFiles.Chunks).ToString()).IsEqualTo(ReferenceFiles.FileHash);
    }

    [Test]
    public async Task Range_hash_over_every_chunk_matches_the_reference()
    {
        var chunkHashes = ReferenceFiles.Chunks.Select(chunk => chunk.Hash).ToArray();

        await Assert.That(XetHashes.VerificationHash(chunkHashes).ToString()).IsEqualTo(ReferenceFiles.XorbRangeHash);
    }

    [Test]
    public async Task Chunk_lengths_sum_to_the_file_length()
    {
        await Assert.That(ReferenceFiles.Chunks.Sum(chunk => (long)chunk.Length)).IsEqualTo(ReferenceFiles.FileLength);
    }

    /// <summary>
    /// A SHA-256 digest is stored in a shard with each 8-byte group reversed, because the reference
    /// implementation parses it from its hex form into the same hash type it uses everywhere else.
    /// </summary>
    [Test]
    public async Task Sha256_digest_bytes_convert_to_the_stored_hash_form()
    {
        var digest = Convert.FromHexString(ReferenceFiles.Sha256);

        var stored = MerkleHash.FromHexOrder(digest);

        await Assert.That(stored.ToString()).IsEqualTo(ReferenceFiles.Sha256);
        await Assert.That(stored).IsEqualTo(MerkleHash.Parse(ReferenceFiles.Sha256));
        await Assert.That(stored.ToByteArray()).IsNotEquivalentTo(digest, CollectionOrdering.Matching);

        var roundTripped = new byte[MerkleHash.Size];
        stored.CopyToHexOrder(roundTripped);
        await Assert.That(roundTripped).IsEquivalentTo(digest, CollectionOrdering.Matching);
    }
}
