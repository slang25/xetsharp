using System.Net;
using XetSharp.Hub;

namespace XetSharp.Tests;

public class HubClientTests
{
    private const string TokenBody = """{"accessToken":"xet_abc","exp":1848535668,"casUrl":"https://cas-server.xethub.hf.co"}""";

    [Test]
    [Arguments(XetRepositoryType.Model, "openai-community/gpt2", "main", "/api/models/openai-community/gpt2/xet-read-token/main")]
    [Arguments(XetRepositoryType.Dataset, "xet-team/xet-spec-reference-files", "v1.1", "/api/datasets/xet-team/xet-spec-reference-files/xet-read-token/v1.1")]
    [Arguments(XetRepositoryType.Space, "jsulz/ready-xet-go", "main", "/api/spaces/jsulz/ready-xet-go/xet-read-token/main")]
    public async Task Builds_the_token_url_for_each_repository_type(
        XetRepositoryType type,
        string id,
        string revision,
        string expectedPath)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(TokenBody));

        var token = await NewClient(handler).GetTokenAsync(new XetRepository(type, id, revision));

        await Assert.That(handler.Requests.Single().RequestUri!.AbsolutePath).IsEqualTo(expectedPath);
        await Assert.That(token.AccessToken).IsEqualTo("xet_abc");
        await Assert.That(token.CasUrl).IsEqualTo(new Uri("https://cas-server.xethub.hf.co"));
        await Assert.That(token.ExpiresAt).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1848535668));
    }

    /// <summary>A branch name with slashes is one path segment, not several.</summary>
    [Test]
    public async Task Encodes_a_revision_that_contains_slashes()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(TokenBody));

        await NewClient(handler).GetTokenAsync(XetRepository.Model("acme/model", "refs/pr/1"), XetTokenScope.Write);

        await Assert.That(handler.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("https://hub.invalid/api/models/acme/model/xet-write-token/refs%2Fpr%2F1");
    }

    [Test]
    public async Task Sends_the_hub_token_when_there_is_one()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(TokenBody));

        await NewClient(handler, hubToken: "hf_secret").GetTokenAsync(XetRepository.Model("acme/model"));

        await Assert.That(handler.Requests.Single().Headers.Authorization!.ToString()).IsEqualTo("Bearer hf_secret");
    }

    /// <summary>Public repositories issue anonymous read tokens, so no token is a supported state.</summary>
    [Test]
    public async Task Sends_no_authorization_header_when_there_is_no_token()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(TokenBody));

        await NewClient(handler).GetTokenAsync(XetRepository.Model("acme/model"));

        await Assert.That(handler.Requests.Single().Headers.Authorization).IsNull();
    }

    [Test]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.Forbidden)]
    [Arguments(HttpStatusCode.NotFound)]
    public async Task Reports_a_failed_token_request_with_its_status(HttpStatusCode status)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(status));

        var exception = await Assert.That(async () => await NewClient(handler).GetTokenAsync(XetRepository.Model("acme/model")))
            .Throws<XetApiException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(status);
    }

    /// <summary>
    /// The file ID travels on the headers of a redirect the client deliberately does not follow.
    /// </summary>
    [Test]
    public async Task Reads_the_file_id_from_the_resolve_redirect()
    {
        var handler = new FakeHttpHandler(_ => ResolveResponse());

        var file = await NewClient(handler).GetFileInfoAsync(
            XetRepository.Dataset("xet-team/xet-spec-reference-files"),
            "Electric_Vehicle_Population_Data_20250917.csv");

        await Assert.That(file.FileId).IsEqualTo(MerkleHash.Parse(ReferenceFiles.FileHash));
        await Assert.That(file.Size).IsEqualTo(ReferenceFiles.FileLength);
        await Assert.That(file.Sha256).IsEqualTo(ReferenceFiles.Sha256);
        await Assert.That(file.CommitSha).IsEqualTo("c4aa3a3f15b1395fff5ce934784bf6c8f2d62de8");

        var request = handler.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Head);
        await Assert.That(request.RequestUri!.AbsoluteUri).IsEqualTo(
            "https://hub.invalid/datasets/xet-team/xet-spec-reference-files/resolve/main/Electric_Vehicle_Population_Data_20250917.csv");
    }

    [Test]
    public async Task Escapes_each_segment_of_a_file_path()
    {
        var handler = new FakeHttpHandler(_ => ResolveResponse());

        await NewClient(handler).GetFileInfoAsync(XetRepository.Model("acme/model"), "sub dir/weights v2.safetensors");

        await Assert.That(handler.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("https://hub.invalid/acme/model/resolve/main/sub%20dir/weights%20v2.safetensors");
    }

    [Test]
    public async Task Reports_a_file_that_is_not_stored_on_xet()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(HttpStatusCode.OK));

        var exception = await Assert.That(async () => await NewClient(handler).GetFileInfoAsync(XetRepository.Model("acme/model"), "README.md"))
            .Throws<XetException>();

        await Assert.That(exception!.Message).Contains("not stored on Xet");
    }

    /// <summary>
    /// Following the redirect silently loses the Xet headers, which would otherwise look exactly
    /// like a file that is not on Xet — so the diagnostic names the real cause.
    /// </summary>
    [Test]
    public async Task Explains_an_http_client_that_follows_redirects()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var response = FakeHttpHandler.Status(HttpStatusCode.OK);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Head, "https://cdn.invalid/somewhere-else");
            return response;
        });

        var exception = await Assert.That(async () => await NewClient(handler).GetFileInfoAsync(XetRepository.Model("acme/model"), "model.bin"))
            .Throws<XetException>();

        await Assert.That(exception!.Message).Contains("AllowAutoRedirect");
    }

    [Test]
    public async Task Reports_a_missing_file()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(HttpStatusCode.NotFound));

        var exception = await Assert.That(async () => await NewClient(handler).GetFileInfoAsync(XetRepository.Model("acme/model"), "nope.bin"))
            .Throws<XetApiException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static HubClient NewClient(FakeHttpHandler handler, string? hubToken = null) =>
        new(new HttpClient(handler), new Uri("https://hub.invalid"), hubToken);

    /// <summary>The headers a real resolve request answers with, minus the ones nothing reads.</summary>
    private static HttpResponseMessage ResolveResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new ByteArrayContent([]) };
        response.Headers.Add("X-Xet-Hash", ReferenceFiles.FileHash);
        response.Headers.Add("X-Linked-Size", ReferenceFiles.FileLength.ToString());
        response.Headers.Add("X-Linked-Etag", $"\"{ReferenceFiles.Sha256}\"");
        response.Headers.Add("X-Repo-Commit", "c4aa3a3f15b1395fff5ce934784bf6c8f2d62de8");
        response.Headers.Location = new Uri("https://cdn.invalid/xet-bridge-us/whatever");
        return response;
    }
}
