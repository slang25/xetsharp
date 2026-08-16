using XetSharp.Chunking;

namespace XetSharp.Tests;

/// <summary>
/// Verifies chunk boundaries against expected values from the xet-core reference implementation
/// (xet_data/src/deduplication/chunking.rs), which are explicitly published for porting.
/// </summary>
public class ChunkerTests
{
    [Test]
    public async Task Random_1mb_data_chunks_at_reference_boundaries()
    {
        var data = TestData.SplitMix64Bytes(seed: 0, count: 1_000_000);

        // Sanity-check the generated input itself, as the reference test does.
        await Assert.That(data[0]).IsEqualTo((byte)175);
        await Assert.That(data[127]).IsEqualTo((byte)132);
        await Assert.That(data[111111]).IsEqualTo((byte)118);

        var boundaries = TestData.Boundaries(Chunker.ChunkAll(data));

        await Assert.That(boundaries).IsEquivalentTo(
        [
            84493, 134421, 144853, 243318, 271793, 336457, 467529, 494581, 582000, 596735, 616815, 653164, 678202,
            724510, 815591, 827760, 958832, 991092, 1000000,
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Constant_1mb_data_chunks_at_maximum_chunk_size()
    {
        var data = new byte[1_000_000];
        Array.Fill(data, (byte)59);

        var boundaries = TestData.Boundaries(Chunker.ChunkAll(data));

        await Assert.That(boundaries).IsEquivalentTo(
            [131072, 262144, 393216, 524288, 655360, 786432, 917504, 1_000_000], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(1)]
    [Arguments(37)]
    [Arguments(255)]
    public async Task Streaming_in_any_block_size_produces_identical_boundaries(int blockSize)
    {
        var data = TestData.SplitMix64Bytes(seed: 1, count: 256_000);
        var reference = TestData.Boundaries(Chunker.ChunkAll(data));

        var chunker = new Chunker();
        var chunks = new List<byte[]>();
        for (var position = 0; position < data.Length; position += blockSize)
        {
            var end = Math.Min(position + blockSize, data.Length);
            chunker.NextBlock(data.AsSpan(position, end - position), isFinal: end == data.Length, chunks);
        }

        await Assert.That(TestData.Boundaries(chunks)).IsEquivalentTo(reference, CollectionOrdering.Matching);
        await Assert.That(chunks.SelectMany(c => c).ToArray()).IsEquivalentTo(data, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Data_below_minimum_chunk_size_is_a_single_chunk()
    {
        var data = TestData.SplitMix64Bytes(seed: 7, count: 63);

        var chunks = Chunker.ChunkAll(data);

        await Assert.That(chunks).Count().IsEqualTo(1);
        await Assert.That(chunks[0]).IsEquivalentTo(data, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Empty_data_produces_no_chunks()
    {
        await Assert.That(Chunker.ChunkAll([])).IsEmpty();
    }

    /// <summary>
    /// The shipping chunker skips ahead over the bytes below the minimum chunk size that cannot hold
    /// a boundary, and stops scanning where the maximum size forces one; neither shortcut may move a
    /// boundary by so much as one byte. Checked against <see cref="NaiveChunker"/>, which does
    /// exactly what the specification says and nothing else, over data shapes the published vectors
    /// do not reach: long runs, sparse triggers, sizes that fall awkwardly against both limits.
    /// </summary>
    [Test]
    [Arguments(1_000_003)]
    [Arguments(300_007)]
    [Arguments(131_073)]
    [Arguments(8_191)]
    [Arguments(65)]
    [Arguments(3)]
    public async Task Chunks_where_the_specification_says_to(int size)
    {
        foreach (var data in new[]
                 {
                     TestData.SplitMix64Bytes(seed: 99, count: size),
                     TestData.TriggeringPatternData(size, padding: 3),
                     Repeated(size, 0x5A),
                 })
        {
            var lengths = Chunker.ChunkAll(data).Select(chunk => chunk.Length).ToList();

            await Assert.That(lengths).IsEquivalentTo(NaiveChunker.ChunkLengths(data), CollectionOrdering.Matching);
        }
    }

    /// <summary>
    /// The same check where the chunker is fed in awkward slices, so the state a boundary depends on
    /// — the rolling hash, and how much of the current chunk is already buffered — has to survive
    /// block splits that land anywhere, including inside the skip-ahead prefix.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(7)]
    [Arguments(4093)]
    public async Task Chunks_where_the_specification_says_to_when_streamed(int blockSize)
    {
        var data = TestData.SplitMix64Bytes(seed: 100, count: 400_009);
        var chunker = new Chunker();
        var chunks = new List<byte[]>();

        for (var position = 0; position < data.Length; position += blockSize)
        {
            var end = Math.Min(position + blockSize, data.Length);
            chunker.NextBlock(data.AsSpan(position, end - position), isFinal: end == data.Length, chunks);
        }

        await Assert.That(chunks.Select(chunk => chunk.Length).ToList())
            .IsEquivalentTo(NaiveChunker.ChunkLengths(data), CollectionOrdering.Matching);
    }

    private static byte[] Repeated(int size, byte value)
    {
        var data = new byte[size];
        Array.Fill(data, value);
        return data;
    }

    [Test]
    [MethodDataSource(typeof(TriggeringPatternVectors), nameof(TriggeringPatternVectors.Cases))]
    public async Task Triggering_pattern_chunks_at_reference_boundaries(TriggeringPatternVectors.Case testCase)
    {
        var data = TestData.TriggeringPatternData(count: 65536, padding: testCase.Padding);

        await Assert.That(data[11111]).IsEqualTo(testCase.SampleAt11111);

        var boundaries = TestData.Boundaries(Chunker.ChunkAll(data));

        await Assert.That(boundaries).IsEquivalentTo(testCase.Boundaries, CollectionOrdering.Matching);
    }
}
