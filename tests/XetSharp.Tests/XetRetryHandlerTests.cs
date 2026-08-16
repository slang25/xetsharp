using System.Net;
using XetSharp.Http;

namespace XetSharp.Tests;

/// <summary>
/// The protocol splits failures into "send it again" and "you asked for the wrong thing". Retrying
/// the second kind wastes a rate-limit budget the service tells us to assume is there.
/// </summary>
public class XetRetryHandlerTests
{
    [Test]
    [Arguments(HttpStatusCode.TooManyRequests)]
    [Arguments(HttpStatusCode.InternalServerError)]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    [Arguments(HttpStatusCode.GatewayTimeout)]
    public async Task Retries_a_transient_status_until_it_succeeds(HttpStatusCode status)
    {
        var attempts = 0;
        var handler = new FakeHttpHandler(_ => ++attempts < 3 ? FakeHttpHandler.Status(status) : FakeHttpHandler.Json("{}"));

        var (response, time) = await SendAsync(handler);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(attempts).IsEqualTo(3);
        await Assert.That(time.Delays.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(HttpStatusCode.BadRequest)]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.Forbidden)]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.RequestedRangeNotSatisfiable)]
    public async Task Returns_a_permanent_failure_at_once(HttpStatusCode status)
    {
        var attempts = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            attempts++;
            return FakeHttpHandler.Status(status);
        });

        var (response, _) = await SendAsync(handler);

        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Gives_up_after_the_attempt_limit_and_returns_the_last_response()
    {
        var attempts = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            attempts++;
            return FakeHttpHandler.Status(HttpStatusCode.ServiceUnavailable);
        });

        var (response, _) = await SendAsync(handler, maxAttempts: 3);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(attempts).IsEqualTo(3);
    }

    [Test]
    public async Task Retries_a_connection_failure()
    {
        var attempts = 0;
        var handler = new FakeHttpHandler(_ => ++attempts < 2
            ? throw new HttpRequestException("connection reset")
            : FakeHttpHandler.Json("{}"));

        var (response, _) = await SendAsync(handler);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task Rethrows_a_connection_failure_that_never_clears()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("connection reset"));
        using var client = new HttpClient(new XetRetryHandler(handler, new TestTimeProvider()) { MaxAttempts = 2 });

        await Assert.That(async () => await client.GetAsync("https://example.invalid/")).Throws<HttpRequestException>();
    }

    /// <summary>A server that says how long to wait is worth listening to.</summary>
    [Test]
    public async Task Waits_as_long_as_retry_after_asks()
    {
        var attempts = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            if (++attempts > 1)
            {
                return FakeHttpHandler.Json("{}");
            }

            var response = FakeHttpHandler.Status(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });

        var (_, time) = await SendAsync(handler);

        await Assert.That(time.Delays.Single()).IsEqualTo(TimeSpan.FromSeconds(7));
    }

    /// <summary>A body that can only be read once cannot be sent again, however transient the failure.</summary>
    [Test]
    public async Task Does_not_retry_a_request_whose_body_cannot_be_replayed()
    {
        var attempts = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            attempts++;
            return FakeHttpHandler.Status(HttpStatusCode.ServiceUnavailable);
        });
        using var client = new HttpClient(new XetRetryHandler(handler, new TestTimeProvider()));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/")
        {
            Content = new StreamContent(new MemoryStream([1, 2, 3])),
        };
        using var response = await client.SendAsync(request);

        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Retries_a_request_with_a_replayable_body()
    {
        var attempts = 0;
        var handler = new FakeHttpHandler(_ => ++attempts < 2
            ? FakeHttpHandler.Status(HttpStatusCode.ServiceUnavailable)
            : FakeHttpHandler.Json("{}"));
        using var client = new HttpClient(new XetRetryHandler(handler, new TestTimeProvider()));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/")
        {
            Content = new ByteArrayContent([1, 2, 3]),
        };
        using var response = await client.SendAsync(request);

        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task<(HttpResponseMessage Response, TestTimeProvider Time)> SendAsync(
        FakeHttpHandler handler,
        int maxAttempts = 4)
    {
        var time = new TestTimeProvider();
        using var client = new HttpClient(new XetRetryHandler(handler, time) { MaxAttempts = maxAttempts });
        return (await client.GetAsync("https://example.invalid/"), time);
    }
}
