using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using XetSharp.Hub;
using XetSharp.Server.Auth;
using XetSharp.Server.Diagnostics;
using XetSharp.Server.Http;
using XetSharp.Server.Storage;
using XetSharp.Shards;
using XetSharp.Xorbs;

namespace XetSharp.Server.Cas;

/// <summary>
/// The CAS API, as <c>cas.openapi.yaml</c> in xet-core lays it out, plus the signed data route a
/// CDN plays for the real service. Handlers are plain <see cref="RequestDelegate"/>s so that no
/// binding needs reflection and the whole thing compiles ahead of time.
/// </summary>
internal sealed class CasEndpoints(XetServer server)
{
    private const string XorbPrefix = "default";

    /// <summary>
    /// The spec names <c>default-merkledb</c> as the only chunk prefix; the reference client sends
    /// <c>default</c>. Refusing either would turn every one of that client's global-deduplication
    /// queries into a miss, so both are taken.
    /// </summary>
    private static readonly string[] DedupePrefixes = ["default-merkledb", "default"];

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/reconstructions/{fileId}", Guard(context => ReconstructionAsync(context, v2: false)));
        endpoints.MapGet("/v2/reconstructions/{fileId}", Guard(context => ReconstructionAsync(context, v2: true)));
        endpoints.MapPost("/v1/reconstructions", Guard(BatchReconstructionAsync));
        endpoints.MapGet("/v1/chunks/{prefix}/{hash}", Guard(DeduplicationAsync));
        endpoints.MapPost("/v1/xorbs/{prefix}/{hash}", Guard(UploadXorbAsync));
        endpoints.MapMethods("/v1/xorbs/{prefix}/{hash}", [HttpMethods.Head], Guard(HeadXorbAsync));
        endpoints.MapMethods("/v1/files/{fileId}", [HttpMethods.Head], Guard(HeadFileAsync));
        endpoints.MapPost("/v1/shards", Guard(UploadShardAsync));
        endpoints.MapPost("/v2/shards", Guard(UploadShardStreamingAsync));
        endpoints.MapPost("/v1/telemetry", Guard(TelemetryAsync));
        endpoints.MapGet(XorbUrlSigner.PathPrefix + "{hash}", Guard(ServeXorbAsync));
    }

    private RequestDelegate Guard(Func<HttpContext, Task> handler) =>
        context => Responses.GuardedAsync(context, server.Logger, handler);

    /// <summary>The token on the request, or a 401; with a wide enough scope, or a 403.</summary>
    private XetTokenClaims Authorize(HttpContext context, XetTokenScope scope)
    {
        if (!server.Tokens.TryValidate(Responses.BearerToken(context), out var claims))
        {
            throw new XetServerException(401, "A valid Xet token is required; mint one from the Hub's xet-read-token or xet-write-token endpoint.");
        }

        if (!claims.Allows(scope))
        {
            throw new XetServerException(403, "This endpoint needs a write-scope token.");
        }

        return claims;
    }

    private async Task ReconstructionAsync(HttpContext context, bool v2)
    {
        var fileId = Responses.RouteHash(context, "fileId");
        Authorize(context, XetTokenScope.Read);

        var file = await server.Store.GetFileAsync(fileId, context.RequestAborted).ConfigureAwait(false)
            ?? throw XetServerException.NotFound($"No file {fileId} is registered.");

        var range = HttpRanges.ParseSingle(context.Request.Headers.Range.ToString(), file.Size);
        var reconstruction = await server.Reconstructions
            .BuildAsync(file, range, server.BaseUrl(context.Request), oneRangePerUrl: !v2, context.RequestAborted)
            .ConfigureAwait(false);

        server.Logger.ReconstructionServed(fileId, range is { } r ? $" bytes {r.Start}-{r.End}" : string.Empty, reconstruction.Terms.Count, reconstruction.Xorbs.Count);
        await Responses.JsonAsync(context, v2 ? reconstruction.WriteV2 : reconstruction.WriteV1, compressible: true).ConfigureAwait(false);
    }

    private async Task BatchReconstructionAsync(HttpContext context)
    {
        Authorize(context, XetTokenScope.Read);
        var body = await Responses.ReadBodyAsync(context, 16 * 1024 * 1024).ConfigureAwait(false);

        var fileIds = new List<MerkleHash>();
        try
        {
            using var document = JsonDocument.Parse(body);
            foreach (var key in document.RootElement.EnumerateArray())
            {
                if (key.GetProperty("prefix").GetString() != XorbPrefix)
                {
                    throw XetServerException.BadRequest($"The only file prefix is '{XorbPrefix}'.");
                }

                var hash = key.GetProperty("hash").GetString();
                if (!MerkleHash.TryParse(hash, out var fileId))
                {
                    throw XetServerException.BadRequest($"'{hash}' is not a file ID.");
                }

                if (!fileIds.Contains(fileId))
                {
                    fileIds.Add(fileId);
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw XetServerException.BadRequest("The body must be a JSON array of { \"prefix\", \"hash\" } keys.");
        }

        var baseUrl = server.BaseUrl(context.Request);
        var reconstructions = new List<(MerkleHash FileId, Reconstruction Reconstruction)>();
        foreach (var fileId in fileIds)
        {
            var file = await server.Store.GetFileAsync(fileId, context.RequestAborted).ConfigureAwait(false)
                ?? throw XetServerException.NotFound($"No file {fileId} is registered.");
            reconstructions.Add((fileId, await server.Reconstructions
                .BuildAsync(file, null, baseUrl, oneRangePerUrl: true, context.RequestAborted)
                .ConfigureAwait(false)));
        }

        // fetch_info is shared across files and keyed by xorb; a xorb two files use lists both
        // files' fetches, which is harmless since each range says which chunks it covers.
        var fetchInfo = new Dictionary<MerkleHash, IReadOnlyList<XorbFetchPlan>>();
        foreach (var (_, reconstruction) in reconstructions)
        {
            foreach (var (xorb, fetches) in reconstruction.Xorbs)
            {
                fetchInfo[xorb] = fetchInfo.TryGetValue(xorb, out var existing) ? [.. existing, .. fetches] : fetches;
            }
        }

        await Responses.JsonAsync(
            context,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("files");
                foreach (var (fileId, reconstruction) in reconstructions)
                {
                    writer.WritePropertyName(fileId.ToString());
                    writer.WriteStartObject();
                    reconstruction.WriteTerms(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                Reconstruction.WriteFetchInfo(writer, fetchInfo);
                writer.WriteEndObject();
            },
            compressible: true).ConfigureAwait(false);
    }

    private async Task DeduplicationAsync(HttpContext context)
    {
        if (!DedupePrefixes.Contains(Responses.RouteValue(context, "prefix")))
        {
            throw XetServerException.BadRequest($"The chunk prefix must be '{DedupePrefixes[0]}'.");
        }

        var chunkHash = Responses.RouteHash(context, "hash");
        Authorize(context, XetTokenScope.Read);

        var location = await server.Store.FindChunkAsync(chunkHash, context.RequestAborted).ConfigureAwait(false);
        if (location is not { } found)
        {
            server.Logger.DeduplicationQueried(chunkHash, "not indexed");
            throw XetServerException.NotFound("The chunk is not in the global-deduplication index.");
        }

        var xorb = await server.Store.GetXorbAsync(found.Xorb, context.RequestAborted).ConfigureAwait(false)
            ?? throw XetServerException.NotFound("The chunk's xorb is no longer stored.");

        server.Logger.DeduplicationQueried(chunkHash, $"chunk {found.ChunkIndex} of xorb {found.Xorb}");
        var shard = DeduplicationShardBuilder.Build(xorb, server.DeduplicationKey, server.Options.TimeProvider.GetUtcNow(), server.Options.DeduplicationShardLifetime);
        var bytes = shard.ToByteArray();
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task UploadXorbAsync(HttpContext context)
    {
        if (Responses.RouteValue(context, "prefix") != XorbPrefix)
        {
            throw XetServerException.BadRequest($"The only xorb prefix is '{XorbPrefix}'.");
        }

        var xorbHash = Responses.RouteHash(context, "hash");
        Authorize(context, XetTokenScope.Write);

        var body = await Responses.ReadBodyAsync(context, XorbSerializer.MaxSerializedSize).ConfigureAwait(false);
        var xorb = XorbIndexer.Index(body, xorbHash);
        var inserted = await server.Store.PutXorbAsync(xorb, body, context.RequestAborted).ConfigureAwait(false);
        if (inserted)
        {
            server.Logger.XorbStored(xorbHash, body.Length, xorb.Chunks.Length);
        }
        else
        {
            server.Logger.XorbAlreadyStored(xorbHash);
        }

        await Responses.JsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("was_inserted", inserted);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private async Task HeadXorbAsync(HttpContext context)
    {
        if (Responses.RouteValue(context, "prefix") != XorbPrefix)
        {
            throw XetServerException.BadRequest($"The only xorb prefix is '{XorbPrefix}'.");
        }

        var xorbHash = Responses.RouteHash(context, "hash");
        Authorize(context, XetTokenScope.Read);
        var xorb = await server.Store.GetXorbAsync(xorbHash, context.RequestAborted).ConfigureAwait(false)
            ?? throw XetServerException.NotFound($"No xorb {xorbHash} is stored.");
        context.Response.ContentLength = xorb.SerializedLength;
    }

    private async Task HeadFileAsync(HttpContext context)
    {
        var fileId = Responses.RouteHash(context, "fileId");
        Authorize(context, XetTokenScope.Read);
        var file = await server.Store.GetFileAsync(fileId, context.RequestAborted).ConfigureAwait(false)
            ?? throw XetServerException.NotFound($"No file {fileId} is registered.");
        context.Response.ContentLength = file.Size;
    }

    private async Task UploadShardAsync(HttpContext context)
    {
        var claims = Authorize(context, XetTokenScope.Write);
        var body = await Responses.ReadBodyAsync(context, MdbShard.MaxUploadSize).ConfigureAwait(false);
        var registration = await server.Registrar.RegisterAsync(body, claims.Repository, null, context.RequestAborted).ConfigureAwait(false);
        server.Logger.ShardRegistered(registration.FileCount, registration.NewFileCount);

        await Responses.JsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("result", registration.Result);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The streaming form: 200 the moment the stream opens, then one JSON event per line, and the
    /// outcome in the last of them. A validation failure after that point is an <c>error</c>
    /// event, since the status code has already gone out.
    /// </summary>
    private async Task UploadShardStreamingAsync(HttpContext context)
    {
        var claims = Authorize(context, XetTokenScope.Write);
        var body = await Responses.ReadBodyAsync(context, MdbShard.MaxUploadSize).ConfigureAwait(false);

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);

        try
        {
            var registration = await server.Registrar.RegisterAsync(
                body,
                claims.Repository,
                (verified, total) => EventAsync(context, $$"""{"type":"validating","verified":{{verified}},"total":{{total}}}"""),
                context.RequestAborted).ConfigureAwait(false);

            await EventAsync(context, """{"type":"committing","stage":"uploading"}""").ConfigureAwait(false);
            await EventAsync(context, """{"type":"committing","stage":"syncing"}""").ConfigureAwait(false);
            server.Logger.ShardRegistered(registration.FileCount, registration.NewFileCount);
            await EventAsync(context, $$"""{"type":"result","result":{{registration.Result}}}""").ConfigureAwait(false);
        }
        catch (XetServerException exception)
        {
            server.Logger.RequestRefused(context.Request.Method, context.Request.Path, exception.StatusCode, exception.Message);
            await EventAsync(context, $$"""{"type":"error","message":"{{JsonEncodedText.Encode(exception.Message)}}"}""").ConfigureAwait(false);
        }
    }

    private static async ValueTask EventAsync(HttpContext context, string json)
    {
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"), context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task TelemetryAsync(HttpContext context)
    {
        // Accepted and dropped: the reference client reports transfer statistics here, and a
        // server that answered anything but 200 would only make it log a warning.
        await Responses.ReadBodyAsync(context, 1024 * 1024).ConfigureAwait(false);
        context.Response.StatusCode = 200;
    }

    /// <summary>
    /// The data route the reconstruction URLs point at. Like the CDN it stands in for, it honours
    /// only a <c>Range</c> header that matches the signature exactly, and answers several ranges as
    /// <c>multipart/byteranges</c>.
    /// </summary>
    private async Task ServeXorbAsync(HttpContext context)
    {
        var xorbHash = Responses.RouteHash(context, "hash");
        var query = context.Request.Query;
        var rangeHeader = context.Request.Headers.Range.ToString();
        var signedRange = query["X-Xet-Signed-Range"].ToString();
        if (string.IsNullOrEmpty(rangeHeader))
        {
            throw new XetServerException(403, "A signed xorb URL serves only the byte ranges it was signed for; the request has no Range header.");
        }

        if (!server.Signer.Verify(xorbHash, signedRange, query["Expires"], query["Signature"]))
        {
            throw new XetServerException(403, "The URL's signature does not cover this request: the Range header must match the signed range exactly, and the URL must not have expired.");
        }

        var xorb = await server.Store.GetXorbAsync(xorbHash, context.RequestAborted).ConfigureAwait(false)
            ?? throw XetServerException.NotFound($"No xorb {xorbHash} is stored.");

        var ranges = HttpRanges.ParseMany(signedRange, xorb.SerializedLength);

        // The CDN compares the header to the signed string byte for byte. Here the two are compared
        // as ranges instead, because an HttpClient that parses the header on the way out puts a
        // space after each comma, and refusing that would refuse a request for exactly the bytes
        // that were signed.
        if (!ranges.SequenceEqual(HttpRanges.ParseMany(rangeHeader, xorb.SerializedLength)))
        {
            throw new XetServerException(403, "The Range header does not ask for the byte ranges the URL was signed for.");
        }

        server.Logger.XorbRangeServed(xorbHash, signedRange);
        context.Response.StatusCode = 206;

        if (ranges.Count == 1)
        {
            var (start, end) = ranges[0];
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ContentRange = $"bytes {start}-{end}/{xorb.SerializedLength}";
            context.Response.ContentLength = end - start + 1;
            await server.Store.CopyXorbRangeAsync(xorbHash, start, end, context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var boundary = Guid.NewGuid().ToString("N");
        context.Response.ContentType = $"multipart/byteranges; boundary={boundary}";
        foreach (var (start, end) in ranges)
        {
            var header = Encoding.ASCII.GetBytes(
                $"--{boundary}\r\nContent-Type: application/octet-stream\r\nContent-Range: bytes {start}-{end}/{xorb.SerializedLength}\r\n\r\n");
            await context.Response.Body.WriteAsync(header, context.RequestAborted).ConfigureAwait(false);
            await server.Store.CopyXorbRangeAsync(xorbHash, start, end, context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.WriteAsync("\r\n"u8.ToArray(), context.RequestAborted).ConfigureAwait(false);
        }

        await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes($"--{boundary}--\r\n"), context.RequestAborted).ConfigureAwait(false);
    }
}
