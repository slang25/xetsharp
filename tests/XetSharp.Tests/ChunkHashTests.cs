using XetSharp.Chunking;
using XetSharp.Hashing;

namespace XetSharp.Tests;

/// <summary>
/// Verifies chunk (DATA_KEY) and term-verification (VERIFICATION_KEY) hashes against vectors
/// generated from the reference keys with the reference BLAKE3 implementation
/// (see tools/vectors in a future milestone; currently generated from a blake3-only Rust harness
/// over the same SplitMix64 data used by the chunk-boundary tests).
/// </summary>
public class ChunkHashTests
{
    private static readonly string[] ExpectedChunkHashes =
    [
        "6eca1e7dadaf08cca5d82d318c800f07c2ddcec115a7e8627e5edd9605b94b8d",
        "624ea34d72a06e5d43a1b8dd10763f0d22f165992c397ed97563899b27fdd88a",
        "4411d2ec847c6e3f451a7451ff3933adb4f1c31587421b9f730b698a78313b47",
        "6342bde97433e29e0779ad33eb8040d986679040361b3cc3a06230fe60dd6c9b",
        "405253fcf15bba751adc4f507d3453273daff81ed4d8acd71c521aa1cbddc0b5",
        "4482374af7f8bebfdb5c5df0299f80128d6c58886ad7b218c562b1d74064e4cb",
        "80acc8d39c853b4b8a8c6ad7b63bf2ea68f62c2226b92f06349f92cc84d213cc",
        "a7076d7d343f711fb20fe6cd023248d8d051e8fe7d44172596cd5c7ea7edaf65",
        "44755217bbb4dadc81ea7695765230a34a2e6cb3b55f373f1de35aeba79ae92c",
        "001adc1d302d5f039278325dcbd5ec3b194f4794f1629d6f962f9f4bb78a7bff",
        "f8460a337c186f07c2e225bb287a1d3d3d686dc69d0828e99640f7d8852c5b90",
        "c9bc3da29025dc1ba562d303d815151d9a937367abb766ae842a165e8493d9fe",
        "5044339dfd65e8163bdfe642614a6be604b04d6aeacf222cf219ad287bfc5cf1",
        "163622db0fe0da93f2ef964eed4c485f3c7a9c312f8e8e8a312ab4bb8141f13e",
        "e1730534a858aa0258ad8904ef12b829b8a123a1c611250275c9ca9471e4c650",
        "1fbc6854f9185caba1e1f55393f41f83b895b18f9c99245c029025ca48f1e14b",
        "8a27b66ccf05b864b6bef6fb5c970fe894f73d2330e8e98fe7841dcdbd9e9576",
        "bc70a33e7a9ec820cac24b87023469a57bdae1bf91cc3961b95806c64a525221",
        "03e5b5f5a088269ec4b329f1e04debfac4cb54b9c0facf038f7e8e0f054be7e2",
    ];

    [Test]
    public async Task Empty_chunk_hash_matches_reference()
    {
        await Assert.That(XetHashes.ChunkHash([]).ToString())
            .IsEqualTo("e0f2cf784e7e5f10c34f84af150e9a5ff9664216debad915364d741049870f67");
    }

    [Test]
    public async Task Small_chunk_hash_matches_reference()
    {
        var data = TestData.SplitMix64Bytes(seed: 0, count: 1000);

        await Assert.That(XetHashes.ChunkHash(data).ToString())
            .IsEqualTo("83cb1074082640e86bf18ec2a232fce94677d0a25799bbeb15dfe416590e9b7c");
    }

    [Test]
    public async Task Chunking_and_hashing_1mb_end_to_end_matches_reference()
    {
        var data = TestData.SplitMix64Bytes(seed: 0, count: 1_000_000);

        var chunkHashes = Chunker.ChunkAll(data)
            .Select(chunk => XetHashes.ChunkHash(chunk))
            .ToArray();

        await Assert.That(chunkHashes.Select(h => h.ToString()).ToArray()).IsEquivalentTo(ExpectedChunkHashes, CollectionOrdering.Matching);

        var rangeHash = XetHashes.VerificationHash(chunkHashes);
        await Assert.That(rangeHash.ToString())
            .IsEqualTo("6daf15a7ed258b91f607e8f5e6723a8564292d12c8104c156f7bad9c255a0180");
    }
}
