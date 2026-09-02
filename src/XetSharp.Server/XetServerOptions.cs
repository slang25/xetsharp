namespace XetSharp.Server;

/// <summary>How a Xet server behaves. Every value has a default that suits a local run.</summary>
public sealed record XetServerOptions
{
    /// <summary>
    /// The URL clients reach this server at, used in the CAS URL a token hands out and in the
    /// signed xorb URLs a reconstruction hands out. Null derives it from each request's scheme and
    /// host, which is right when nothing sits between the client and the server.
    /// </summary>
    public Uri? PublicBaseUrl { get; init; }

    /// <summary>
    /// The key tokens and signed URLs are HMAC'd with. Null generates one at startup, which is fine
    /// for a single process and wrong for anything that scales out or restarts under clients: a
    /// token minted by one instance would be refused by the next.
    /// </summary>
    public byte[]? SigningKey { get; init; }

    /// <summary>How long a Xet token minted by the Hub facade lasts.</summary>
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long the signed xorb URLs in a reconstruction last.</summary>
    public TimeSpan DownloadUrlLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a global-deduplication response says its xorbs may be deduplicated against.
    /// Nothing here expires xorbs, so this is advice to the client rather than a promise kept.
    /// </summary>
    public TimeSpan DeduplicationShardLifetime { get; init; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Hub tokens that grant read and write access to every repository. A request presenting one
    /// of these as a bearer token is trusted; what happens to any other request is decided by the
    /// two flags below.
    /// </summary>
    public IReadOnlyCollection<string> HubTokens { get; init; } = [];

    /// <summary>
    /// Whether a request without a recognised Hub token may read. On by default, matching the
    /// public Hub, which issues read tokens for public repositories to anyone.
    /// </summary>
    public bool AllowAnonymousReads { get; init; } = true;

    /// <summary>
    /// Whether a request without a recognised Hub token may write. On by default because a dev
    /// server that refuses uploads until it is configured is a dev server nobody uses; turn it off
    /// and set <see cref="HubTokens"/> for anything reachable by strangers.
    /// </summary>
    public bool AllowAnonymousWrites { get; init; } = true;

    /// <summary>
    /// The repository revision a Hub commit lands on when the client names one that does not
    /// exist yet. The public Hub creates repositories explicitly; this server creates them on
    /// first commit so a test needs no setup step.
    /// </summary>
    public bool CreateRepositoriesOnCommit { get; init; } = true;

    /// <summary>Injected for tests; defaults to <see cref="System.TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
