using System.Net;

namespace XetSharp.Http;

/// <summary>
/// Retries the failures the protocol calls transient — 429 and 5xx responses, plus connection
/// errors — with exponential backoff and full jitter, honouring <c>Retry-After</c> when the server
/// sends one. Statuses that mean the request itself was wrong (400, 401, 403, 404, 416) are
/// returned to the caller untouched: retrying them would only repeat the mistake.
/// </summary>
/// <remarks>
/// <see cref="HttpClient.Timeout"/> is a budget for the whole call, retries included, and it reaches
/// a handler as the very cancellation token the handler is given. A request that runs out of that
/// budget is therefore not retried here — there is nothing left to retry within — and surfaces to
/// the caller as the <see cref="TaskCanceledException"/> HttpClient raises for a timeout.
/// </remarks>
public sealed class XetRetryHandler : DelegatingHandler
{
    private readonly TimeProvider _timeProvider;

    public XetRetryHandler(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    public XetRetryHandler(HttpMessageHandler innerHandler, TimeProvider? timeProvider = null)
        : base(innerHandler) => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Total attempts, including the first. Defaults to 4.</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>The backoff for the first retry; doubles each attempt up to <see cref="MaxDelay"/>.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The ceiling on the computed backoff. A server's <c>Retry-After</c> overrides it.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest <c>Retry-After</c> worth waiting out. A server asking for longer than this is
    /// telling us to come back later rather than to hold the call open, so its response is handed
    /// to the caller — <c>Retry-After</c> header and all — instead of being retried. Defaults to
    /// two minutes.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(2);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var isLastAttempt = attempt >= MaxAttempts || !CanResend(request);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken) && !isLastAttempt)
            {
                await DelayAsync(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (isLastAttempt || !XetApiException.IsRetryableStatus(response.StatusCode))
            {
                return response;
            }

            var retryAfter = RetryAfter(response);
            if (retryAfter > MaxRetryAfter)
            {
                return response;
            }

            var delay = retryAfter ?? BackoffFor(attempt);
            response.Dispose();
            await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a failed attempt can be repeated. A request whose body is a one-shot stream cannot:
    /// its content has already been consumed.
    /// </summary>
    private static bool CanResend(HttpRequestMessage request) => request.Content is null or ByteArrayContent;

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        // Cancellation of the token we were handed is either the caller giving up or HttpClient's
        // overall timeout running out; neither leaves anything to retry within. Cancellation from
        // anywhere else — a connect timeout inside the inner handler, say — is a transport failure.
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        HttpRequestException or IOException => true,
        _ => false,
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        return retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    private TimeSpan BackoffFor(int attempt)
    {
        var exponential = BaseDelay * Math.Pow(2, attempt - 1);
        var capped = exponential < MaxDelay ? exponential : MaxDelay;
        return capped * Random.Shared.NextDouble();
    }

    /// <summary>
    /// Waits exactly as long as asked. Callers cap what they ask for: computed backoff by
    /// <see cref="MaxDelay"/>, a server's <c>Retry-After</c> by <see cref="MaxRetryAfter"/>.
    /// </summary>
    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
