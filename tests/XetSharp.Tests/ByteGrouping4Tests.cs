using XetSharp.Xorbs;

namespace XetSharp.Tests;

public class ByteGrouping4Tests
{
    /// <summary>
    /// The worked example from the spec: <c>[A1,A2,A3,A4, B1,B2,B3,B4, C1,C2,C3,C4]</c> regroups to
    /// <c>[A1,B1,C1, A2,B2,C2, A3,B3,C3, A4,B4,C4]</c>.
    /// </summary>
    [Test]
    public async Task Groups_bytes_by_position_within_each_four_byte_group()
    {
        // Bytes 0..11 stand in for A1..C4: byte 4g + p is group g's byte at position p.
        var source = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        var grouped = new byte[source.Length];

        ByteGrouping4.Group(source, grouped);

        await Assert.That(grouped).IsEquivalentTo(new byte[] { 0, 4, 8, 1, 5, 9, 2, 6, 10, 3, 7, 11 }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A trailing partial group continues the pattern, so its 1–3 bytes land one each in the first
    /// groups — making those groups a byte longer than the rest.
    /// </summary>
    [Test]
    public async Task Distributes_a_trailing_partial_group_across_the_first_groups()
    {
        var source = Enumerable.Range(0, 14).Select(i => (byte)i).ToArray();
        var grouped = new byte[source.Length];

        ByteGrouping4.Group(source, grouped);

        // Group sizes are 4, 4, 3, 3: indices 12 and 13 extend the first two groups.
        await Assert.That(grouped).IsEquivalentTo(new byte[] { 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 3, 7, 11 }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(13)]
    [Arguments(4096)]
    [Arguments(131072)]
    public async Task Ungroup_inverts_group(int length)
    {
        var source = TestData.SplitMix64Bytes(seed: (ulong)length, count: length);
        var grouped = new byte[length];
        var restored = new byte[length];

        ByteGrouping4.Group(source, grouped);
        ByteGrouping4.Ungroup(grouped, restored);

        await Assert.That(restored).IsEquivalentTo(source, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Byte-grouped float data compresses better than the same bytes ungrouped — the reason the
    /// scheme exists. Little-endian floats over a narrow range share their high bytes, which
    /// grouping gathers into long runs.
    /// </summary>
    [Test]
    public async Task Beats_plain_lz4_on_column_like_float_data()
    {
        var floats = new byte[4 * 8192];
        for (var i = 0; i < 8192; i++)
        {
            BitConverter.TryWriteBytes(floats.AsSpan(i * 4), 1.0f + (i % 97) * 0.0001f);
        }

        var lz4 = new System.Buffers.ArrayBufferWriter<byte>();
        XorbSerializer.SerializeChunk(floats, ChunkCompressionScheme.Lz4, lz4);

        var byteGrouped = new System.Buffers.ArrayBufferWriter<byte>();
        XorbSerializer.SerializeChunk(floats, ChunkCompressionScheme.ByteGrouping4Lz4, byteGrouped);

        await Assert.That(byteGrouped.WrittenCount).IsLessThan(lz4.WrittenCount);
    }
}
