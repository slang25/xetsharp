using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using XetSharp.Hub;
using XetSharp.Upload;

namespace XetSharp.Server.Tests;

/// <summary>
/// The Hub facade: the endpoints that are not in the Xet spec but without which no client can
/// find the CAS. Shapes here are pinned to what the official clients read.
/// </summary>
public class HubFacadeTests
{
    private static readonly XetRepository Repository = XetRepository.Dataset("acme/data");

    [Test]
    public async Task Token_endpoint_answers_in_the_body_and_in_the_headers()
    {
        await using var host = await TestXetServer.StartAsync();
        using var response = await host.HttpClient.GetAsync("/api/datasets/acme/data/xet-read-token/main");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("accessToken").GetString()!;
        await Assert.That(token).StartsWith("xet_");
        await Assert.That(json.RootElement.GetProperty("casUrl").GetString()).IsEqualTo(host.BaseUrl.ToString().TrimEnd('/'));
        await Assert.That(json.RootElement.GetProperty("exp").GetInt64()).IsGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await Assert.That(response.Headers.GetValues("X-Xet-Access-Token").Single()).IsEqualTo(token);
        await Assert.That(response.Headers.GetValues("X-Xet-Cas-Url").Single()).IsEqualTo(host.BaseUrl.ToString().TrimEnd('/'));
        await Assert.That(long.Parse(response.Headers.GetValues("X-Xet-Token-Expiration").Single())).IsEqualTo(json.RootElement.GetProperty("exp").GetInt64());
    }

    [Test]
    public async Task Configured_hub_tokens_gate_writes_when_anonymous_writes_are_off()
    {
        await using var host = await TestXetServer.StartAsync(new XetServerOptions { AllowAnonymousWrites = false, HubTokens = ["hf_secret"] });

        using var anonymous = await host.HttpClient.GetAsync("/api/models/acme/scratch/xet-write-token/main");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/models/acme/scratch/xet-write-token/main");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "hf_wrong");
        using var refused = await host.HttpClient.SendAsync(wrong);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        var right = new HttpRequestMessage(HttpMethod.Get, "/api/models/acme/scratch/xet-write-token/main");
        right.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "hf_secret");
        using var granted = await host.HttpClient.SendAsync(right);
        await Assert.That(granted.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Reads stay open.
        using var read = await host.HttpClient.GetAsync("/api/models/acme/scratch/xet-read-token/main");
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // And the client, given the token, uploads.
        using var client = host.CreateClient(hubToken: "hf_secret");
        await client.UploadAndCommitAsync(XetRepository.Model("acme/scratch"), [XetUploadFile.FromBytes("x.bin", TestData.SplitMix64Bytes(1, 100_000))], "Add");
    }

