using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using XetSharp.Hub;
using XetSharp.Json;
using XetSharp.Shards;
using XetSharp.Xorbs;

namespace XetSharp.Cas;

/// <summary>
/// A typed client for the CAS API. It owns the bearer-token dance — acquiring a token for the
/// repository, and refreshing once when the service rejects one — so callers only deal in
/// repositories and file IDs.
/// </summary>
public sealed class CasClient
{
    /// <summary>The only prefix the xorb endpoints accept.</summary>
    private const string XorbPrefix = "default";

    /// <summary>The only prefix the global-deduplication endpoint accepts.</summary>
    private const string DedupePrefix = "default-merkledb";

    private static readonly MediaTypeHeaderValue OctetStream = new("application/octet-stream");

    private readonly HttpClient _httpClient;
    private readonly IXetTokenSource _tokenSource;

    /// <summary>
    /// The CAS origins observed not to serve <c>/v2/reconstructions</c>, so later files skip straight
    /// to v1 instead of paying for the probe every time. Kept per origin because a token source can
    /// hand out URLs for more than one deployment, and what one of them supports says nothing about
    /// the others.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _reconstructionV2Unavailable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same record for <c>/v2/shards</c>, which has its own <c>/v1</c> predecessor.</summary>
    private readonly ConcurrentDictionary<string, bool> _shardUploadV2Unavailable = new(StringComparer.OrdinalIgnoreCase);

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
                    await EnsureSuccessAsync(response, $"Reconstruction of {fileId}", cancellationToken).ConfigureAwait(false);
                    return FileReconstruction.Parse(await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        var (fallback, fallbackToken) = await SendAsync(repository, ReconstructionRequest("v1", fileId, range), cancellationToken)
            .ConfigureAwait(false);
        using (fallback)
        {
            await EnsureSuccessAsync(fallback, $"Reconstruction of {fileId}", cancellationToken).ConfigureAwait(false);
            _reconstructionV2Unavailable[OriginOf(fallbackToken.CasUrl)] = true;
            return FileReconstruction.Parse(await ReadBodyAsync(fallback, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Asks whether the CAS service already knows <paramref name="chunkHash"/>, returning the shard
    /// it answers with — a listing of the xorbs holding that chunk and its neighbours, which an
    /// upload can dedupe against. Returns null when the chunk is not in the global index, which is
    /// an ordinary answer rather than an error.
    /// </summary>
    /// <remarks>
    /// The chunk hashes in the returned shard are usually HMAC-protected; match against them with
    /// <see cref="MdbShard.TryFindChunk"/>, which applies the footer's key.
    /// </remarks>
    public Task<MdbShard?> QueryChunkDeduplicationAsync(
        XetRepository repository,
        MerkleHash chunkHash,
        CancellationToken cancellationToken = default) =>
        QueryChunkDeduplicationAsync(repository, chunkHash, XetTokenScope.Read, cancellationToken);

    /// <summary>
    /// The same query against a token of a given scope. An upload asks with its write token: the
    /// endpoint only needs read scope, but write supersedes read, and asking for a second token
    /// mid-upload would be a Hub round trip for nothing.
    /// </summary>
    internal async Task<MdbShard?> QueryChunkDeduplicationAsync(
        XetRepository repository,
        MerkleHash chunkHash,
        XetTokenScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var (response, _) = await SendAsync(
            repository,
            token => new HttpRequestMessage(HttpMethod.Get, new Uri(token.CasUrl, $"/v1/chunks/{DedupePrefix}/{chunkHash}")),
            cancellationToken,
            scope).ConfigureAwait(false);

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response, $"Global-deduplication query for chunk {chunkHash}", cancellationToken)
                .ConfigureAwait(false);
            return MdbShard.Parse(await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Uploads a serialized xorb. Returns whether the service stored it: false means it already had
    /// this xorb, which content addressing makes a success rather than a conflict.
    /// </summary>
    /// <remarks>Requires a write-scope token, and so a Hub token with write access to the repository.</remarks>
    public async Task<bool> UploadXorbAsync(
        XetRepository repository,
        MerkleHash xorbHash,
        ReadOnlyMemory<byte> serializedXorb,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (serializedXorb.Length > XorbSerializer.MaxSerializedSize)
        {
            throw new ArgumentException(
                $"A xorb of {serializedXorb.Length} bytes is over the {XorbSerializer.MaxSerializedSize}-byte limit the CAS service accepts.",
                nameof(serializedXorb));
        }

        var (response, _) = await SendAsync(
            repository,
            token => Post(new Uri(token.CasUrl, $"/v1/xorbs/{XorbPrefix}/{xorbHash}"), serializedXorb),
            cancellationToken,
            XetTokenScope.Write).ConfigureAwait(false);

        using (response)
        {
            await EnsureSuccessAsync(response, $"Upload of xorb {xorbHash}", cancellationToken).ConfigureAwait(false);
            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

            // The status already said the xorb is stored; was_inserted only distinguishes storing it
            // now from having stored it before, so a body we cannot read is not worth failing over.
            return TryDeserialize(body, XetJsonContext.Default.UploadXorbResponseJson)?.WasInserted ?? true;
        }
    }

    /// <summary>
    /// Uploads a shard, registering the files it describes. Every xorb the shard references MUST
    /// already be uploaded; the service rejects the shard with a 400 otherwise.
    /// </summary>
    /// <returns>
    /// Whether this call registered the shard, as opposed to finding it already present. Both are
    /// success; the distinction is informational.
    /// </returns>
    /// <remarks>
    /// Prefers the streaming <c>/v2/shards</c> endpoint, whose HTTP status turns 200 the moment the
    /// stream opens — so success or failure is carried by the terminal event, not the status code —
    /// and falls back to <c>/v1/shards</c> on deployments that do not serve it.
    /// </remarks>
    public async Task<bool> UploadShardAsync(
        XetRepository repository,
        ReadOnlyMemory<byte> serializedShard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var token = await _tokenSource.GetTokenAsync(repository, XetTokenScope.Write, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!_shardUploadV2Unavailable.ContainsKey(OriginOf(token.CasUrl)))
        {
            var (response, _) = await SendAsync(
                repository,
                current => Post(new Uri(current.CasUrl, "/v2/shards"), serializedShard),
                cancellationToken,
                XetTokenScope.Write).ConfigureAwait(false);

            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.NotImplemented))
                {
                    await EnsureSuccessAsync(response, "Shard upload", cancellationToken).ConfigureAwait(false);
                    return await ReadShardUploadStreamAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var (fallback, fallbackToken) = await SendAsync(
            repository,
            current => Post(new Uri(current.CasUrl, "/v1/shards"), serializedShard),
            cancellationToken,
            XetTokenScope.Write).ConfigureAwait(false);

        using (fallback)
        {
            await EnsureSuccessAsync(fallback, "Shard upload", cancellationToken).ConfigureAwait(false);
            _shardUploadV2Unavailable[OriginOf(fallbackToken.CasUrl)] = true;

            var body = await ReadBodyAsync(fallback, cancellationToken).ConfigureAwait(false);
            return TryDeserialize(body, XetJsonContext.Default.UploadShardResponseJson)?.Result != 0;
        }
    }

    /// <summary>
    /// Reads a <c>/v2/shards</c> event stream to its terminal event. The 200 that opened the stream
    /// says nothing about the outcome, so anything short of a <c>result</c> event is a failure —
    /// including a stream that simply stops.
    /// </summary>
    private static async Task<bool> ReadShardUploadStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ShardUploadEventJson? shardEvent;
                try
                {
                    shardEvent = JsonSerializer.Deserialize(line, XetJsonContext.Default.ShardUploadEventJson);
                }
                catch (JsonException exception)
                {
                    throw new XetException($"The shard upload stream carried a line that is not valid JSON: {Excerpt(line)}", exception);
                }

                switch (shardEvent?.Type)
                {
                    case "result":
                        return shardEvent!.Result != 0;
                    case "error":
                        throw new XetException($"The shard upload failed: {shardEvent!.Message ?? "the service gave no reason"}.");
                    case null or "validating" or "committing":
                        // Progress and heartbeats; the outcome only arrives with a terminal event.
                        break;
                    default:
                        throw new XetException($"The shard upload stream carried an unknown event type '{shardEvent.Type}'.");
                }
            }
        }

        throw new XetException("The shard upload stream ended without a result or error event, so the outcome is unknown.");
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

    /// <summary>A POST carrying a binary body, resendable so the retry handler can repeat it.</summary>
    private static HttpRequestMessage Post(Uri uri, ReadOnlyMemory<byte> body)
    {
        var content = new ByteArrayContent(body.ToArray());
        content.Headers.ContentType = OctetStream;
        return new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = response.StatusCode switch
        {
            HttpStatusCode.BadRequest =>
                "the request was rejected as malformed — for a shard upload this also means a referenced xorb is missing, or the shard is too large",
            HttpStatusCode.Unauthorized => "the Xet token was rejected",
            HttpStatusCode.Forbidden => "the Xet token's scope is too narrow; uploads need a write token, which needs a Hub token with write access",
            HttpStatusCode.NotFound => "the CAS service has no such resource",
            HttpStatusCode.RequestedRangeNotSatisfiable => "the requested range starts past the end of the file",
            _ => "the CAS service returned an error",
        };

        var body = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
        throw new XetApiException(
            $"{what} failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}.{body}",
            response.StatusCode,
            response.RequestMessage?.RequestUri);
    }

    /// <summary>
    /// Parses a response body that only carries information, not the outcome. A service that
    /// answers 200 with something unparseable has still done what was asked.
    /// </summary>
    private static T? TryDeserialize<T>(ReadOnlySpan<byte> body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Excerpt(string text) => text.Length <= 200 ? text : text[..200] + "…";

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
