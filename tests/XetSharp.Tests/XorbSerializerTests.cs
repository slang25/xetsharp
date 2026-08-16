using System.Buffers;
using XetSharp.Hashing;
using XetSharp.Xorbs;

namespace XetSharp.Tests;

public class XorbSerializerTests
{
    /// <summary>
    /// The decisive interop test: bytes written by the reference implementation must decompress to
    /// chunks whose hashes match the reference's own listing.
    /// </summary>
    [Test]
    public async Task Reference_xorb_prefix_decodes_to_the_listed_chunks()
    {
        var serialized = ReferenceFiles.Read("ev-population.xorb.first-10-chunks");

        var chunks = XorbSerializer.Deserialize(serialized);

        await Assert.That(chunks.Count).IsEqualTo(ReferenceFiles.XorbPrefixChunkCount);
        for (var i = 0; i < chunks.Count; i++)
        {
            var (expectedHash, expectedLength) = ReferenceFiles.Chunks[i];
            await Assert.That((ulong)chunks[i].Length).IsEqualTo(expectedLength);
            await Assert.That(XetHashes.ChunkHash(chunks[i])).IsEqualTo(expectedHash);
        }
    }

    [Test]
    public async Task Reference_xorb_prefix_is_consumed_exactly()
    {
        var serialized = ReferenceFiles.Read("ev-population.xorb.first-10-chunks");

        await Assert.That(BytesLeftOver(serialized)).IsEqualTo(0);

        static int BytesLeftOver(ReadOnlySpan<byte> serialized)
        {
            var reader = new XorbChunkReader(serialized);
            var chunk = new ArrayBufferWriter<byte>();
            while (reader.TryReadChunk(chunk))
            {
                chunk.ResetWrittenCount();
            }

            return reader.Remaining.Length;
        }
    }

    /// <summary>
    /// The spec calls scheme 1 "standard LZ4 compression" without saying which framing. The
    /// reference bytes settle it: every payload opens with the LZ4 frame magic number.
    /// </summary>
    [Test]
    public async Task Reference_chunks_are_lz4_frames_not_bare_blocks()
    {
        var records = ReadRecordSummaries(ReferenceFiles.Read("ev-population.xorb.first-10-chunks"));

        await Assert.That(records.Count).IsEqualTo(ReferenceFiles.XorbPrefixChunkCount);
        foreach (var (header, magic) in records)
        {
            await Assert.That(header.Version).IsEqualTo(XorbChunkHeader.CurrentVersion);
            await Assert.That(header.CompressionScheme).IsEqualTo(ChunkCompressionScheme.Lz4);
            await Assert.That(magic).IsEquivalentTo(new byte[] { 0x04, 0x22, 0x4d, 0x18 }, CollectionOrdering.Matching);
        }

        static List<(XorbChunkHeader Header, byte[] Magic)> ReadRecordSummaries(ReadOnlySpan<byte> serialized)
        {
            var records = new List<(XorbChunkHeader, byte[])>();
            var reader = new XorbChunkReader(serialized);
            while (reader.TryReadRawChunk(out var header, out var data))
            {
                records.Add((header, data[..4].ToArray()));
            }

            return records;
        }
    }

