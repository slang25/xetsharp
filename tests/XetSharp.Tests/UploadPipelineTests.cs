using XetSharp.Chunking;
using XetSharp.Hashing;
using XetSharp.Hub;
using XetSharp.Shards;
using XetSharp.Upload;
using XetSharp.Xorbs;

namespace XetSharp.Tests;

/// <summary>
/// The upload pipeline against a stand-in CAS service: chunk, deduplicate, pack, upload, register.
/// The strongest check here is the round trip — a file uploaded through this pipeline is downloaded
/// again through the download pipeline, so the xorbs and the shard have to agree with each other
/// and with the format layer.
/// </summary>
public class UploadPipelineTests
{
    private static readonly XetRepository Repository = XetRepository.Model("acme/scratch");

    [Test]
    public async Task Uploads_a_file_as_one_xorb_and_one_term()
    {
        var content = TestData.SplitMix64Bytes(1, 400_000);
        var (client, cas) = NewClient();

        var result = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);

        var expectedChunks = Chunker.ChunkAll(content);
        var uploaded = result.Files.Single();
        await Assert.That(uploaded.Path).IsEqualTo("data.bin");
        await Assert.That(uploaded.Size).IsEqualTo(content.Length);
        await Assert.That(uploaded.ChunkCount).IsEqualTo(expectedChunks.Count);
        await Assert.That(uploaded.Sha256).IsEqualTo(FakeCas.Sha256Of(content));
        await Assert.That(uploaded.FileId).IsEqualTo(
            XetHashes.FileHash([.. expectedChunks.Select(chunk => (XetHashes.ChunkHash(chunk), (ulong)chunk.Length))]));

