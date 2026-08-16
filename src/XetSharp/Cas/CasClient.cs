using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using XetSharp.Hub;

namespace XetSharp.Cas;

/// <summary>
/// A typed client for the CAS API. It owns the bearer-token dance — acquiring a token for the
/// repository, and refreshing once when the service rejects one — so callers only deal in
/// repositories and file IDs.
/// </summary>
public sealed class CasClient
{
    private readonly HttpClient _httpClient;
    private readonly IXetTokenSource _tokenSource;

    /// <summary>
    /// The CAS origins observed not to serve <c>/v2/reconstructions</c>, so later files skip straight
    /// to v1 instead of paying for the probe every time. Kept per origin because a token source can
    /// hand out URLs for more than one deployment, and what one of them supports says nothing about
    /// the others.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _reconstructionV2Unavailable = new(StringComparer.OrdinalIgnoreCase);

    public CasClient(HttpClient httpClient, IXetTokenSource tokenSource)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenSource);
        _httpClient = httpClient;
        _tokenSource = tokenSource;
    }

    /// <summary>
    /// Asks how to rebuild a file, optionally only the part of it covered by
    /// <paramref name="range"/> (whose end is inclusive, as in HTTP).
    /// </summary>
    public async Task<FileReconstruction> GetReconstructionAsync(
        XetRepository repository,
        MerkleHash fileId,
        ByteRange? range = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        // Which deployment serves this repository is only known once a token has been minted for it,
        // and the version probe below is per deployment. Tokens are cached, so asking costs nothing.
        var token = await _tokenSource.GetTokenAsync(repository, XetTokenScope.Read, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!_reconstructionV2Unavailable.ContainsKey(OriginOf(token.CasUrl)))
        {
            var (response, _) = await SendAsync(repository, ReconstructionRequest("v2", fileId, range), cancellationToken)
                .ConfigureAwait(false);
            using (response)
            {
                // A 404 here is ambiguous: either the file is unknown or this deployment predates
                // /v2. Only the v1 request can tell the two apart.
                if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.NotImplemented))
                {
                    await EnsureReconstructionSuccessAsync(response, fileId, cancellationToken).ConfigureAwait(false);
                    return FileReconstruction.Parse(await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        var (fallback, fallbackToken) = await SendAsync(repository, ReconstructionRequest("v1", fileId, range), cancellationToken)
            .ConfigureAwait(false);
        using (fallback)
        {
            await EnsureReconstructionSuccessAsync(fallback, fileId, cancellationToken).ConfigureAwait(false);
            _reconstructionV2Unavailable[OriginOf(fallbackToken.CasUrl)] = true;
            return FileReconstruction.Parse(await ReadBodyAsync(fallback, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>The scheme, host and port a CAS URL points at — what "this deployment" means here.</summary>
    private static string OriginOf(Uri casUrl) => casUrl.GetLeftPart(UriPartial.Authority);

    private static Func<XetToken, HttpRequestMessage> ReconstructionRequest(string version, MerkleHash fileId, ByteRange? range) =>
        token =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(token.CasUrl, $"/{version}/reconstructions/{fileId}"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            if (range is { } wanted)
            {
                request.Headers.Range = new RangeHeaderValue(wanted.Start, wanted.End);
            }

            return request;
        };

    /// <summary>
    /// Sends a request with a fresh-enough token, retrying once against a newly minted token if the
    /// service says the current one is no longer good. Returns the token the response was obtained
    /// with, since it names the deployment that answered.
    /// </summary>
    private async Task<(HttpResponseMessage Response, XetToken Token)> SendAsync(
        XetRepository repository,
        Func<XetToken, HttpRequestMessage> createRequest,
        CancellationToken cancellationToken,
        XetTokenScope scope = XetTokenScope.Read)
    {
        XetToken? rejected = null;
        for (var attempt = 0; ; attempt++)
        {
            var token = await _tokenSource.GetTokenAsync(repository, scope, rejected, cancellationToken).ConfigureAwait(false);
            using var request = createRequest(token);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
            {
                return (response, token);
            }

            response.Dispose();
            rejected = token;
        }
    }

    private static async Task EnsureReconstructionSuccessAsync(
        HttpResponseMessage response,
        MerkleHash fileId,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => "the file ID is malformed",
            HttpStatusCode.Unauthorized => "the Xet token was rejected",
            HttpStatusCode.Forbidden => "the Xet token's scope is too narrow",
            HttpStatusCode.NotFound => "no such file",
            HttpStatusCode.RequestedRangeNotSatisfiable => "the requested range starts past the end of the file",
            _ => "the CAS service returned an error",
        };

        var body = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
        throw new XetApiException(
            $"Reconstruction of {fileId} failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}.{body}",
            response.StatusCode,
            response.RequestMessage?.RequestUri);
    }

    /// <summary>
    /// Reads a response body, decompressing it if the handler did not. Reconstruction responses run
    /// to megabytes for large files, so the protocol asks clients to request compression.
    /// </summary>
    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var encoding = response.Content.Headers.ContentEncoding.LastOrDefault();
            var decoded = encoding is null ? stream : Decompress(stream, encoding);
            await using (decoded.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream(
                    capacity: (int)Math.Clamp(response.Content.Headers.ContentLength ?? 64 * 1024, 4096, 8 * 1024 * 1024));
                await decoded.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                return buffer.ToArray();
            }
        }
    }

    private static Stream Decompress(Stream stream, string encoding) => encoding.ToLowerInvariant() switch
    {
        "gzip" => new GZipStream(stream, CompressionMode.Decompress),
        "deflate" => new ZLibStream(stream, CompressionMode.Decompress),
        "identity" => stream,
        _ => throw new XetException($"The CAS service replied with an unsupported content encoding '{encoding}'."),
    };

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            return body.Length == 0 ? string.Empty : $" Response: {body[..Math.Min(body.Length, 500)]}";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }
}
