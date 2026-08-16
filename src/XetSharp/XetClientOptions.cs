using XetSharp.Download;
using XetSharp.Hub;
using XetSharp.Upload;

namespace XetSharp;

/// <summary>How a <see cref="XetClient"/> should behave. Every value has a working default.</summary>
public sealed record XetClientOptions
{
    /// <summary>The Hub to resolve files and tokens against.</summary>
    public Uri HubUrl { get; init; } = HubClient.DefaultHubUrl;

    /// <summary>
    /// The Hugging Face access token. When null, one is looked up the way the official tools do
    /// (see <see cref="HuggingFaceCredentials.ResolveToken"/>) unless
    /// <see cref="UseAmbientCredentials"/> says otherwise. Public repositories need no token at all.
    /// </summary>
    public string? HubToken { get; init; }

    /// <summary>Whether to fall back to the environment's Hugging Face token when none is given.</summary>
    public bool UseAmbientCredentials { get; init; } = true;

    public XetDownloadOptions Download { get; init; } = XetDownloadOptions.Default;

    public XetUploadOptions Upload { get; init; } = XetUploadOptions.Default;

    /// <summary>
    /// An HttpClient to use instead of the one the client builds for itself — the hook for
    /// <c>IHttpClientFactory</c> users. It MUST be configured with
    /// <c>AllowAutoRedirect = false</c>, or resolving a file path will lose the Xet headers.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>How long any single HTTP request may take. Ignored when <see cref="HttpClient"/> is set.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Injected for tests; defaults to <see cref="System.TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }
}
