using System.Net;
using System.Security.Cryptography;
using XetSharp.Chunking;
using XetSharp.Hashing;
using XetSharp.Hub;
using XetSharp.Server.Storage;
using XetSharp.Upload;

namespace XetSharp.Server.Tests;

/// <summary>
/// The real client against the real server: what goes up must come down, through every layer on
/// both sides. These are the tests that would catch the server and the client disagreeing about
/// the protocol, which is the only kind of bug a server has.
/// </summary>
public class RoundTripTests
{
    private static readonly XetRepository Repository = XetRepository.Model("acme/scratch");

    [Test]
    public async Task Uploads_and_downloads_a_file()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var content = TestData.SplitMix64Bytes(1, 900_000);

        var upload = await client.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)], "Add data");

        await Assert.That(upload.Files.Single().Size).IsEqualTo(content.Length);
        await Assert.That(upload.Commit?.CommitOid).IsNotNull();

        using var downloaded = new MemoryStream();
        var download = await client.DownloadAsync(Repository, "data.bin", downloaded);

        await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
        await Assert.That(download.FileHash).IsEqualTo(upload.Files.Single().FileId);
        await Assert.That(download.Sha256).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    [Test]
    public async Task Round_trips_a_file_spread_over_several_xorbs()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient(options: new XetClientOptions { Upload = new XetUploadOptions { MaxXorbBytes = 600_000 } });
        var content = TestData.SplitMix64Bytes(2, 2_000_000);

        var upload = await client.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("big.bin", content)], "Add");
        await Assert.That(upload.XorbCount).IsGreaterThan(2);

        using var downloaded = new MemoryStream();
        await client.DownloadAsync(Repository, "big.bin", downloaded);
        await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0L, 100L)]
    [Arguments(70_000L, 90_000L)]
    [Arguments(123_456L, 654_321L)]
    [Arguments(899_990L, 899_999L)]
    [Arguments(500_000L, 5_000_000L)]
    public async Task Downloads_a_byte_range(long offset, long length)
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var content = TestData.SplitMix64Bytes(3, 900_000);
        await client.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)], "Add");

        using var downloaded = new MemoryStream();
        await client.DownloadRangeAsync(Repository, "data.bin", downloaded, offset, length);

        var expected = content.AsSpan((int)offset, (int)Math.Min(length, content.Length - offset)).ToArray();
        await Assert.That(downloaded.ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Two files sharing content in one upload: the second reuses the first's chunks, which makes
    /// its reconstruction reach into the middle of a xorb — two byte ranges in one signed URL,
    /// answered as <c>multipart/byteranges</c>.
    /// </summary>
    [Test]
    public async Task Round_trips_files_that_share_chunks()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var a = TestData.SplitMix64Bytes(10, 300_000);
        var b = TestData.SplitMix64Bytes(11, 300_000);
        var c = TestData.SplitMix64Bytes(12, 300_000);
        byte[] abc = [.. a, .. b, .. c];
        byte[] ac = [.. a, .. c];

        var upload = await client.UploadAndCommitAsync(
            Repository,
            [XetUploadFile.FromBytes("abc.bin", abc), XetUploadFile.FromBytes("ac.bin", ac)],
            "Add");

        await Assert.That(upload.XorbCount).IsEqualTo(1);
        await Assert.That(upload.DeduplicatedBytes).IsGreaterThan(0);

        using var downloaded = new MemoryStream();
        await client.DownloadAsync(Repository, "ac.bin", downloaded);
        await Assert.That(downloaded.ToArray()).IsEquivalentTo(ac, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A second upload of the same bytes, from a fresh client that knows nothing about the first,
    /// finds them through the global-deduplication index and stores no new xorb.
    /// </summary>
    [Test]
    public async Task Deduplicates_a_second_upload_through_the_global_index()
    {
        await using var host = await TestXetServer.StartAsync();
        var content = TestData.WithEligibleChunk(20, 700_000);

        using (var first = host.CreateClient())
        {
            await first.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("one.bin", content)], "Add");
        }

        var xorbsBefore = ((Storage.InMemoryXetStore)host.Store).XorbCount;
        using var second = host.CreateClient();
        var upload = await second.UploadAndCommitAsync(XetRepository.Model("acme/other"), [XetUploadFile.FromBytes("two.bin", content)], "Add");

        // The client cannot know the chunks before its first eligible one are stored — it only
        // asks about the sampled few — so those are packed again, and everything from the first
        // hit to the end of the file is matched against the listing the server answered with.
        var chunks = Chunker.ChunkAll(content);
        var firstEligible = chunks.FindIndex(chunk => GlobalDeduplication.IsEligible(XetHashes.ChunkHash(chunk)));
        var expectedDeduplicated = chunks.Skip(firstEligible).Sum(chunk => (long)chunk.Length);

        await Assert.That(upload.DeduplicatedBytes).IsEqualTo(expectedDeduplicated);
        await Assert.That(((Storage.InMemoryXetStore)host.Store).XorbCount).IsEqualTo(xorbsBefore + (firstEligible == 0 ? 0 : 1));

        using var downloaded = new MemoryStream();
        await second.DownloadAsync(XetRepository.Model("acme/other"), "two.bin", downloaded);
        await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Uploading_the_same_file_twice_is_idempotent()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var content = TestData.SplitMix64Bytes(4, 400_000);

        var first = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);
        var second = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);

        await Assert.That(second.Files.Single().FileId).IsEqualTo(first.Files.Single().FileId);
        await Assert.That(((Storage.InMemoryXetStore)host.Store).XorbCount).IsEqualTo(1);
    }

    [Test]
    public async Task Serves_the_whole_file_over_plain_http_from_resolve_and_by_file_id()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var content = TestData.SplitMix64Bytes(5, 500_000);
        await client.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)], "Add");

        using var resolve = await host.HttpClient.GetAsync("/acme/scratch/resolve/main/data.bin");
        await Assert.That(resolve.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await resolve.Content.ReadAsByteArrayAsync()).IsEquivalentTo(content, CollectionOrdering.Matching);

        var byId = await host.HttpClient.GetByteArrayAsync($"/files/{resolve.Headers.GetValues("X-Xet-Hash").Single()}");
        await Assert.That(byId).IsEquivalentTo(content, CollectionOrdering.Matching);
    }
}
