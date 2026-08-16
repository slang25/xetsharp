using XetSharp.Hashing;
using XetSharp.Shards;

namespace XetSharp.Tests;

public class MdbShardTests
{
    /// <summary>
    /// Every reference shard variant must survive parse → serialize unchanged, byte for byte. That
    /// covers the four shapes the protocol uses: with and without verification entries, with and
    /// without a footer, and the HMAC-keyed global-dedupe response.
    /// </summary>
    [Test]
    [Arguments("ev-population.csv.shard")]
    [Arguments("ev-population.csv.shard.verification")]
    [Arguments("ev-population.csv.shard.verification-no-footer")]
    [Arguments("ev-population.csv.shard.dedupe")]
    public async Task Round_trips_reference_shards_byte_for_byte(string name)
    {
        var original = ReferenceFiles.Read(name);

        var reserialized = MdbShard.Parse(original).ToByteArray();

        await Assert.That(reserialized.Length).IsEqualTo(original.Length);
        await Assert.That(FirstDifference(original, reserialized)).IsEqualTo(-1);
    }

    /// <summary>
    /// The upload wire form is the same shard with its footer dropped, which is exactly what
    /// leaving <see cref="MdbShard.Footer"/> null produces.
    /// </summary>
    [Test]
    public async Task Dropping_the_footer_produces_the_upload_wire_form()
    {
        var withFooter = MdbShard.Parse(ReferenceFiles.Read("ev-population.csv.shard.verification"));
        var expected = ReferenceFiles.Read("ev-population.csv.shard.verification-no-footer");

        var uploadBody = (withFooter with { Footer = null }).ToByteArray();

        await Assert.That(uploadBody.Length).IsEqualTo(expected.Length);
        await Assert.That(FirstDifference(expected, uploadBody)).IsEqualTo(-1);
    }

    [Test]
    public async Task Reads_the_reference_file_reconstruction()
    {
        var shard = MdbShard.Parse(ReferenceFiles.Read("ev-population.csv.shard.verification"));

        var file = shard.Files.Single();
        await Assert.That(file.FileHash.ToString()).IsEqualTo(ReferenceFiles.FileHash);
        await Assert.That(file.Sha256?.ToString()).IsEqualTo(ReferenceFiles.Sha256);

        var term = file.Terms.Single();
        await Assert.That(term.XorbHash.ToString()).IsEqualTo(ReferenceFiles.XorbHash);
        await Assert.That(term.UnpackedLength).IsEqualTo(ReferenceFiles.FileLength);
        await Assert.That(term.ChunkIndexStart).IsEqualTo(0u);
        await Assert.That(term.ChunkIndexEnd).IsEqualTo((uint)ReferenceFiles.Chunks.Length);
        await Assert.That(term.VerificationHash?.ToString()).IsEqualTo(ReferenceFiles.XorbRangeHash);
    }

    /// <summary>
    /// The term's verification hash is reproducible from the chunk hashes it covers — the property
    /// that lets a server check an uploader really holds the data.
    /// </summary>
    [Test]
    public async Task Term_verification_hash_is_the_range_hash_of_its_chunks()
    {
        var shard = MdbShard.Parse(ReferenceFiles.Read("ev-population.csv.shard.verification"));
        var term = shard.Files.Single().Terms.Single();
        var chunkHashes = ReferenceFiles.Chunks[(int)term.ChunkIndexStart..(int)term.ChunkIndexEnd]
            .Select(chunk => chunk.Hash)
            .ToArray();

        await Assert.That(XetHashes.VerificationHash(chunkHashes)).IsEqualTo(term.VerificationHash);
    }

