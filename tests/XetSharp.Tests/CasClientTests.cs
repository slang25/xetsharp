using System.Net;
using XetSharp.Cas;
using XetSharp.Hub;

namespace XetSharp.Tests;

/// <summary>
/// The CAS client probes for <c>/v2/reconstructions</c> and remembers what it finds. What it
/// remembers belongs to the deployment that answered: one token source can hand out URLs for
/// several of them, and they need not be running the same version of the API.
/// </summary>
public class CasClientTests
{
    private const string EmptyReconstruction = """{"offset_into_first_range":0,"terms":[],"xorbs":{}}""";

    private static readonly MerkleHash FileId = MerkleHash.Parse(new string('a', 64));

    private static readonly XetRepository OnV1Only = XetRepository.Model("acme/old");

    private static readonly XetRepository OnV2 = XetRepository.Model("acme/new");

    [Test]
    public async Task Falls_back_to_v1_and_stops_probing_that_deployment()
    {
        var (client, handler) = NewClient();

        await client.GetReconstructionAsync(OnV1Only, FileId);
        await client.GetReconstructionAsync(OnV1Only, FileId);

        await Assert.That(Paths(handler)).IsEquivalentTo(
            [$"/v2/reconstructions/{FileId}", $"/v1/reconstructions/{FileId}", $"/v1/reconstructions/{FileId}"],
            CollectionOrdering.Matching);
    }

    /// <summary>
    /// A deployment that predates v2 says nothing about the one next to it, which may have no v1
    /// route at all — so the probe has to be per deployment, not per client.
    /// </summary>
    [Test]
    public async Task Keeps_probing_a_deployment_that_has_not_answered_yet()
    {
        var (client, handler) = NewClient();

        await client.GetReconstructionAsync(OnV1Only, FileId);
        await client.GetReconstructionAsync(OnV2, FileId);

        await Assert.That(handler.Requests
                .Where(request => request.RequestUri!.Host == "cas-new.invalid")
                .Select(request => request.RequestUri!.AbsolutePath))
            .IsEquivalentTo([$"/v2/reconstructions/{FileId}"], CollectionOrdering.Matching);
    }

    private static (CasClient Client, FakeHttpHandler Handler) NewClient()
    {
        // The old deployment serves only v1; the new one serves v2 and has no v1 route at all.
        var handler = new FakeHttpHandler(request =>
        {
            var url = request.RequestUri!;
            var version = url.AbsolutePath.Split('/')[1];
            var served = url.Host == "cas-old.invalid" ? "v1" : "v2";
            return version == served ? FakeHttpHandler.Json(EmptyReconstruction) : FakeHttpHandler.Status(HttpStatusCode.NotFound);
        });

        return (new CasClient(new HttpClient(handler), new StubTokenSource()), handler);
    }

    private static IEnumerable<string> Paths(FakeHttpHandler handler) =>
        handler.Requests.Select(request => request.RequestUri!.AbsolutePath);

    /// <summary>Hands out a token per repository, pointing at whichever deployment hosts it.</summary>
    private sealed class StubTokenSource : IXetTokenSource
    {
        public ValueTask<XetToken> GetTokenAsync(
            XetRepository repository,
            XetTokenScope scope = XetTokenScope.Read,
            XetToken? rejectedToken = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new XetToken(
                "xet_test",
                DateTimeOffset.UtcNow.AddHours(1),
                new Uri(repository == OnV1Only ? "https://cas-old.invalid" : "https://cas-new.invalid")));
    }
}