    [Test]
    [Arguments(ChunkCompressionScheme.None)]
    [Arguments(ChunkCompressionScheme.Lz4)]
    [Arguments(ChunkCompressionScheme.ByteGrouping4Lz4)]
    public async Task Round_trips_a_xorb_under_every_scheme(ChunkCompressionScheme scheme)
    {
        var chunks = Chunking.Chunker.ChunkAll(TestData.SplitMix64Bytes(seed: 7, count: 400_000));
        var serialized = new ArrayBufferWriter<byte>();
        foreach (var chunk in chunks)
        {
            XorbSerializer.SerializeChunk(chunk, scheme, serialized);
        }

        var restored = XorbSerializer.Deserialize(serialized.WrittenSpan);

        await Assert.That(restored.Count).IsEqualTo(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            await Assert.That(restored[i]).IsEquivalentTo(chunks[i], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Round_trips_the_reference_chunks_through_our_own_serializer()
    {
        var original = XorbSerializer.Deserialize(ReferenceFiles.Read("ev-population.xorb.first-10-chunks"));

        var reserialized = new ArrayBufferWriter<byte>();
        XorbSerializer.Serialize(original.Select(chunk => (ReadOnlyMemory<byte>)chunk), reserialized);
        var restored = XorbSerializer.Deserialize(reserialized.WrittenSpan);

        for (var i = 0; i < original.Count; i++)
        {
            await Assert.That(restored[i]).IsEquivalentTo(original[i], CollectionOrdering.Matching);
        }
    }

    /// <summary>Compression that does not pay for itself must give way to storing the chunk as-is.</summary>
    [Test]
    public async Task Stores_incompressible_data_uncompressed()
    {
        var incompressible = TestData.SplitMix64Bytes(seed: 42, count: 65_536);
        var serialized = new ArrayBufferWriter<byte>();

        XorbSerializer.SerializeChunk(incompressible, serialized);

        XorbChunkHeader.TryRead(serialized.WrittenSpan, out var header);
        await Assert.That(header.CompressionScheme).IsEqualTo(ChunkCompressionScheme.None);
        await Assert.That(header.CompressedSize).IsEqualTo(incompressible.Length);
        await Assert.That(XorbSerializer.Deserialize(serialized.WrittenSpan).Single()).IsEquivalentTo(incompressible, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Compresses_repetitive_data()
    {
        var repetitive = new byte[100_000];
        Array.Fill(repetitive, (byte)'x');
        var serialized = new ArrayBufferWriter<byte>();

        XorbSerializer.SerializeChunk(repetitive, serialized);

        XorbChunkHeader.TryRead(serialized.WrittenSpan, out var header);
        await Assert.That(header.CompressionScheme).IsNotEqualTo(ChunkCompressionScheme.None);
        await Assert.That(header.CompressedSize).IsLessThan(1000);
        await Assert.That(XorbSerializer.Deserialize(serialized.WrittenSpan).Single()).IsEquivalentTo(repetitive, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The reference xorb ends with four bytes the spec never mentions, so a reader has to stop at
    /// the last complete record rather than assume the data ends on a record boundary.
    /// </summary>
    [Test]
    public async Task Stops_at_a_trailing_remainder_too_short_to_be_a_header()
    {
        var serialized = new ArrayBufferWriter<byte>();
        XorbSerializer.SerializeChunk("hello"u8, serialized);
        serialized.Write("XETB"u8);

        var chunks = XorbSerializer.Deserialize(serialized.WrittenSpan);

        await Assert.That(chunks.Single()).IsEquivalentTo("hello"u8.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Rejects_a_record_whose_data_is_truncated()
    {
        var serialized = new ArrayBufferWriter<byte>();
        XorbSerializer.SerializeChunk(TestData.SplitMix64Bytes(seed: 1, count: 20_000), serialized);
        var truncated = serialized.WrittenSpan[..^100].ToArray();

        await Assert.That(() => _ = XorbSerializer.Deserialize(truncated)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Rejects_an_unknown_compression_scheme()
    {
        var serialized = new ArrayBufferWriter<byte>();
        XorbSerializer.SerializeChunk("hello"u8, serialized);
        var corrupted = serialized.WrittenSpan.ToArray();
        corrupted[4] = 99;

        await Assert.That(() => _ = XorbSerializer.Deserialize(corrupted)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Rejects_a_chunk_larger_than_the_protocol_maximum()
    {
        var oversized = new byte[XorbChunkHeader.MaxUncompressedSize + 1];

        await Assert.That(() => XorbSerializer.SerializeChunk(oversized, new ArrayBufferWriter<byte>()))
            .Throws<ArgumentOutOfRangeException>();
    }
}
