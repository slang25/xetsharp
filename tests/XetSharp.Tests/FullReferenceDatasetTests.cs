using XetSharp.Chunking;
using XetSharp.Hashing;
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
}
