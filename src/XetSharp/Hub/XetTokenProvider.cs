using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XetSharp.Diagnostics;

namespace XetSharp.Hub;

/// <summary>Supplies CAS tokens, from wherever the caller wants them to come from.</summary>
public interface IXetTokenSource
{
    /// <param name="rejectedToken">
    /// The token the CAS API just rejected, if this call is a retry. Whatever is cached is replaced
    /// rather than handed back — unless it has already been replaced, in which case the caller gets
    /// the replacement instead of minting yet another token.
    /// </param>
    ValueTask<XetToken> GetTokenAsync(
        XetRepository repository,
        XetTokenScope scope,
        XetToken? rejectedToken = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Caches Xet tokens per repository and scope, refreshing them <see cref="XetToken.RefreshBuffer"/>
/// before they expire. Concurrent callers waiting on the same token share one Hub request — including
/// the callers whose requests were all rejected with the same spent token.
/// </summary>
public sealed class XetTokenProvider(HubClient hubClient, TimeProvider? timeProvider = null, ILogger? logger = null) : IXetTokenSource
{
    private readonly HubClient _hubClient = hubClient ?? throw new ArgumentNullException(nameof(hubClient));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Dictionary<(XetRepository Repository, XetTokenScope Scope), Task<XetToken>> _cache = [];
    private readonly Lock _gate = new();

    public async ValueTask<XetToken> GetTokenAsync(
        XetRepository repository,
        XetTokenScope scope = XetTokenScope.Read,
        XetToken? rejectedToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var key = (repository, scope);
        var now = _timeProvider.GetUtcNow();

        Task<XetToken> request;
        var minting = false;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && IsUsable(cached, now) && !HandedOut(cached, rejectedToken))
            {
                request = cached;
            }
            else
            {
                if (rejectedToken is not null)
                {
                    _logger.TokenRejected(scope, repository);
                }

                _logger.MintingToken(scope, repository);
                minting = true;

                // Deliberately not passing the caller's token: the request is shared, so one caller
                // walking away must not cancel it for everyone else waiting on the same token.
                request = _hubClient.GetTokenAsync(repository, scope, CancellationToken.None);
                _cache[key] = request;
            }
        }

        try
        {
            var token = await request.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Only the caller that started the request reports it, so a hundred waiters sharing one
            // Hub round trip do not write a hundred lines about it.
            if (minting)
            {
                _logger.MintedToken(scope, repository, token.CasUrl, token.ExpiresAt);
            }

            return token;
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

    /// <summary>
    /// Whether the cached request is the one that produced a token the service has now rejected.
    /// Identity is the instance handed out, not the value: a replacement minted for an unexpired
    /// token can be byte-for-byte identical to it, and would otherwise look rejected in its turn. A
    /// refresh already in flight is by definition a later one, so callers holding the rejected token
    /// join it rather than starting another.
    /// </summary>
    private static bool HandedOut(Task<XetToken> request, XetToken? rejectedToken) =>
        rejectedToken is not null && request.IsCompletedSuccessfully && ReferenceEquals(request.Result, rejectedToken);
}
