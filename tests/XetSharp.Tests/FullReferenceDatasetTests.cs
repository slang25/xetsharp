using XetSharp.Chunking;
using XetSharp.Hashing;
using XetSharp.Hub;
using XetSharp.Shards;
using XetSharp.Upload;
using XetSharp.Xorbs;

namespace XetSharp.Tests;

/// <summary>
/// End-to-end checks against the two reference artefacts too large to vendor: the original 63 MB
/// CSV and the 14 MB xorb built from it. Opt in with <c>XETSHARP_REFERENCE_DIR</c>; see
/// <see cref="SkipWithoutReferenceDatasetAttribute"/>.
/// </summary>
public class FullReferenceDatasetTests
{
    private const string XorbFileName = "eea25d6ee393ccae385820daed127b96ef0ea034dfb7cf6da3a950ce334b7632.xorb";

    private const string CsvFileName = "Electric_Vehicle_Population_Data_20250917.csv";

    /// <summary>
    /// Chunking a real 63 MB file must land on exactly the boundaries the reference implementation
    /// found — 796 of them — with matching hashes.
    /// </summary>
    [Test]
    [SkipWithoutReferenceDataset]
    public async Task Chunks_the_original_file_exactly_as_the_reference_does()
    {
        var chunks = Chunker.ChunkAll(SkipWithoutReferenceDatasetAttribute.Read(CsvFileName));

        await Assert.That(chunks.Count).IsEqualTo(ReferenceFiles.Chunks.Length);
        for (var i = 0; i < chunks.Count; i++)
        {
            await Assert.That((ulong)chunks[i].Length).IsEqualTo(ReferenceFiles.Chunks[i].Length);
            await Assert.That(XetHashes.ChunkHash(chunks[i])).IsEqualTo(ReferenceFiles.Chunks[i].Hash);
        }
    }

    /// <summary>
    /// Every chunk of the whole reference xorb decodes to the listed hash and length, and the four
    /// undocumented trailing bytes are left unread.
    /// </summary>
    [Test]
    [SkipWithoutReferenceDataset]
    public async Task Decodes_the_whole_reference_xorb()
    {
        var serialized = SkipWithoutReferenceDatasetAttribute.Read(XorbFileName);

        var chunks = XorbSerializer.Deserialize(serialized);

        await Assert.That(chunks.Count).IsEqualTo(ReferenceFiles.Chunks.Length);
        var chunkHashes = new (MerkleHash Hash, ulong Length)[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            chunkHashes[i] = (XetHashes.ChunkHash(chunks[i]), (ulong)chunks[i].Length);
            await Assert.That(chunkHashes[i]).IsEqualTo(ReferenceFiles.Chunks[i]);
        }

        await Assert.That(XetHashes.XorbHash(chunkHashes).ToString()).IsEqualTo(ReferenceFiles.XorbHash);
        await Assert.That(XetHashes.FileHash(chunkHashes).ToString()).IsEqualTo(ReferenceFiles.FileHash);
    }

    /// <summary>
    /// The upload path's cross-implementation check, and the strongest one in the suite: running the
    /// real 63 MB file through chunking, packing and shard building has to produce the very shard
    /// the reference implementation uploaded for it — byte for byte, once the one field it could not
    /// know is accounted for.
    /// </summary>
    /// <remarks>
    /// The published shard records <c>num_bytes_on_disk = 0</c>: it describes a xorb that had not
    /// been serialized when the shard was written. Ours records what we actually uploaded, so the
    /// comparison zeroes it — and then asserts separately that it is the length of the xorb the
    /// service received.
    /// </remarks>
    [Test]
    [SkipWithoutReferenceDataset]
    public async Task Builds_the_shard_the_reference_implementation_uploaded()
    {
        var cas = new FakeCas();
        using var client = new XetClient(new XetClientOptions
        {
            HubUrl = new Uri("https://hub.invalid"),
            UseAmbientCredentials = false,
            HttpClient = new HttpClient(new FakeHttpHandler(cas.Handler)),
        });

        var result = await client.UploadAsync(
            XetRepository.Dataset("xet-team/xet-spec-reference-files"),
            [XetUploadFile.FromFile(SkipWithoutReferenceDatasetAttribute.PathTo(CsvFileName), "reference.csv")]);

        await Assert.That(result.Files.Single().FileId.ToString()).IsEqualTo(ReferenceFiles.FileHash);
        await Assert.That(result.Files.Single().Sha256).IsEqualTo(ReferenceFiles.Sha256);
        await Assert.That(result.XorbCount).IsEqualTo(1);

        var built = cas.Shards.Single();
        await Assert.That(built.Xorbs.Single().SerializedLength).IsEqualTo((uint)cas.Xorbs.Values.Single().Length);

        var comparable = built with { Xorbs = [built.Xorbs.Single() with { SerializedLength = 0 }] };
        await Assert.That(comparable.ToByteArray())
            .IsEquivalentTo(ReferenceFiles.Read("ev-population.csv.shard.verification-no-footer"), CollectionOrdering.Matching);

    }

    /// <summary>
    /// The xorb the upload produced holds the reference file's chunks, in order, undamaged — but is
    /// not the reference implementation's bytes. Nothing requires it to be: a xorb is addressed by
    /// the hash of its chunks, which the shard comparison above already pins, and the compression
    /// scheme is the writer's to pick per chunk. Choosing the smallest of the three rather than
    /// always LZ4 makes this xorb about 70 KB smaller than the published one.
    /// </summary>
    [Test]
    [SkipWithoutReferenceDataset]
    public async Task Packs_the_reference_file_into_a_xorb_that_decodes_to_its_chunks()
    {
        var cas = new FakeCas();
        using var client = new XetClient(new XetClientOptions
        {
            HubUrl = new Uri("https://hub.invalid"),
            UseAmbientCredentials = false,
            HttpClient = new HttpClient(new FakeHttpHandler(cas.Handler)),
        });

        await client.UploadAsync(
            XetRepository.Dataset("xet-team/xet-spec-reference-files"),
            [XetUploadFile.FromFile(SkipWithoutReferenceDatasetAttribute.PathTo(CsvFileName), "reference.csv")]);

        var packed = cas.Xorbs.Values.Single();
        var chunks = XorbSerializer.Deserialize(packed);

        await Assert.That(chunks.Count).IsEqualTo(ReferenceFiles.Chunks.Length);
        for (var i = 0; i < chunks.Count; i++)
        {
            await Assert.That((XetHashes.ChunkHash(chunks[i]), (ulong)chunks[i].Length)).IsEqualTo(ReferenceFiles.Chunks[i]);
        }

        await Assert.That(packed.Length).IsLessThan(SkipWithoutReferenceDatasetAttribute.Read(XorbFileName).Length);
    }
}
