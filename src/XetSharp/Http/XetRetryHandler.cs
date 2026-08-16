using System.Net;

namespace XetSharp.Http;

/// <summary>
/// Retries the failures the protocol calls transient — 429 and 5xx responses, plus connection
/// errors and timeouts — with exponential backoff and full jitter, honouring <c>Retry-After</c>
/// when the server sends one. Statuses that mean the request itself was wrong (400, 401, 403, 404,
/// 416) are returned to the caller untouched: retrying them would only repeat the mistake.
/// </summary>
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

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

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

            var delay = RetryAfter(response) ?? BackoffFor(attempt);
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
        // A cancelled HttpClient request surfaces as TaskCanceledException whether the caller
        // cancelled or the request timed out; only the latter is worth retrying.
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

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay > MaxDelay ? MaxDelay : delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