        await Assert.That(cas.Xorbs.Count).IsEqualTo(1);
        var term = cas.Shards.Single().Files.Single().Terms.Single();
        await Assert.That(term.ChunkIndexStart).IsEqualTo(0u);
        await Assert.That(term.ChunkIndexEnd).IsEqualTo((uint)expectedChunks.Count);
        await Assert.That(term.UnpackedLength).IsEqualTo((uint)content.Length);
    }

    /// <summary>
    /// The whole point of the exercise: bytes that go up must come back. This runs the real
    /// download pipeline over the real upload's output, so a disagreement anywhere between the xorb
    /// serializer, the chunk offsets in the shard and the reconstruction shows up as wrong bytes.
    /// </summary>
    [Test]
    public async Task Round_trips_an_uploaded_file_through_the_download_pipeline()
    {
        var content = TestData.SplitMix64Bytes(7, 900_000);
        var (client, _) = NewClient();

        var result = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);

        using var downloaded = new MemoryStream();
        var download = await client.DownloadAsync(Repository, result.Files.Single().FileId.ToString(), downloaded);

        await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
        await Assert.That(download.FileHash).IsEqualTo(result.Files.Single().FileId);
        await Assert.That(download.Sha256).IsEqualTo(result.Files.Single().Sha256);
    }

    /// <summary>
    /// A file whose data spans several xorbs still reassembles, which is the case the single-xorb
    /// round trip cannot reach: terms end at xorb boundaries rather than at the end of the file.
    /// </summary>
    [Test]
    public async Task Round_trips_a_file_spread_over_several_xorbs()
    {
        var content = TestData.SplitMix64Bytes(11, 2_000_000);
        var (client, cas) = NewClient(new XetUploadOptions { MaxXorbBytes = 600_000 });

        var result = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);

        await Assert.That(cas.Xorbs.Count).IsGreaterThan(2);
        await Assert.That(cas.Shards.Single().Files.Single().Terms.Count).IsEqualTo(cas.Xorbs.Count);

        using var downloaded = new MemoryStream();
        await client.DownloadAsync(Repository, result.Files.Single().FileId.ToString(), downloaded);

        await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The same bytes uploaded twice cost one copy. The second file's reconstruction points at the
    /// first file's xorb, and no new xorb is created.
    /// </summary>
    [Test]
    public async Task Deduplicates_identical_files_within_one_upload()
    {
        var content = TestData.SplitMix64Bytes(3, 500_000);
        var (client, cas) = NewClient();

        var result = await client.UploadAsync(
            Repository,
            [XetUploadFile.FromBytes("first.bin", content), XetUploadFile.FromBytes("second.bin", content)]);

        await Assert.That(cas.Xorbs.Count).IsEqualTo(1);
        await Assert.That(result.XorbCount).IsEqualTo(1);
        await Assert.That(result.DeduplicatedBytes).IsEqualTo((long)content.Length);
        await Assert.That(result.Files[0].FileId).IsEqualTo(result.Files[1].FileId);

        var files = cas.Shards.Single().Files;
        await Assert.That(files.Count).IsEqualTo(2);
        await Assert.That(files[1].Terms.Single()).IsEqualTo(files[0].Terms.Single());
    }

    /// <summary>
    /// Repetition inside a single file deduplicates too, while the xorb holding it is still being
    /// packed — the case that needs the chunks-not-yet-in-a-xorb index.
    /// </summary>
    [Test]
    public async Task Deduplicates_repeated_content_inside_one_file()
    {
        var block = TestData.SplitMix64Bytes(5, 500_000);
        var content = (byte[])[.. block, .. block];
        var (client, cas) = NewClient();

        var result = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("doubled.bin", content)]);

        // The two halves chunk identically except where the join falls, so most of the second half
        // costs nothing.
        await Assert.That(result.DeduplicatedBytes).IsGreaterThan(block.Length * 3 / 4);
        await Assert.That(cas.Xorbs.Values.Sum(xorb => xorb.Length)).IsLessThan(content.Length);

        using var downloaded = new MemoryStream();
        await client.DownloadAsync(Repository, result.Files.Single().FileId.ToString(), downloaded);
        await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Every term carries the verification hash of the chunks it covers — the proof the service uses
    /// to check the uploader really holds the data it claims to be registering.
    /// </summary>
    [Test]
    public async Task Every_term_carries_the_verification_hash_of_its_chunks()
    {
        var content = TestData.SplitMix64Bytes(13, 700_000);
        var chunkHashes = Chunker.ChunkAll(content).Select(chunk => XetHashes.ChunkHash(chunk)).ToArray();
        var (client, cas) = NewClient(new XetUploadOptions { MaxXorbBytes = 300_000 });

        await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);

        var file = cas.Shards.Single().Files.Single();
        await Assert.That(file.HasVerification).IsTrue();

        var covered = 0;
        foreach (var term in file.Terms)
        {
            var expected = XetHashes.VerificationHash(chunkHashes[covered..(covered + (int)term.ChunkCount)]);
            await Assert.That(term.VerificationHash).IsEqualTo(expected);
            covered += (int)term.ChunkCount;
        }

        await Assert.That(covered).IsEqualTo(chunkHashes.Length);
    }

    /// <summary>
    /// The shard's CAS-info section describes each new xorb the way the reference shards do: chunk
    /// offsets accumulated over the uncompressed lengths, and the serialized length as uploaded.
    /// </summary>
    [Test]
    public async Task Describes_each_new_xorb_the_way_the_reference_shards_do()
    {
        var content = TestData.SplitMix64Bytes(17, 400_000);
        var (client, cas) = NewClient();

        await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);

        var xorb = cas.Shards.Single().Xorbs.Single();
        await Assert.That(xorb.SerializedLength).IsEqualTo((uint)cas.Xorbs[xorb.XorbHash].Length);
        await Assert.That(xorb.TotalUncompressedBytes).IsEqualTo((uint)content.Length);

        var offset = 0u;
        foreach (var chunk in xorb.Chunks)
        {
            await Assert.That(chunk.ByteRangeStart).IsEqualTo(offset);
            offset += chunk.UnpackedLength;
        }

        await Assert.That(offset).IsEqualTo(xorb.TotalUncompressedBytes);
        await Assert.That(XetHashes.XorbHash([.. xorb.Chunks.Select(chunk => (chunk.Hash, (ulong)chunk.UnpackedLength))]))
            .IsEqualTo(xorb.XorbHash);
    }

    /// <summary>
    /// The one ordering rule the protocol enforces: a shard naming a xorb the service does not have
    /// is rejected. The stand-in service enforces it too, so getting this wrong fails the test
    /// rather than passing quietly.
    /// </summary>
    [Test]
    public async Task Uploads_every_xorb_before_the_shard_that_names_them()
    {
        var content = TestData.SplitMix64Bytes(19, 2_000_000);
        var (client, cas) = NewClient(new XetUploadOptions { MaxXorbBytes = 300_000 });

        await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);

        var shardIndex = cas.Requests.FindIndex(request => request.EndsWith("/shards"));
        await Assert.That(shardIndex).IsGreaterThan(0);
        await Assert.That(cas.Requests[..shardIndex].Count(request => request.Contains("/v1/xorbs/"))).IsEqualTo(cas.Xorbs.Count);
        await Assert.That(cas.Requests[(shardIndex + 1)..]).IsEmpty();
    }

    /// <summary>A shard upload body carries no footer; the format's own round-trip test covers the rest.</summary>
    [Test]
    public async Task Sends_the_shard_in_its_footerless_upload_form()
    {
        var (client, cas) = NewClient();

        await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(23, 100_000))]);

        await Assert.That(cas.Shards.Single().Footer).IsNull();
        await Assert.That(MdbShard.Parse(cas.ShardBodies.Single()).ToByteArray())
            .IsEquivalentTo(cas.ShardBodies.Single(), CollectionOrdering.Matching);
    }

    /// <summary>
    /// A chunk the service already holds is referenced rather than re-uploaded, and the reference
    /// points at the service's xorb — which this client never saw the contents of.
    /// </summary>
    [Test]
    public async Task Deduplicates_against_the_global_index()
    {
        var content = TestData.SplitMix64Bytes(29, 600_000);
        var (client, cas) = NewClient(QueryEveryChunk);
        var existing = GiveTheServiceTheWholeFile(cas, content, MerkleHash.Parse(ReferenceFiles.DedupeHmacKey));

        var result = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);

        // The first query hits, and the shard it answers with covers every remaining chunk offline,
        // so nothing at all is uploaded.
        await Assert.That(result.GlobalDeduplicationQueries).IsEqualTo(1);
        await Assert.That(result.XorbCount).IsEqualTo(0);
        await Assert.That(result.DeduplicatedBytes).IsEqualTo((long)content.Length);
        await Assert.That(cas.Requests.Any(request => request.Contains("/v1/xorbs/"))).IsFalse();
        await Assert.That(cas.Shards.Single().Files.Single().Terms.Single().XorbHash).IsEqualTo(existing);

        // And the file the service now knows about really is the file, reconstructed from a xorb
        // this client never uploaded.
        using var downloaded = new MemoryStream();
        await client.DownloadAsync(Repository, result.Files.Single().FileId.ToString(), downloaded);
        await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A deduplication response is matched through the footer's HMAC key. An unprotected one has to
    /// work too, and getting the key handling wrong would silently disable deduplication rather
    /// than fail.
    /// </summary>
    [Test]
    public async Task Deduplicates_against_an_unprotected_deduplication_response()
    {
        var content = TestData.SplitMix64Bytes(59, 400_000);
        var (client, cas) = NewClient(QueryEveryChunk);
        GiveTheServiceTheWholeFile(cas, content, MerkleHash.Zero);

        var result = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);

        await Assert.That(result.XorbCount).IsEqualTo(0);
        await Assert.That(result.DeduplicatedBytes).IsEqualTo((long)content.Length);
    }

    /// <summary>Deduplication can be switched off, and then nothing is asked of the global index.</summary>
    [Test]
    public async Task Skips_the_global_index_when_asked_to()
    {
        var content = TestData.SplitMix64Bytes(31, 400_000);
        var (client, cas) = NewClient(QueryEveryChunk with { UseGlobalDeduplication = false });
        GiveTheServiceTheWholeFile(cas, content, MerkleHash.Zero);

        var result = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);

        await Assert.That(cas.DedupeQueries).IsEmpty();
        await Assert.That(result.GlobalDeduplicationQueries).IsEqualTo(0);
        await Assert.That(result.XorbCount).IsEqualTo(1);
    }

    /// <summary>
    /// A deduplication response past its expiry is dropped rather than used: referencing its xorbs
    /// risks the service rejecting the whole upload, which is a worse outcome than uploading data
    /// it already had.
    /// </summary>
    [Test]
    public async Task Ignores_an_expired_deduplication_response()
    {
        var content = TestData.SplitMix64Bytes(61, 400_000);
        var (client, cas) = NewClient(QueryEveryChunk);
        GiveTheServiceTheWholeFile(cas, content, MerkleHash.Zero, DateTimeOffset.UtcNow.AddDays(-1));

        var result = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);

        await Assert.That(cas.DedupeQueries).IsNotEmpty();
        await Assert.That(result.DeduplicatedBytes).IsEqualTo(0L);
        await Assert.That(result.XorbCount).IsEqualTo(1);
    }

    /// <summary>The streaming endpoint is preferred, and its predecessor is used where it is missing.</summary>
    [Test]
    public async Task Falls_back_to_the_v1_shard_upload_endpoint()
    {
        var (client, cas) = NewClient();
        cas.ServesStreamingShardUpload = false;

        await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(37, 100_000))]);
        await client.UploadAsync(Repository, [XetUploadFile.FromBytes("other.bin", TestData.SplitMix64Bytes(41, 100_000))]);

        await Assert.That(cas.Requests.Count(request => request == "POST /v2/shards")).IsEqualTo(1);
        await Assert.That(cas.Requests.Count(request => request == "POST /v1/shards")).IsEqualTo(2);
    }

    /// <summary>Uploads need a write token; a read token would be rejected by the service.</summary>
    [Test]
    public async Task Asks_for_a_write_scoped_token()
    {
        var (client, cas) = NewClient(QueryEveryChunk);

        await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(43, 100_000))]);

        await Assert.That(cas.DedupeQueries).IsNotEmpty();

        await Assert.That(cas.Requests.Any(request => request.Contains("xet-write-token"))).IsTrue();
        await Assert.That(cas.Requests.Any(request => request.Contains("xet-read-token"))).IsFalse();
    }

    [Test]
    public async Task Rejects_an_empty_file()
    {
        var (client, _) = NewClient();

        await Assert.That(async () => await client.UploadAsync(Repository, [XetUploadFile.FromBytes("empty.bin", default)]))
            .Throws<XetException>();
    }

    [Test]
    public async Task Rejects_the_same_path_twice()
    {
        var (client, _) = NewClient();
        var file = XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(47, 100_000));

        await Assert.That(async () => await client.UploadAsync(Repository, [file, file])).Throws<ArgumentException>();
    }

    /// <summary>
    /// A chunk that compresses is stored compressed, and the pipeline's own numbers stay in raw
    /// bytes — so an upload of compressible data moves less than it describes.
    /// </summary>
    [Test]
    public async Task Compresses_the_chunks_it_packs()
    {
        var content = new byte[500_000];
        TestData.SplitMix64Bytes(53, 50_000).CopyTo(content, 0);
        var (client, cas) = NewClient();

        var result = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("sparse.bin", content)]);

        var xorb = cas.Xorbs.Values.Single();
        await Assert.That(result.UploadedBytes).IsEqualTo((long)xorb.Length);
        await Assert.That(result.UploadedBytes).IsLessThan(content.Length / 2);

        // Compression is per chunk, so the serialized xorb still decodes to the raw bytes it packs.
        await Assert.That((uint)XorbSerializer.Deserialize(xorb).Sum(chunk => chunk.Length))
            .IsEqualTo(cas.Shards.Single().Xorbs.Single().TotalUncompressedBytes);
    }

    /// <summary>
    /// Options that ask about every chunk rather than the sampled one in a thousand, so a test can
    /// exercise global deduplication without generating gigabytes to make a hit likely.
    /// </summary>
    private static XetUploadOptions QueryEveryChunk => new() { GlobalDeduplicationSampleRate = 1 };

    /// <summary>
    /// Puts a real xorb holding all of <paramref name="content"/>'s chunks into the service, and
    /// arranges for its global-deduplication responses to point at it — the state the service would
    /// be in if someone else had uploaded the same file already. Returns the xorb's hash.
    /// </summary>
    private static MerkleHash GiveTheServiceTheWholeFile(
        FakeCas cas,
        byte[] content,
        MerkleHash hmacKey,
        DateTimeOffset? expiresAt = null)
    {
        var chunks = Chunker.ChunkAll(content);
        var hashes = chunks.Select(chunk => XetHashes.ChunkHash(chunk)).ToArray();
        var xorbHash = XetHashes.XorbHash([.. chunks.Select((chunk, i) => (hashes[i], (ulong)chunk.Length))]);

        var serialized = new System.Buffers.ArrayBufferWriter<byte>();
        XorbSerializer.Serialize(chunks.Select(chunk => (ReadOnlyMemory<byte>)chunk), serialized);
        cas.Xorbs[xorbHash] = serialized.WrittenSpan.ToArray();

        var offset = 0u;
        var listing = new List<ShardCasChunk>();
        for (var i = 0; i < chunks.Count; i++)
        {
            listing.Add(new ShardCasChunk(
                hmacKey == MerkleHash.Zero ? hashes[i] : XetHashes.Hmac(hashes[i], hmacKey),
                offset,
                (uint)chunks[i].Length));
            offset += (uint)chunks[i].Length;
        }

        cas.DedupeResponse = new MdbShard
        {
            Files = [],
            Xorbs = [new ShardCasInfo(xorbHash, offset, (uint)serialized.WrittenCount, listing)],
            Footer = new ShardFooter
            {
                ChunkHashHmacKey = hmacKey,
                ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(1),
            },
        };

        return xorbHash;
    }

    private static (XetClient Client, FakeCas Cas) NewClient(XetUploadOptions? upload = null)
    {
        var cas = new FakeCas();
        var client = new XetClient(new XetClientOptions
        {
            HubUrl = new Uri("https://hub.invalid"),
            UseAmbientCredentials = false,
            HttpClient = new HttpClient(new FakeHttpHandler(cas.Handler)),
            Upload = upload ?? XetUploadOptions.Default,
        });

        return (client, cas);
    }
}
