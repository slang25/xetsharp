using XetSharp.Hashing;

namespace XetSharp.Tests;

/// <summary>
/// Verifies the xorb and file hash constructions against the 18 reference vectors published in
/// the xet-core reference implementation (aggregated_hashes.rs, test_correctness), covering the
/// empty sequence, single-leaf identity, multi-level tree collapse, and salted finalization.
/// </summary>
public class XetHashesTests
{
    [Test]
    [MethodDataSource(typeof(AggregatedHashVectors), nameof(AggregatedHashVectors.Cases))]
    public async Task Xorb_and_file_hashes_match_reference_vectors(AggregatedHashVectors.Case testCase)
    {
        var leaves = testCase.Leaves
            .Select(leaf => (MerkleHash.Parse(leaf.Hash), leaf.Length))
            .ToArray();

        await Assert.That(XetHashes.XorbHash(leaves).ToString()).IsEqualTo(testCase.XorbHash);

        foreach (var (salt, expected) in testCase.FileHashes)
        {
            var fileHash = XetHashes.FileHash(leaves, MerkleHash.Parse(salt));
            await Assert.That(fileHash.ToString()).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Single_chunk_xorb_hash_is_the_chunk_hash_itself()
    {
        var chunkHash = XetHashes.ChunkHash([1, 2, 3]);

        await Assert.That(XetHashes.XorbHash([(chunkHash, 3)])).IsEqualTo(chunkHash);
    }
}
