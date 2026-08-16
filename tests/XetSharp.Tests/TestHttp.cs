using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace XetSharp.Tests;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a delegate and records what it was asked,
/// so tests can drive the whole client stack without a network.
/// </summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly Lock _gate = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>
    /// The <c>Range</c> header of each request, verbatim — the value the CDN signature covers. Read
    /// through <see cref="HttpHeaders.NonValidated"/>, because asking for the parsed value would
    /// reformat the very string under test.
    /// </summary>
    public List<string?> RangeHeaders { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Requests.Add(request);
            RangeHeaders.Add(request.Headers.NonValidated.TryGetValues("Range", out var values) ? values.ToString() : null);
        }

        // Answered off the calling thread so that concurrent requests really do overlap, the way
        // they would against a real server.
        var response = await Task.Run(() => respond(request), cancellationToken).ConfigureAwait(false);

        // Left alone when the responder set one itself, so a test can pretend the request was
        // redirected somewhere else on the way.
        response.RequestMessage ??= request;
        return response;
    }

    /// <summary>
    /// A body, as a 206 by default. <paramref name="rangeStart"/> adds the <c>Content-Range</c> a
    /// real server sends with a partial response; leaving it off is how a test plays a server that
    /// answers without one.
    /// </summary>
    public static HttpResponseMessage Bytes(byte[] body, HttpStatusCode status = HttpStatusCode.PartialContent, long? rangeStart = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        if (rangeStart is { } start)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + body.Length - 1);
        }

        return response;
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    /// <summary>Builds a <c>multipart/byteranges</c> body the way a CDN does for a multi-range GET.</summary>
    public static HttpResponseMessage Multipart(string boundary, params (long Start, long End, byte[] Body)[] parts)
    {
        var body = new List<byte>();
        foreach (var (start, end, content) in parts)
        {
            body.AddRange(Encoding.ASCII.GetBytes(
                $"--{boundary}\r\nContent-Type: application/octet-stream\r\nContent-Range: bytes {start}-{end}/999999\r\n\r\n"));
            body.AddRange(content);
            body.AddRange("\r\n"u8.ToArray());
        }

        body.AddRange(Encoding.ASCII.GetBytes($"--{boundary}--\r\n"));

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([.. body]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/byteranges")
        {
            Parameters = { new NameValueHeaderValue("boundary", boundary) },
        };

        return response;
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> whose clock the test sets and whose timers fire straight away, so
/// backoff and token expiry can be exercised without any real waiting.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();

    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every delay asked for, in order.</summary>
    public List<TimeSpan> Delays { get; } = [];

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            Delays.Add(dueTime);
        }

        return new ImmediateTimer(callback, state);
    }

    private sealed class ImmediateTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ImmediateTimer(TimerCallback callback, object? state)
        {
            (_callback, _state) = (callback, state);
            Fire();
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Fire();
            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // Queued rather than invoked inline: the caller is still inside CreateTimer and has not yet
        // stored the timer it is about to wait on.
        private void Fire() => ThreadPool.QueueUserWorkItem(_ => _callback(_state));
    }
}
