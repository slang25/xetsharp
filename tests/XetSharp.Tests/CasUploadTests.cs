using System.Net;
using System.Text;
using XetSharp.Cas;
using XetSharp.Hub;
using XetSharp.Shards;

namespace XetSharp.Tests;

/// <summary>
/// The upload endpoints of the CAS API, and in particular the streaming shard upload — whose HTTP
/// status turns 200 the moment the stream opens, so the only thing that says whether the upload
/// worked is the event it ends on.
/// </summary>
public class CasUploadTests
{
    private static readonly XetRepository Repository = XetRepository.Model("acme/scratch");

    private static readonly MerkleHash XorbHash = MerkleHash.Parse(ReferenceFiles.XorbHash);

    private static readonly byte[] Shard = ReferenceFiles.Read("ev-population.csv.shard.verification-no-footer");

    [Test]
    [Arguments("""{"was_inserted":true}""", true)]
    [Arguments("""{"was_inserted":false}""", false)]
    public async Task Reports_whether_a_xorb_was_new(string body, bool expected)
    {
        var (client, handler) = NewClient(_ => FakeHttpHandler.Json(body));

        var inserted = await client.UploadXorbAsync(Repository, XorbHash, new byte[] { 1, 2, 3 });

        await Assert.That(inserted).IsEqualTo(expected);
        await Assert.That(handler.Requests[^1].RequestUri!.AbsolutePath).IsEqualTo($"/v1/xorbs/default/{XorbHash}");
        await Assert.That(handler.Requests[^1].Method).IsEqualTo(HttpMethod.Post);
    }

    /// <summary>
    /// The status already said the xorb is stored. A body that cannot be read tells us only whether
    /// it was stored just now, which is not worth failing an upload over.
    /// </summary>
    [Test]
    public async Task Treats_an_unreadable_xorb_response_as_success()
    {
        var (client, _) = NewClient(_ => FakeHttpHandler.Json("not json at all"));

        await Assert.That(await client.UploadXorbAsync(Repository, XorbHash, new byte[] { 1 })).IsTrue();
    }

    /// <summary>A read token on a write endpoint is the mistake the 403 exists to name.</summary>
    [Test]
    public async Task Explains_a_forbidden_upload()
    {
        var (client, _) = NewClient(_ => FakeHttpHandler.Status(HttpStatusCode.Forbidden));

        var exception = await Assert.That(async () => await client.UploadXorbAsync(Repository, XorbHash, new byte[] { 1 }))
            .Throws<XetApiException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(exception.Message).Contains("write");
        await Assert.That(exception.IsRetryable).IsFalse();
    }

    [Test]
    public async Task Rejects_a_xorb_over_the_size_limit()
    {
        var (client, handler) = NewClient(_ => FakeHttpHandler.Json("""{"was_inserted":true}"""));

        await Assert.That(async () =>
                await client.UploadXorbAsync(Repository, XorbHash, new byte[Xorbs.XorbSerializer.MaxSerializedSize + 1]))
            .Throws<ArgumentException>();

        await Assert.That(handler.Requests).IsEmpty();
    }