    [Test]
    public async Task Reads_the_reference_xorb_chunk_listing()
    {
        var shard = MdbShard.Parse(ReferenceFiles.Read("ev-population.csv.shard.verification"));

        var xorb = shard.Xorbs.Single();
        await Assert.That(xorb.XorbHash.ToString()).IsEqualTo(ReferenceFiles.XorbHash);
        await Assert.That(xorb.TotalUncompressedBytes).IsEqualTo(ReferenceFiles.FileLength);
        await Assert.That(xorb.Chunks.Count).IsEqualTo(ReferenceFiles.Chunks.Length);

        for (var i = 0; i < xorb.Chunks.Count; i++)
        {
            await Assert.That(xorb.Chunks[i].Hash).IsEqualTo(ReferenceFiles.Chunks[i].Hash);
            await Assert.That((ulong)xorb.Chunks[i].UnpackedLength).IsEqualTo(ReferenceFiles.Chunks[i].Length);
        }
    }

    /// <summary>
    /// The reference shards record each chunk's offset in the file's uncompressed byte stream, not
    /// its position in the serialized xorb — worth pinning, since the field name says "in CAS block".
    /// </summary>
    [Test]
    public async Task Chunk_byte_range_starts_accumulate_uncompressed_lengths()
    {
        var xorb = MdbShard.Parse(ReferenceFiles.Read("ev-population.csv.shard.verification")).Xorbs.Single();

        var offset = 0u;
        foreach (var chunk in xorb.Chunks)
        {
            await Assert.That(chunk.ByteRangeStart).IsEqualTo(offset);
            offset += chunk.UnpackedLength;
        }

        await Assert.That(offset).IsEqualTo(xorb.TotalUncompressedBytes);
    }

