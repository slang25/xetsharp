namespace XetSharp.Hub;

/// <summary>
/// What a Xet token may be used for. <see cref="Write"/> supersedes <see cref="Read"/>: only the
/// upload endpoints require it, and a write token can call every read endpoint too.
/// </summary>
public enum XetTokenScope
{
    Read,
    Write,
}

/// <summary>
/// A bearer token for the CAS API, together with the CAS endpoint it is valid against. Both come
/// from the Hub's <c>xet-{scope}-token</c> endpoint and are scoped to one repository and revision.
/// </summary>
public sealed record XetToken(string AccessToken, DateTimeOffset ExpiresAt, Uri CasUrl)
{
    /// <summary>
    /// How long before <see cref="ExpiresAt"/> a token is treated as spent. Matches the reference
    /// implementation, so a request never leaves with a token about to expire in flight.
    /// </summary>
    public static readonly TimeSpan RefreshBuffer = TimeSpan.FromSeconds(30);

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt - RefreshBuffer;
}
