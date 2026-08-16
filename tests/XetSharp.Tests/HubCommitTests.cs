using System.Net;
using System.Text;
using System.Text.Json;
using XetSharp.Hub;

namespace XetSharp.Tests;

/// <summary>
/// The Hub commit that publishes an upload. It is the Hub's own API rather than part of the Xet
/// protocol — Xet stores the bytes, and the repository's Git history still has to be told — so what
/// is pinned here is the body's shape: newline-delimited <c>{ key, value }</c> envelopes.
/// </summary>
public class HubCommitTests
{
    private static readonly XetRepository Repository = XetRepository.Dataset("acme/scratch", "refs/pr/1");

    [Test]
    public async Task Sends_one_ndjson_line_per_file_after_a_header()
    {
        var (client, handler, bodies) = NewClient();

        await client.CommitAsync(Repository, new XetCommitRequest
        {
            Summary = "Add weights",
            Description = "Two shards of them",
            Files = [new XetCommitFile("weights/a.safetensors", new string('a', 64), 1234)],
            DeletedFiles = ["old.bin"],
        });

        var lines = LinesOf(bodies);
        await Assert.That(lines.Length).IsEqualTo(3);

        await Assert.That(Value(lines[0], "key")).IsEqualTo("header");
        await Assert.That(Value(lines[0], "value", "summary")).IsEqualTo("Add weights");
        await Assert.That(Value(lines[0], "value", "description")).IsEqualTo("Two shards of them");

        await Assert.That(Value(lines[1], "key")).IsEqualTo("lfsFile");
        await Assert.That(Value(lines[1], "value", "path")).IsEqualTo("weights/a.safetensors");
        await Assert.That(Value(lines[1], "value", "algo")).IsEqualTo("sha256");
        await Assert.That(Value(lines[1], "value", "oid")).IsEqualTo(new string('a', 64));
        await Assert.That(Value(lines[1], "value", "size")).IsEqualTo("1234");

        await Assert.That(Value(lines[2], "key")).IsEqualTo("deletedFile");
        await Assert.That(Value(lines[2], "value", "path")).IsEqualTo("old.bin");
    }

    /// <summary>
    /// The revision goes in as one path segment. A pull-request ref carries slashes, which as extra
    /// segments would address a repository that does not exist.
    /// </summary>
    [Test]
    public async Task Posts_to_the_commit_endpoint_for_the_revision()
    {
        var (client, handler, _) = NewClient();

        await client.CommitAsync(Repository, Minimal);

        await Assert.That(handler.Requests[^1].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Requests[^1].RequestUri!.PathAndQuery)
            .IsEqualTo("/api/datasets/acme/scratch/commit/refs%2Fpr%2F1");
        await Assert.That(handler.Requests[^1].Content!.Headers.ContentType!.MediaType).IsEqualTo("application/x-ndjson");
    }

    [Test]
    public async Task Asks_for_a_pull_request_when_told_to()
    {
        var (client, handler, _) = NewClient();

        await client.CommitAsync(Repository, Minimal with { CreatePullRequest = true });

        await Assert.That(handler.Requests[^1].RequestUri!.Query).IsEqualTo("?create_pr=1");
    }

    /// <summary>
    /// A parent commit is how a caller refuses to clobber a branch that has moved on, so it has to
    /// reach the Hub — and be absent when it was not asked for.
    /// </summary>
    [Test]
    public async Task Names_the_parent_commit_only_when_one_is_given()
    {
        var (client, _, bodies) = NewClient();

        await client.CommitAsync(Repository, Minimal with { ParentCommit = "deadbeef" });
        await client.CommitAsync(Repository, Minimal);

        var withParent = LinesOf(bodies, 0);
        var without = LinesOf(bodies, 1);
        await Assert.That(Value(withParent[0], "value", "parentCommit")).IsEqualTo("deadbeef");
        await Assert.That(without[0].GetProperty("value").TryGetProperty("parentCommit", out _)).IsFalse();
    }

    [Test]
    public async Task Reads_the_commit_back()
    {
        var (client, _, _) = NewClient(FakeHttpHandler.Json(
            """{"commitOid":"abc123","commitUrl":"https://hub.invalid/acme/scratch/commit/abc123","pullRequestUrl":null}"""));

        var commit = await client.CommitAsync(Repository, Minimal);

        await Assert.That(commit.CommitOid).IsEqualTo("abc123");
        await Assert.That(commit.CommitUrl).IsEqualTo(new Uri("https://hub.invalid/acme/scratch/commit/abc123"));
        await Assert.That(commit.PullRequestUrl).IsNull();
    }

    [Test]
    public async Task Explains_a_commit_the_token_is_not_allowed_to_make()
    {
        var (client, _, _) = NewClient(FakeHttpHandler.Status(HttpStatusCode.Forbidden));

        var exception = await Assert.That(async () => await client.CommitAsync(Repository, Minimal)).Throws<XetApiException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Rejects_a_commit_that_would_change_nothing()
    {
        var (client, _, _) = NewClient();

        await Assert.That(async () => await client.CommitAsync(Repository, new XetCommitRequest { Summary = "Nothing" }))
            .Throws<ArgumentException>();
    }

    private static XetCommitRequest Minimal => new()
    {
        Summary = "Add a file",
        Files = [new XetCommitFile("a.bin", new string('b', 64), 1)],
    };

    private static JsonElement[] LinesOf(List<byte[]> bodies, int? index = null) =>
    [
        .. Encoding.UTF8.GetString(bodies[index ?? (bodies.Count - 1)])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement),
    ];

    private static string Value(JsonElement line, params string[] path)
    {
        var element = line;
        foreach (var name in path)
        {
            element = element.GetProperty(name);
        }

        return element.ToString();
    }

    /// <summary>
    /// Request bodies are captured while the request is in flight: the client disposes the message
    /// it sent, and with it the content, before a test could read it back off the recorded request.
    /// </summary>
    private static (HubClient Client, FakeHttpHandler Handler, List<byte[]> Bodies) NewClient(HttpResponseMessage? response = null)
    {
        List<byte[]> bodies = [];
        var handler = new FakeHttpHandler(request =>
        {
            bodies.Add(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            return response ?? FakeHttpHandler.Json("""{"commitOid":"abc123"}""");
        });

        return (new HubClient(new HttpClient(handler), new Uri("https://hub.invalid"), "hf_test"), handler, bodies);
    }
}