    [Test]
    public async Task Resolve_carries_the_headers_the_clients_read()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var content = TestData.SplitMix64Bytes(2, 300_000);
        var upload = await client.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("dir/data.bin", content)], "Add");

        using var response = await host.HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/datasets/acme/data/resolve/main/dir/data.bin"));
        await Assert.That((int)response.StatusCode).IsEqualTo(302);

        var file = upload.Files.Single();
        await Assert.That(response.Headers.GetValues("X-Xet-Hash").Single()).IsEqualTo(file.FileId.ToString());
        await Assert.That(response.Headers.GetValues("X-Linked-Size").Single()).IsEqualTo(content.Length.ToString());
        await Assert.That(response.Headers.GetValues("X-Linked-Etag").Single()).IsEqualTo($"\"{file.Sha256}\"");
        await Assert.That(response.Headers.ETag!.Tag).IsEqualTo($"\"{file.Sha256}\"");
        await Assert.That(response.Headers.GetValues("X-Repo-Commit").Single()).IsEqualTo(upload.Commit!.CommitOid);
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo($"{host.BaseUrl}files/{file.FileId}");

        var link = response.Headers.GetValues("Link").Single();
        await Assert.That(link).Contains($"/api/datasets/acme/data/xet-read-token/{upload.Commit.CommitOid}>; rel=\"xet-auth\"");
        await Assert.That(link).Contains($"/v1/reconstructions/{file.FileId}>; rel=\"xet-reconstruction-info\"");

        // The commit the headers name works as a revision, which is how a client pins a download.
        using var pinned = await host.HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/datasets/acme/data/resolve/{upload.Commit.CommitOid}/dir/data.bin"));
        await Assert.That((int)pinned.StatusCode).IsEqualTo(302);

        using var missing = await host.HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/datasets/acme/data/resolve/main/nope.bin"));
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        using var noRepo = await host.HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/acme/nothing/resolve/main/data.bin"));
        await Assert.That(noRepo.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Commits_build_on_each_other_and_can_delete()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var one = TestData.SplitMix64Bytes(3, 100_000);
        var two = TestData.SplitMix64Bytes(4, 100_000);

        var first = await client.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("one.bin", one)], "One");
        var second = await client.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("two.bin", two)], "Two");
        await Assert.That(second.Commit!.CommitOid).IsNotEqualTo(first.Commit!.CommitOid);

        using var info = await host.HttpClient.GetAsync("/api/datasets/acme/data");
        using var json = JsonDocument.Parse(await info.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("sha").GetString()).IsEqualTo(second.Commit.CommitOid);
        await Assert.That(json.RootElement.GetProperty("siblings").EnumerateArray().Select(sibling => sibling.GetProperty("rfilename").GetString()!))
            .IsEquivalentTo(["one.bin", "two.bin"], CollectionOrdering.Matching);

        await client.CommitAsync(Repository, new XetCommitRequest { Summary = "Remove one", DeletedFiles = ["one.bin"] });
        using var gone = await host.HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/datasets/acme/data/resolve/main/one.bin"));
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The earlier commit still resolves what it had.
        using var historic = await host.HttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/datasets/acme/data/resolve/{first.Commit.CommitOid}/one.bin"));
        await Assert.That((int)historic.StatusCode).IsEqualTo(302);
    }

    [Test]
    public async Task Commit_refuses_a_file_that_was_not_uploaded()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();

        Func<Task> attempt = () => client.CommitAsync(Repository, new XetCommitRequest
        {
            Summary = "Phantom",
            Files = [new XetCommitFile("ghost.bin", new string('a', 64), 123)],
        });

        var exception = await Assert.That(attempt).Throws<XetApiException>();
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(exception.Message).Contains("has not been uploaded");
    }

    [Test]
    public async Task Commit_refuses_a_stale_parent()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var first = await client.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("a.bin", TestData.SplitMix64Bytes(5, 50_000))], "A");
        await client.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("b.bin", TestData.SplitMix64Bytes(6, 50_000))], "B");

        Func<Task> attempt = () => client.CommitAsync(Repository, new XetCommitRequest
        {
            Summary = "Stale",
            DeletedFiles = ["a.bin"],
            ParentCommit = first.Commit!.CommitOid,
        });

        var exception = await Assert.That(attempt).Throws<XetApiException>();
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
    }

    /// <summary>
    /// A small file sent inline in a commit — the official client's route for anything the Hub
    /// calls a regular file — is chunked and stored here, so it downloads like every other file.
    /// </summary>
    [Test]
    public async Task Inline_files_in_a_commit_become_xet_files()
    {
        await using var host = await TestXetServer.StartAsync();
        var content = Encoding.UTF8.GetBytes("# A readme\n\nSmall enough to travel inline.\n");
        var body = $$$"""
            {"key":"header","value":{"summary":"Add readme","description":""}}
            {"key":"file","value":{"path":"README.md","content":"{{{Convert.ToBase64String(content)}}}","encoding":"base64"}}
            """;
        using var commit = await host.HttpClient.PostAsync("/api/models/acme/docs/commit/main", new StringContent(body, Encoding.UTF8, "application/x-ndjson"));
        await Assert.That(commit.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var client = host.CreateClient();
        using var downloaded = new MemoryStream();
        await client.DownloadAsync(XetRepository.Model("acme/docs"), "README.md", downloaded);
        await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Preupload_routes_everything_through_xet()
    {
        await using var host = await TestXetServer.StartAsync();
        var body = """{"files":[{"path":"model.safetensors","sample":"AAAA","size":1234},{"path":"README.md","sample":"IyBI","size":12}]}""";
        using var response = await host.HttpClient.PostAsync("/api/models/acme/scratch/preupload/main", new StringContent(body, Encoding.UTF8, "application/json"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var files = json.RootElement.GetProperty("files").EnumerateArray().ToList();
        await Assert.That(files.Count).IsEqualTo(2);
        await Assert.That(files.All(file => file.GetProperty("uploadMode").GetString() == "lfs")).IsTrue();
        await Assert.That(files.All(file => !file.GetProperty("shouldIgnore").GetBoolean())).IsTrue();
    }

    [Test]
    public async Task Public_base_url_overrides_what_the_request_came_in_on()
    {
        await using var host = await TestXetServer.StartAsync(new XetServerOptions { PublicBaseUrl = new Uri("https://xet.example.com/") });
        using var response = await host.HttpClient.GetAsync("/api/models/acme/scratch/xet-read-token/main");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("casUrl").GetString()).IsEqualTo("https://xet.example.com");
    }
}
