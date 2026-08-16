using XetSharp.Hashing;
using XetSharp.Upload;
using XetSharp.Xorbs;

namespace XetSharp.Tests;

/// <summary>
/// The packer that decides what goes in a xorb. Its one hard obligation is the CAS service's 64 MiB
/// ceiling on a serialized xorb: exceeding it means an upload the service refuses.
/// </summary>
public class XorbBuilderTests
{
    /// <summary>
    /// Filled with data that does not compress — the worst case, where every chunk is stored as-is
    /// and pays for its 8-byte header — a full xorb still serializes to under the limit.
    /// </summary>
    [Test]
    public async Task Stays_under_the_size_limit_on_data_that_will_not_compress()
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes);
        var chunk = TestData.SplitMix64Bytes(1, XorbChunkHeader.MaxUncompressedSize);

        while (builder.CanAdd(chunk.Length))
        {
            builder.Add(chunk, XetHashes.ChunkHash(chunk));
        }

        var packed = builder.Build();
        await Assert.That((uint)packed.Serialized.Length).IsGreaterThan(packed.Info.TotalUncompressedBytes);
        await Assert.That(packed.Serialized.Length).IsLessThanOrEqualTo(XorbSerializer.MaxSerializedSize);
    }

    /// <summary>
    /// The chunk-count cap is what makes the size guarantee a fixed number rather than something to
    /// discover a chunk too late.
    /// </summary>
    [Test]
    public async Task Stops_at_the_chunk_count_cap()
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes);
        var chunk = new byte[8];

        while (builder.CanAdd(chunk.Length))
        {
            builder.Add(chunk, XetHashes.ChunkHash(chunk));
        }

        await Assert.That(builder.ChunkCount).IsEqualTo(XorbBuilder.MaxChunks);
        await Assert.That(() => builder.Add(chunk, XetHashes.ChunkHash(chunk))).Throws<InvalidOperationException>();
    }

    /// <summary>
    /// What the builder produces is what the shard has to say about it: the xorb's hash over its
    /// chunks, the raw total, the serialized length, and each chunk's offset in the uncompressed
    /// stream.
    /// </summary>
    [Test]
    public async Task Describes_what_it_packed()
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes);
        var chunks = Enumerable.Range(0, 5).Select(i => TestData.SplitMix64Bytes((ulong)i + 1, 10_000 + i)).ToArray();
        foreach (var chunk in chunks)
        {
            builder.Add(chunk, XetHashes.ChunkHash(chunk));
        }

        var packed = builder.Build();

        await Assert.That(packed.Hash).IsEqualTo(
            XetHashes.XorbHash([.. chunks.Select(chunk => (XetHashes.ChunkHash(chunk), (ulong)chunk.Length))]));
        await Assert.That(packed.Info.XorbHash).IsEqualTo(packed.Hash);
        await Assert.That(packed.Info.SerializedLength).IsEqualTo((uint)packed.Serialized.Length);
        await Assert.That(packed.Info.TotalUncompressedBytes).IsEqualTo((uint)chunks.Sum(chunk => chunk.Length));

        var offset = 0u;
        for (var i = 0; i < chunks.Length; i++)
        {
            await Assert.That(packed.Info.Chunks[i].ByteRangeStart).IsEqualTo(offset);
            await Assert.That(packed.Info.Chunks[i].UnpackedLength).IsEqualTo((uint)chunks[i].Length);
            offset += (uint)chunks[i].Length;
        }

        await Assert.That(XorbSerializer.Deserialize(packed.Serialized).Select(chunk => chunk.Length))
            .IsEquivalentTo(chunks.Select(chunk => chunk.Length), CollectionOrdering.Matching);
    }

    /// <summary>Building empties the builder, so the next xorb starts at index zero and offset zero.</summary>
    [Test]
    public async Task Starts_over_after_building()
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes);
        var chunk = TestData.SplitMix64Bytes(9, 5_000);

        builder.Add(chunk, XetHashes.ChunkHash(chunk));
        builder.Build();

        await Assert.That(builder.IsEmpty).IsTrue();
        await Assert.That(builder.Add(chunk, XetHashes.ChunkHash(chunk))).IsEqualTo(0);
        await Assert.That(builder.Build().Info.Chunks.Single().ByteRangeStart).IsEqualTo(0u);
    }

    [Test]
    public async Task Refuses_to_build_a_xorb_with_no_chunks()
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes);

        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }
}
