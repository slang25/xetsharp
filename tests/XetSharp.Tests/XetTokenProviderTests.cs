using XetSharp.Hub;

namespace XetSharp.Tests;

/// <summary>
/// Tokens are short-lived and scoped to one repository and revision, so caching them correctly is
/// the difference between one Hub request per download and one per piece of work.
/// </summary>
public class XetTokenProviderTests
{
    private static readonly XetRepository Repository = XetRepository.Model("acme/model");

    [Test]
    public async Task Reuses_a_token_that_is_still_good()
    {
        var (provider, handler, _) = NewProvider(expiresInSeconds: 3600);

        var first = await provider.GetTokenAsync(Repository);
        var second = await provider.GetTokenAsync(Repository);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(first);
    }

    /// <summary>
    /// A token is replaced 30 seconds before it expires, so one never leaves with a request that
    /// might outlive it.
    /// </summary>
    [Test]
    public async Task Replaces_a_token_within_the_refresh_buffer_of_expiry()
    {
        var (provider, handler, time) = NewProvider(expiresInSeconds: 60);

        await provider.GetTokenAsync(Repository);
        time.UtcNow = time.UtcNow.AddSeconds(31);
        await provider.GetTokenAsync(Repository);

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Fetches_a_new_token_when_the_service_rejects_the_old_one()
    {
        var (provider, handler, _) = NewProvider(expiresInSeconds: 3600);

        var rejected = await provider.GetTokenAsync(Repository);
        await provider.GetTokenAsync(Repository, rejectedToken: rejected);

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
    }

    /// <summary>
    /// Several requests in flight against one token all get a 401 at once. They are all asking for
    /// the same thing — a replacement for that one token — so one Hub request must serve them all.
    /// </summary>
    [Test]
    public async Task Shares_one_refresh_between_callers_holding_the_same_rejected_token()
    {
        var (provider, handler, _) = NewProvider(expiresInSeconds: 3600);
        var rejected = await provider.GetTokenAsync(Repository);

        var replacements = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => provider.GetTokenAsync(Repository, rejectedToken: rejected).AsTask()));

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(replacements.Distinct().Count()).IsEqualTo(1);
    }

    /// <summary>Scope and revision are both part of a token's identity; neither can be shared.</summary>
    [Test]
    public async Task Keeps_tokens_for_different_scopes_and_revisions_apart()
    {
        var (provider, handler, _) = NewProvider(expiresInSeconds: 3600);

        await provider.GetTokenAsync(Repository);
        await provider.GetTokenAsync(Repository, XetTokenScope.Write);
        await provider.GetTokenAsync(Repository with { Revision = "dev" });

        await Assert.That(handler.Requests.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Shares_one_request_between_callers_asking_at_once()
    {
        var release = new TaskCompletionSource();
        var handler = new FakeHttpHandler(_ =>
        {
            release.Task.Wait(TimeSpan.FromSeconds(10));
            return FakeHttpHandler.Json(TokenBody(3600));
        });
        var provider = new XetTokenProvider(new HubClient(new HttpClient(handler), new Uri("https://hub.invalid")));

        var callers = Enumerable.Range(0, 5).Select(_ => provider.GetTokenAsync(Repository).AsTask()).ToArray();
        release.SetResult();
        await Task.WhenAll(callers);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    /// <summary>A failure must not be cached: the next caller should get a fresh attempt.</summary>
    [Test]
    public async Task Retries_after_a_failed_token_request()
    {
        var fail = true;
        var handler = new FakeHttpHandler(_ => fail
            ? FakeHttpHandler.Status(System.Net.HttpStatusCode.ServiceUnavailable)
            : FakeHttpHandler.Json(TokenBody(3600)));
        var provider = new XetTokenProvider(new HubClient(new HttpClient(handler), new Uri("https://hub.invalid")));

        await Assert.That(async () => await provider.GetTokenAsync(Repository)).Throws<XetApiException>();
        fail = false;

        await Assert.That((await provider.GetTokenAsync(Repository)).AccessToken).IsEqualTo("xet_abc");
    }

    private static (XetTokenProvider Provider, FakeHttpHandler Handler, TestTimeProvider Time) NewProvider(int expiresInSeconds)
    {
        var time = new TestTimeProvider();
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(TokenBody(expiresInSeconds, time.UtcNow)));
        var hub = new HubClient(new HttpClient(handler), new Uri("https://hub.invalid"));
        return (new XetTokenProvider(hub, time), handler, time);
    }

    private static string TokenBody(int expiresInSeconds, DateTimeOffset? now = null) =>
        $$"""
        {"accessToken":"xet_abc","exp":{{(now ?? DateTimeOffset.UtcNow).AddSeconds(expiresInSeconds).ToUnixTimeSeconds()}},"casUrl":"https://cas.invalid"}
        """;
}
