namespace XetSharp.Tests;

public class MerkleHashTests
{
    [Test]
    public async Task RoundTrips_through_hex()
    {
        const string hex = "aa02fe646d15a4b6c1eee6b3e1ccaa10e8e2e63b4ac1798e6bebbd4e5b1f6e33";

        var hash = MerkleHash.Parse(hex);

        await Assert.That(hash.ToString()).IsEqualTo(hex);
    }

    [Test]
    public async Task RoundTrips_through_bytes()
    {
        var bytes = Enumerable.Range(0, MerkleHash.Size).Select(i => (byte)i).ToArray();

        var hash = new MerkleHash(bytes);

        await Assert.That(hash.ToByteArray()).IsEquivalentTo(bytes);
    }

    [Test]
    public async Task Hex_form_reverses_bytes_within_each_8_byte_group()
    {
        // Worked example from the spec's hash-to-string procedure: bytes 0x00..0x1f
        // https://huggingface.co/docs/xet/main/en/api
        var bytes = Enumerable.Range(0, MerkleHash.Size).Select(i => (byte)i).ToArray();

        var hash = new MerkleHash(bytes);

        await Assert.That(hash.ToString())
            .IsEqualTo("07060504030201000f0e0d0c0b0a090817161514131211101f1e1d1c1b1a1918");
        await Assert.That(MerkleHash.Parse(hash.ToString())).IsEqualTo(hash);
    }

    [Test]
    public async Task Hex_form_matches_reference_implementation_vector()
    {
        // Ported from xet-core's data_hash.rs test_hash_hex_string_endianness.
        byte[] raw =
        [
            22, 175, 58, 132, 4, 75, 131, 214, 190, 153, 138, 66, 226, 3, 153, 242, 204, 86, 80, 234, 249, 153, 80, 99,
            159, 80, 65, 138, 236, 231, 149, 78,
        ];
        const string expected = "d6834b04843aaf16f29903e2428a99be635099f9ea5056cc4e95e7ec8a41509f";

        var hash = new MerkleHash(raw);

        await Assert.That(hash.ToString()).IsEqualTo(expected);
        await Assert.That(MerkleHash.Parse(expected)).IsEqualTo(hash);
    }

    [Test]
    public async Task Equality_compares_by_value()
    {
        const string hex = "aa02fe646d15a4b6c1eee6b3e1ccaa10e8e2e63b4ac1798e6bebbd4e5b1f6e33";

        await Assert.That(MerkleHash.Parse(hex)).IsEqualTo(MerkleHash.Parse(hex));
        await Assert.That(MerkleHash.Parse(hex)).IsNotEqualTo(MerkleHash.Zero);
    }
}
