namespace XetSharp.Hub;

/// <summary>Supplies CAS tokens, from wherever the caller wants them to come from.</summary>
public interface IXetTokenSource
{
    /// <param name="forceRefresh">
    /// Set when the CAS API rejected the current token: it discards whatever is cached rather than
    /// handing the same rejected token back.
    /// </param>
    ValueTask<XetToken> GetTokenAsync(
        XetRepository repository,
        XetTokenScope scope,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Caches Xet tokens per repository and scope, refreshing them <see cref="XetToken.RefreshBuffer"/>
/// before they expire. Concurrent callers waiting on the same token share one Hub request.
/// </summary>
public sealed class XetTokenProvider(HubClient hubClient, TimeProvider? timeProvider = null) : IXetTokenSource
{
    private readonly HubClient _hubClient = hubClient ?? throw new ArgumentNullException(nameof(hubClient));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<(XetRepository Repository, XetTokenScope Scope), Task<XetToken>> _cache = [];
    private readonly Lock _gate = new();

    public async ValueTask<XetToken> GetTokenAsync(
        XetRepository repository,
        XetTokenScope scope = XetTokenScope.Read,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var key = (repository, scope);
        var now = _timeProvider.GetUtcNow();

        Task<XetToken> request;
        lock (_gate)
        {
            if (!forceRefresh && _cache.TryGetValue(key, out var cached) && IsUsable(cached, now))
            {
                request = cached;
            }
            else
            {
                // Deliberately not passing the caller's token: the request is shared, so one caller
                // walking away must not cancel it for everyone else waiting on the same token.
                request = _hubClient.GetTokenAsync(repository, scope, CancellationToken.None);
                _cache[key] = request;
            }
        }

        try
        {
            return await request.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var cached) && ReferenceEquals(cached, request) && !IsUsable(cached, _timeProvider.GetUtcNow()))
                {
                    _cache.Remove(key);
                }
            }

            throw;
        }
    }

    private static bool IsUsable(Task<XetToken> request, DateTimeOffset now) =>
        !request.IsCompleted || (request.IsCompletedSuccessfully && !request.Result.IsExpired(now));
}
