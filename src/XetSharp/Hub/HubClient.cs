using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using XetSharp.Json;

namespace XetSharp.Hub;

/// <summary>
/// The Hugging Face Hub half of the protocol: turning a repository path into a Xet file ID, and
/// trading a Hub token for a CAS token. Everything after this point talks to the CAS service.
/// </summary>
public sealed class HubClient
{
    /// <summary>The public Hub.</summary>
    public static readonly Uri DefaultHubUrl = new("https://huggingface.co");

    private readonly HttpClient _httpClient;
    private readonly Uri _hubUrl;
    private readonly string? _hubToken;

    /// <param name="httpClient">
    /// The client used for Hub requests. It MUST NOT follow redirects: the file ID travels on the
    /// headers of the resolve endpoint's 302, and following it lands on a CDN response that has
    /// none of them.
    /// </param>
    /// <param name="hubUrl">The Hub base URL; defaults to <see cref="DefaultHubUrl"/>.</param>
    /// <param name="hubToken">
    /// A Hugging Face access token, or null to make unauthenticated requests. Public repositories
    /// issue anonymous Xet read tokens, so null is enough to download from them.
    /// </param>
    public HubClient(HttpClient httpClient, Uri? hubUrl = null, string? hubToken = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _hubUrl = hubUrl ?? DefaultHubUrl;
        _hubToken = string.IsNullOrWhiteSpace(hubToken) ? null : hubToken;
    }

    /// <summary>
    /// Requests a Xet token for <paramref name="repository"/>. The token is short-lived; callers
    /// that need one repeatedly should go through <see cref="XetTokenProvider"/> instead.
    /// </summary>
    public async Task<XetToken> GetTokenAsync(
        XetRepository repository,
        XetTokenScope scope = XetTokenScope.Read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        repository.Validate();

        var scopeSegment = scope == XetTokenScope.Write ? "write" : "read";
        var uri = new Uri(
            _hubUrl,
            $"/api/{repository.ApiSegment}/{repository.Id}/xet-{scopeSegment}-token/{repository.EncodedRevision}");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddAuthorization(request);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, uri, "request a Xet token", cancellationToken).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync(XetJsonContext.Default.XetTokenJson, cancellationToken)
            .ConfigureAwait(false);

        if (payload?.AccessToken is not { Length: > 0 } accessToken || payload.CasUrl is not { Length: > 0 } casUrl)
        {
            throw new XetApiException("The Hub's token response is missing accessToken or casUrl.", response.StatusCode, uri);
        }

        return new XetToken(accessToken, DateTimeOffset.FromUnixTimeSeconds(payload.Exp), new Uri(casUrl, UriKind.Absolute));
    }

    /// <summary>
    /// Resolves a file path in a repository to its Xet file ID and size, by reading the headers of
    /// the resolve endpoint's redirect without following it.
    /// </summary>
    /// <exception cref="XetException">The file exists but is not stored on Xet.</exception>
    public async Task<XetFileInfo> GetFileInfoAsync(
        XetRepository repository,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        repository.Validate();

        var uri = ResolveUri(repository, path);

        // HEAD rather than GET: for a file that is *not* on Xet the resolve endpoint answers with
        // the file itself, and we only ever want the headers.
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        AddAuthorization(request);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && response.StatusCode is not (HttpStatusCode.Found or
            HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect or HttpStatusCode.RedirectMethod))
        {
            await EnsureSuccessAsync(response, uri, "resolve a file path", cancellationToken).ConfigureAwait(false);
        }

        if (GetHeader(response, "X-Xet-Hash") is not { } hashText)
        {
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
            {
                throw new XetException(
                    $"The resolve request for '{uri}' was redirected to '{finalUri}', so the Xet headers were lost. " +
                    "The HttpClient used for Hub requests must be configured with AllowAutoRedirect = false.");
            }

            throw new XetException(
                $"'{path}' in {repository.Id} is not stored on Xet: the resolve response carries no X-Xet-Hash header.");
        }

        if (!MerkleHash.TryParse(hashText, out var fileId))
        {
            throw new XetException($"The Hub returned an X-Xet-Hash that is not a valid file ID: '{hashText}'.");
        }

        var size = GetHeader(response, "X-Linked-Size") is { } sizeText && long.TryParse(sizeText, out var parsedSize)
            ? parsedSize
            : response.Content.Headers.ContentLength ?? -1;

        return new XetFileInfo(
            fileId,
            size,
            GetHeader(response, "X-Linked-Etag")?.Trim('"'),
            GetHeader(response, "X-Repo-Commit"));
    }

    internal Uri ResolveUri(XetRepository repository, string path)
    {
        var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_hubUrl, $"/{repository.ResolvePrefix}{repository.Id}/resolve/{repository.EncodedRevision}/{encodedPath}");
    }

    internal void AddAuthorization(HttpRequestMessage request)
    {
        if (_hubToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _hubToken);
        }
    }

    private static string? GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        Uri uri,
        string what,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " The Hub token is missing or invalid.",
            HttpStatusCode.Forbidden => " The Hub token does not carry the permissions this needs.",
            HttpStatusCode.NotFound => " The repository, revision or file does not exist.",
            _ => string.Empty,
        };

        var body = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
        throw new XetApiException(
            $"Failed to {what}: the Hub returned {(int)response.StatusCode} {response.ReasonPhrase}.{detail}{body}",
            response.StatusCode,
            uri);
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? string.Empty : $" Response: {body.Trim()[..Math.Min(body.Trim().Length, 500)]}";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }
}

/// <summary>The Hub's <c>xet-{scope}-token</c> response.</summary>
internal sealed class XetTokenJson
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("exp")]
    public long Exp { get; set; }

    [JsonPropertyName("casUrl")]
    public string? CasUrl { get; set; }
}