    [Test]
    public async Task Reads_the_footer_of_a_global_dedupe_response()
    {
        var shard = MdbShard.Parse(ReferenceFiles.Read("ev-population.csv.shard.dedupe"));

        await Assert.That(shard.Files).IsEmpty();
        await Assert.That(shard.Footer!.IsHmacProtected).IsTrue();
        await Assert.That(shard.Footer.ChunkHashHmacKey.ToString()).IsEqualTo(ReferenceFiles.DedupeHmacKey);
        await Assert.That(shard.Footer.CreatedAt).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1_758_663_941));
        await Assert.That(shard.Footer.ExpiresAt).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1_760_478_341));

        // A dedupe response carries no files, so it accounts for stored bytes but not materialized ones.
        await Assert.That(shard.Footer.MaterializedBytes).IsEqualTo(0ul);
        await Assert.That(shard.Footer.StoredBytes).IsEqualTo((ulong)ReferenceFiles.FileLength);
    }

    /// <summary>
    /// The base reference shard predates the verification section, so its file carries the metadata
    /// flag alone — the reader has to honour the flags rather than assume a layout.
    /// </summary>
    [Test]
    public async Task Reads_a_shard_whose_files_have_no_verification_entries()
    {
        var shard = MdbShard.Parse(ReferenceFiles.Read("ev-population.csv.shard"));

        var file = shard.Files.Single();
        await Assert.That(file.HasVerification).IsFalse();
        await Assert.That(file.Terms.Single().VerificationHash).IsNull();
        await Assert.That(file.Sha256?.ToString()).IsEqualTo(ReferenceFiles.Sha256);
        await Assert.That(shard.Footer!.MaterializedBytes).IsEqualTo((ulong)ReferenceFiles.FileLength);
    }

    /// <summary>
    /// A dedupe response hides its chunk hashes behind the footer's HMAC key; matching a local chunk
    /// means hashing it through the same key. Getting this wrong silently disables deduplication.
    /// </summary>
    [Test]
    public async Task Finds_chunks_in_an_hmac_protected_dedupe_shard()
    {
        var shard = MdbShard.Parse(ReferenceFiles.Read("ev-population.csv.shard.dedupe"));
        var key = shard.Footer!.ChunkHashHmacKey;

        for (var i = 0; i < 5; i++)
        {
            var chunkHash = ReferenceFiles.Chunks[i].Hash;

            await Assert.That(shard.Xorbs.Single().Chunks[i].Hash).IsEqualTo(XetHashes.Hmac(chunkHash, key));
            await Assert.That(shard.TryFindChunk(chunkHash, out var xorb, out var index)).IsTrue();
            await Assert.That(xorb!.XorbHash.ToString()).IsEqualTo(ReferenceFiles.XorbHash);
            await Assert.That(index).IsEqualTo(i);
        }
    }

    [Test]
    public async Task Does_not_find_a_chunk_the_dedupe_shard_has_never_seen()
    {
        var shard = MdbShard.Parse(ReferenceFiles.Read("ev-population.csv.shard.dedupe"));

        await Assert.That(shard.TryFindChunk(XetHashes.ChunkHash("not in the shard"u8), out _, out _)).IsFalse();
    }

    [Test]
    public async Task Round_trips_a_shard_built_from_scratch()
    {
        var shard = new MdbShard
        {
            Files =
            [
                new ShardFileInfo(
                    MerkleHash.Parse(ReferenceFiles.FileHash),
                    [
                        new ShardFileTerm(MerkleHash.Parse(ReferenceFiles.XorbHash), 1234, 0, 3, MerkleHash.Parse(ReferenceFiles.XorbRangeHash)),
                        new ShardFileTerm(MerkleHash.Parse(ReferenceFiles.XorbHash), 5678, 7, 9, MerkleHash.Parse(ReferenceFiles.XorbRangeHash)),
                    ])
                {
                    Sha256 = MerkleHash.Parse(ReferenceFiles.Sha256),
                },
            ],
            Xorbs =
            [
                new ShardCasInfo(
                    MerkleHash.Parse(ReferenceFiles.XorbHash),
                    6912,
                    4321,
                    [.. ReferenceFiles.Chunks.Take(9).Select((chunk, i) => new ShardCasChunk(chunk.Hash, (uint)i * 768, 768))]),
            ],
            Footer = new ShardFooter
            {
                ChunkHashHmacKey = MerkleHash.Parse(ReferenceFiles.DedupeHmacKey),
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            },
        };

        var serialized = shard.ToByteArray();
        var restored = MdbShard.Parse(serialized);

        await Assert.That(FirstDifference(serialized, restored.ToByteArray())).IsEqualTo(-1);
        await Assert.That(restored.Files.Single().Terms).IsEquivalentTo(shard.Files.Single().Terms, CollectionOrdering.Matching);
        await Assert.That(restored.Files.Single().Sha256).IsEqualTo(shard.Files.Single().Sha256);
        await Assert.That(restored.Xorbs.Single().Chunks).IsEquivalentTo(shard.Xorbs.Single().Chunks, CollectionOrdering.Matching);
        await Assert.That(restored.Footer).IsEqualTo(shard.Footer);
    }

    [Test]
    public async Task Rejects_a_file_whose_terms_disagree_about_verification()
    {
        var shard = new MdbShard
        {
            Files =
            [
                new ShardFileInfo(
                    MerkleHash.Parse(ReferenceFiles.FileHash),
                    [
                        new ShardFileTerm(MerkleHash.Parse(ReferenceFiles.XorbHash), 1, 0, 1, MerkleHash.Parse(ReferenceFiles.XorbRangeHash)),
                        new ShardFileTerm(MerkleHash.Parse(ReferenceFiles.XorbHash), 1, 1, 2),
                    ]),
            ],
            Xorbs = [],
        };

        await Assert.That(() => _ = shard.ToByteArray()).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(31)]
    public async Task Rejects_a_shard_whose_magic_is_wrong(int corruptedIndex)
    {
        var corrupted = ReferenceFiles.Read("ev-population.csv.shard");
        corrupted[corruptedIndex] ^= 0xFF;

        await Assert.That(() => _ = MdbShard.Parse(corrupted)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Rejects_an_unsupported_header_version()
    {
        var corrupted = ReferenceFiles.Read("ev-population.csv.shard");
        corrupted[32] = 3;

        await Assert.That(() => _ = MdbShard.Parse(corrupted)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Rejects_a_truncated_shard()
    {
        var truncated = ReferenceFiles.Read("ev-population.csv.shard")[..5000];

        await Assert.That(() => _ = MdbShard.Parse(truncated)).Throws<InvalidDataException>();
    }

    private static int FirstDifference(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (left[i] != right[i])
            {
                return i;
            }
        }

        return left.Length == right.Length ? -1 : Math.Min(left.Length, right.Length);
    }
}