    /// <summary>
    /// The shard endpoint has a limit of its own, and a caller who is over it should hear so before
    /// copying and sending 64 MiB for the service to refuse.
    /// </summary>
    [Test]
    public async Task Rejects_a_shard_over_the_size_limit()
    {
        var (client, handler) = NewClient(_ => FakeHttpHandler.Json("""{"result":1}"""));

        await Assert.That(async () => await client.UploadShardAsync(Repository, new byte[MdbShard.MaxUploadSize + 1]))
            .Throws<ArgumentException>();

        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    [Arguments(1, true)]
    [Arguments(0, false)]
    public async Task Reads_the_outcome_off_the_terminal_event(int result, bool expected)
    {
        var (client, _) = NewClient(_ => Ndjson(
            """{"type":"validating","verified":0,"total":796}""",
            """{"type":"validating","verified":796,"total":796}""",
            """{"type":"committing","stage":"uploading"}""",
            """{"type":"committing","stage":"syncing"}""",
            $$"""{"type":"result","result":{{result}}}"""));

        await Assert.That(await client.UploadShardAsync(Repository, Shard)).IsEqualTo(expected);
    }

    /// <summary>
    /// A failure arrives as an event on a 200 response. Treating the status as the answer would
    /// report a shard as registered when the service had just refused it.
    /// </summary>
    [Test]
    public async Task Fails_on_an_error_event_despite_the_success_status()
    {
        var (client, _) = NewClient(_ => Ndjson(
            """{"type":"validating","verified":0,"total":796}""",
            """{"type":"error","message":"xorb eea25d6e is missing"}"""));

        var exception = await Assert.That(async () => await client.UploadShardAsync(Repository, Shard)).Throws<XetException>();

        await Assert.That(exception!.Message).Contains("xorb eea25d6e is missing");
    }

    /// <summary>A stream that simply stops has told us nothing, which is not the same as success.</summary>
    [Test]
    public async Task Fails_when_the_stream_ends_without_a_terminal_event()
    {
        var (client, _) = NewClient(_ => Ndjson("""{"type":"committing","stage":"syncing"}"""));

        var exception = await Assert.That(async () => await client.UploadShardAsync(Repository, Shard)).Throws<XetException>();

        await Assert.That(exception!.Message).Contains("without a result or error event");
    }

    /// <summary>An event type this client has never heard of is not something to guess at.</summary>
    [Test]
    public async Task Fails_on_an_unknown_event_type()
    {
        var (client, _) = NewClient(_ => Ndjson("""{"type":"reticulating","splines":3}"""));

        await Assert.That(async () => await client.UploadShardAsync(Repository, Shard)).Throws<XetException>();
    }

    /// <summary>
    /// Where the streaming endpoint is missing its predecessor answers instead — and, as with the
    /// reconstruction endpoints, that deployment is not probed again.
    /// </summary>
    [Test]
    public async Task Falls_back_to_the_v1_shard_endpoint_and_stops_probing()
    {
        var (client, handler) = NewClient(request => request.RequestUri!.AbsolutePath == "/v2/shards"
            ? FakeHttpHandler.Status(HttpStatusCode.NotFound)
            : FakeHttpHandler.Json("""{"result":1}"""));

        await Assert.That(await client.UploadShardAsync(Repository, Shard)).IsTrue();
        await Assert.That(await client.UploadShardAsync(Repository, Shard)).IsTrue();

        await Assert.That(handler.Requests.Select(request => request.RequestUri!.AbsolutePath))
            .IsEquivalentTo(["/v2/shards", "/v1/shards", "/v1/shards"], CollectionOrdering.Matching);
    }

    /// <summary>A chunk the global index has never seen is an ordinary answer, not an error.</summary>
    [Test]
    public async Task Reports_a_chunk_the_global_index_does_not_know()
    {
        var (client, _) = NewClient(_ => FakeHttpHandler.Status(HttpStatusCode.NotFound));

        await Assert.That(await client.QueryChunkDeduplicationAsync(Repository, XorbHash)).IsNull();
    }

    [Test]
    public async Task Parses_a_global_deduplication_response()
    {
        var dedupe = ReferenceFiles.Read("ev-population.csv.shard.dedupe");
        var (client, handler) = NewClient(_ => FakeHttpHandler.Bytes(dedupe, HttpStatusCode.OK));

        var shard = await client.QueryChunkDeduplicationAsync(Repository, ReferenceFiles.Chunks[0].Hash);

        await Assert.That(shard!.Footer!.IsHmacProtected).IsTrue();
        await Assert.That(shard.TryFindChunk(ReferenceFiles.Chunks[0].Hash, out _, out _)).IsTrue();
        await Assert.That(handler.Requests[^1].RequestUri!.AbsolutePath)
            .IsEqualTo($"/v1/chunks/default-merkledb/{ReferenceFiles.Chunks[0].Hash}");
    }

    private static HttpResponseMessage Ndjson(params string[] lines) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Join('\n', lines) + "\n", Encoding.UTF8, "application/x-ndjson"),
        };

    private static (CasClient Client, FakeHttpHandler Handler) NewClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHttpHandler(respond);
        return (new CasClient(new HttpClient(handler), new StubTokenSource()), handler);
    }

    private sealed class StubTokenSource : IXetTokenSource
    {
        public ValueTask<XetToken> GetTokenAsync(
            XetRepository repository,
            XetTokenScope scope = XetTokenScope.Read,
            XetToken? rejectedToken = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new XetToken("xet_test", DateTimeOffset.UtcNow.AddHours(1), new Uri("https://cas.invalid")));
    }
}
