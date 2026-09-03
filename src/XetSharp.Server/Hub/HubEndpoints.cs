using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using XetSharp.Chunking;
using XetSharp.Hashing;
using XetSharp.Hub;
using XetSharp.Server.Auth;
using XetSharp.Server.Cas;
using XetSharp.Server.Diagnostics;
using XetSharp.Server.Http;
using XetSharp.Server.Storage;
using XetSharp.Xorbs;

namespace XetSharp.Server.Hub;

/// <summary>
/// The corner of the Hugging Face Hub API a Xet client touches: minting tokens, resolving a path
/// to a file ID, and committing uploaded files so they show up. None of it is in the Xet spec; the
/// shapes are what the official clients send and read, pinned by the interop tests.
/// </summary>
internal sealed class HubEndpoints(XetServer server)
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/{types}/{ns}/{name}/xet-read-token/{revision}", Guard(context => TokenAsync(context, XetTokenScope.Read)));
        endpoints.MapGet("/api/{types}/{ns}/{name}/xet-write-token/{revision}", Guard(context => TokenAsync(context, XetTokenScope.Write)));
        endpoints.MapPost("/api/{types}/{ns}/{name}/preupload/{revision}", Guard(PreuploadAsync));
        endpoints.MapPost("/api/{types}/{ns}/{name}/commit/{revision}", Guard(CommitAsync));
        endpoints.MapGet("/api/{types}/{ns}/{name}", Guard(RepositoryInfoAsync));
        endpoints.MapGet("/api/{types}/{ns}/{name}/revision/{revision}", Guard(RepositoryInfoAsync));

        string[] resolveMethods = [HttpMethods.Get, HttpMethods.Head];
        endpoints.MapMethods("/{ns}/{name}/resolve/{revision}/{*path}", resolveMethods, Guard(context => ResolveAsync(context, "model")));
        endpoints.MapMethods("/datasets/{ns}/{name}/resolve/{revision}/{*path}", resolveMethods, Guard(context => ResolveAsync(context, "dataset")));
        endpoints.MapMethods("/spaces/{ns}/{name}/resolve/{revision}/{*path}", resolveMethods, Guard(context => ResolveAsync(context, "space")));
        endpoints.MapMethods("/files/{fileId}", resolveMethods, Guard(DownloadAsync));
    }

    private RequestDelegate Guard(Func<HttpContext, Task> handler) =>
        context => Responses.GuardedAsync(context, server.Logger, handler);

    private static RepositoryId Repository(HttpContext context)
    {
        var types = Responses.RouteValue(context, "types");
        var type = types switch
        {
            "models" => "model",
            "datasets" => "dataset",
            "spaces" => "space",
            _ => throw XetServerException.NotFound($"'{types}' is not a repository type; expected models, datasets or spaces."),
        };
        return new RepositoryId(type, Responses.RouteValue(context, "ns"), Responses.RouteValue(context, "name"));
    }

    /// <summary>Whether the request carries one of the configured Hub tokens.</summary>
    private bool IsTrusted(HttpContext context) =>
        Responses.BearerToken(context) is { } token && server.Options.HubTokens.Contains(token);

    private void RequireAccess(HttpContext context, XetTokenScope scope)
    {
        if (IsTrusted(context))
        {
            return;
        }

        var allowed = scope == XetTokenScope.Write ? server.Options.AllowAnonymousWrites : server.Options.AllowAnonymousReads;
        if (!allowed)
        {
            throw new XetServerException(
                Responses.BearerToken(context) is null ? 401 : 403,
                $"A Hub token with {(scope == XetTokenScope.Write ? "write" : "read")} access is required.");
        }
    }

    private async Task TokenAsync(HttpContext context, XetTokenScope scope)
    {
        var repository = Repository(context);
        var revision = Responses.RouteValue(context, "revision");
        RequireAccess(context, scope);

        var expires = server.Options.TimeProvider.GetUtcNow().Add(server.Options.TokenLifetime);
        var token = server.Tokens.Issue(new XetTokenClaims(repository, revision, scope, expires));
        var casUrl = server.BaseUrl(context.Request).ToString().TrimEnd('/');
        server.Logger.TokenMinted(scope == XetTokenScope.Write ? "write" : "read", repository, revision);

        // The body is what the spec documents; the headers are what the official Python client
        // actually reads.
        context.Response.Headers["X-Xet-Cas-Url"] = casUrl;
        context.Response.Headers["X-Xet-Access-Token"] = token;
        context.Response.Headers["X-Xet-Token-Expiration"] = expires.ToUnixTimeSeconds().ToString();
        await Responses.JsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("accessToken", token);
            writer.WriteNumber("exp", expires.ToUnixTimeSeconds());
            writer.WriteString("casUrl", casUrl);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private async Task ResolveAsync(HttpContext context, string type)
    {
        var repository = new RepositoryId(type, Responses.RouteValue(context, "ns"), Responses.RouteValue(context, "name"));
        var revisionName = Responses.RouteValue(context, "revision");
        var path = Responses.RouteValue(context, "path");
        RequireAccess(context, XetTokenScope.Read);

        var revision = await server.Repositories.GetRevisionAsync(repository, revisionName, context.RequestAborted).ConfigureAwait(false)
            ?? throw XetServerException.NotFound($"Repository {repository} has no revision '{revisionName}'.");
        if (!revision.Files.TryGetValue(path, out var file))
        {
            throw XetServerException.NotFound($"Repository {repository} has no file '{path}' at {revisionName}.");
        }

        server.Logger.Resolved(path, repository, revisionName, file.FileId);
        var baseUrl = server.BaseUrl(context.Request);
        var tokenUrl = new Uri(baseUrl, $"api/{repository.ApiSegment}/{repository.Id}/xet-read-token/{Uri.EscapeDataString(revision.CommitId)}");
        var reconstructionUrl = new Uri(baseUrl, $"v1/reconstructions/{file.FileId}");

        var headers = context.Response.Headers;
        headers["X-Xet-Hash"] = file.FileId.ToString();
        headers["X-Linked-Size"] = file.Size.ToString();
        headers["X-Linked-Etag"] = $"\"{file.Sha256}\"";
        headers.ETag = $"\"{file.Sha256}\"";
        headers["X-Repo-Commit"] = revision.CommitId;
        headers.Link = $"<{tokenUrl}>; rel=\"xet-auth\", <{reconstructionUrl}>; rel=\"xet-reconstruction-info\"";
        headers.Location = new Uri(baseUrl, $"files/{file.FileId}").ToString();
        headers.ContentLength = HttpMethods.IsHead(context.Request.Method) ? file.Size : 0;
        context.Response.StatusCode = 302;
    }

    /// <summary>
    /// The whole file over plain HTTP, assembled from its xorbs — where the resolve redirect lands
    /// for a client that is not speaking Xet, and the oracle the interop tests compare against.
    /// </summary>
    private async Task DownloadAsync(HttpContext context)
    {
        var fileId = Responses.RouteHash(context, "fileId");
        RequireAccess(context, XetTokenScope.Read);
        var file = await server.Store.GetFileAsync(fileId, context.RequestAborted).ConfigureAwait(false)
            ?? throw XetServerException.NotFound($"No file {fileId} is registered.");

        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = file.Size;
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        var buffer = new ArrayBufferWriter<byte>();
        foreach (var term in file.Terms)
        {
            var xorb = await server.Store.GetXorbAsync(term.Xorb, context.RequestAborted).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"File {fileId} references xorb {term.Xorb}, which is no longer stored.");
            var (start, end) = xorb.ByteRangeOf(term.ChunkStart, term.ChunkEnd);

            using var serialized = new MemoryStream((int)(end - start + 1));
            await server.Store.CopyXorbRangeAsync(term.Xorb, start, end, serialized, context.RequestAborted).ConfigureAwait(false);

            buffer.ResetWrittenCount();
            var reader = new XorbChunkReader(serialized.GetBuffer().AsSpan(0, (int)serialized.Length));
            while (reader.TryReadChunk(buffer))
            {
            }

            await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private async Task PreuploadAsync(HttpContext context)
    {
        Repository(context);
        RequireAccess(context, XetTokenScope.Write);
        var body = await Responses.ReadBodyAsync(context, 16 * 1024 * 1024).ConfigureAwait(false);

        var paths = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
            {
                paths.Add(file.GetProperty("path").GetString() ?? throw XetServerException.BadRequest("A file has no path."));
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw XetServerException.BadRequest("The body must be { \"files\": [ { \"path\", \"sample\", \"size\" } ] }.");
        }

        // Everything goes through Xet here: a server that exists to exercise the protocol has no
        // reason to route small files around it.
        await Responses.JsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("files");
            foreach (var path in paths)
            {
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteString("uploadMode", "lfs");
                writer.WriteBoolean("shouldIgnore", false);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private async Task CommitAsync(HttpContext context)
    {
        var repository = Repository(context);
        var branch = Responses.RouteValue(context, "revision");
        RequireAccess(context, XetTokenScope.Write);
        var body = await Responses.ReadBodyAsync(context, 64 * 1024 * 1024).ConfigureAwait(false);

        var existing = await server.Repositories.GetRevisionAsync(repository, branch, context.RequestAborted).ConfigureAwait(false);
        if (existing is null && !server.Options.CreateRepositoriesOnCommit)
        {
            throw XetServerException.NotFound($"Repository {repository} does not exist.");
        }

        var commit = await ParseCommitAsync(body, repository, context.RequestAborted).ConfigureAwait(false);

        // The store checks the parent and advances the branch as one step; a check here would
        // let two commits from the same head both pass it.
        string commitId;
        try
        {
            commitId = await server.Repositories.CommitAsync(repository, branch, commit, context.RequestAborted).ConfigureAwait(false);
        }
        catch (BranchMovedException exception)
        {
            throw new XetServerException(412, exception.Message);
        }

        server.Logger.Committed(commitId, repository, branch, commit.Added.Count, commit.Deleted.Count);

        var baseUrl = server.BaseUrl(context.Request);
        await Responses.JsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("commitUrl", new Uri(baseUrl, $"{RepositoryPathPrefix(repository)}{repository.Id}/commit/{commitId}").ToString());
            writer.WriteString("commitOid", commitId);
            writer.WriteNull("pullRequestUrl");
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the commit body: newline-delimited JSON, one <c>{ key, value }</c> envelope per line.
    /// <c>lfsFile</c> lines name files by SHA-256 that a shard must already have registered;
    /// <c>file</c> lines carry small files inline, which are chunked and stored here so that
    /// they are Xet files like everything else. Anything not the shape it should be is a 400,
    /// whether the JSON will not parse or a property has the wrong type.
    /// </summary>
    private async Task<RepositoryCommit> ParseCommitAsync(byte[] body, RepositoryId repository, CancellationToken cancellationToken)
    {
        var summary = string.Empty;
        string? description = null;
        string? parentCommit = null;
        var added = new List<RepositoryFile>();
        var deleted = new List<string>();

        foreach (var line in Encoding.UTF8.GetString(body).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                throw XetServerException.BadRequest("The commit body must be newline-delimited JSON.");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw XetServerException.BadRequest("Each commit line must be a { \"key\", \"value\" } object.");
                }

                var key = OptionalString(root, "key", "commit");
                if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                {
                    throw XetServerException.BadRequest($"A '{key}' line has no value object.");
                }

                switch (key)
                {
                    case "header":
                        summary = OptionalString(value, "summary", key) ?? string.Empty;
                        description = OptionalString(value, "description", key);
                        parentCommit = OptionalString(value, "parentCommit", key);
                        break;

                    case "lfsFile":
                        added.Add(await LfsFileAsync(value, repository, cancellationToken).ConfigureAwait(false));
                        break;

                    case "file":
                        added.Add(await InlineFileAsync(value, repository, cancellationToken).ConfigureAwait(false));
                        break;

                    case "deletedFile":
                        deleted.Add(RequiredString(value, "path", key));
                        break;

                    default:
                        throw XetServerException.BadRequest($"Unknown commit line key '{key}'.");
                }
            }
        }

        if (added.Count == 0 && deleted.Count == 0)
        {
            throw XetServerException.BadRequest("The commit changes nothing.");
        }

        return new RepositoryCommit(summary, description, added, deleted, parentCommit);
    }

    private async Task<RepositoryFile> LfsFileAsync(JsonElement value, RepositoryId repository, CancellationToken cancellationToken)
    {
        var path = RequiredString(value, "path", "lfsFile");
        var oid = RequiredString(value, "oid", "lfsFile");
        var size = RequiredInt64(value, "size", "lfsFile");
        if (OptionalString(value, "algo", "lfsFile") is { } algorithm && algorithm != "sha256")
        {
            throw XetServerException.BadRequest($"'{path}' names its content by {algorithm}; only sha256 is supported.");
        }

        if (!MerkleHash.TryParse(oid, out var sha256))
        {
            throw XetServerException.BadRequest($"'{path}' has an oid that is not a SHA-256: '{oid}'.");
        }

        var file = await server.Store.GetFileBySha256Async(repository, sha256, cancellationToken).ConfigureAwait(false)
            ?? throw XetServerException.BadRequest($"'{path}' (sha256 {oid}) has not been uploaded to {repository}: no file registered there has that SHA-256.");
        if (file.Size != size)
        {
            throw XetServerException.BadRequest($"'{path}' is declared as {size} bytes but the uploaded file is {file.Size}.");
        }

        return new RepositoryFile(path, file.FileId, file.Size, sha256.ToString());
    }

    /// <summary>
    /// An inline file becomes a xorb and a registered file, through the same chunker and packer
    /// a client uses, so a download of it goes through exactly the same path as anything else.
    /// </summary>
    private async Task<RepositoryFile> InlineFileAsync(JsonElement value, RepositoryId repository, CancellationToken cancellationToken)
    {
        var path = RequiredString(value, "path", "file");
        var encoding = OptionalString(value, "encoding", "file") ?? "utf-8";
        var content = RequiredString(value, "content", "file");
        byte[] bytes;
        try
        {
            bytes = encoding == "base64" ? Convert.FromBase64String(content) : Encoding.UTF8.GetBytes(content);
        }
        catch (FormatException)
        {
            throw XetServerException.BadRequest($"'{path}' has content that is not valid base64.");
        }

        var sha256 = MerkleHash.FromHexOrder(SHA256.HashData(bytes));
        if (bytes.Length == 0)
        {
            var empty = new StoredFile(MerkleHash.Zero, 0, sha256, []);
            await server.Store.PutFileAsync(empty, repository, cancellationToken).ConfigureAwait(false);
            return new RepositoryFile(path, empty.FileId, 0, sha256.ToString());
        }

        var chunks = Chunker.ChunkAll(bytes);
        var nodes = chunks.Select(chunk => (XetHashes.ChunkHash(chunk), (ulong)chunk.Length)).ToArray();
        var xorbHash = XetHashes.XorbHash(nodes);

        var serialized = new ArrayBufferWriter<byte>(bytes.Length + chunks.Count * XorbChunkHeader.Size);
        XorbSerializer.Serialize(chunks.Select(chunk => (ReadOnlyMemory<byte>)chunk), serialized);
        var xorb = XorbIndexer.Index(serialized.WrittenSpan, xorbHash);
        await server.Store.PutXorbAsync(xorb, serialized.WrittenMemory, cancellationToken).ConfigureAwait(false);

        var file = new StoredFile(
            XetHashes.FileHash(nodes),
            bytes.Length,
            sha256,
            [new StoredTerm(xorbHash, 0, chunks.Count, bytes.Length)]);
        await server.Store.PutFileAsync(file, repository, cancellationToken).ConfigureAwait(false);
        return new RepositoryFile(path, file.FileId, file.Size, sha256.ToString());
    }

    /// <summary>A string property a commit line must carry, or a 400 saying which line lacks what.</summary>
    private static string RequiredString(JsonElement element, string name, string? line) =>
        OptionalString(element, name, line) ?? throw XetServerException.BadRequest($"A '{line}' line has no '{name}'.");

    /// <summary>A string property a commit line may carry: null when absent or null, a 400 when it is some other type.</summary>
    private static string? OptionalString(JsonElement element, string name, string? line)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : throw XetServerException.BadRequest($"A '{line}' line has a '{name}' that is not a string.");
    }

    private static long RequiredInt64(JsonElement element, string name, string? line) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)
            ? value
            : throw XetServerException.BadRequest($"A '{line}' line has no integer '{name}'.");

    private async Task RepositoryInfoAsync(HttpContext context)
    {
        var repository = Repository(context);
        var revisionName = context.Request.RouteValues["revision"]?.ToString() ?? "main";
        RequireAccess(context, XetTokenScope.Read);
        var revision = await server.Repositories.GetRevisionAsync(repository, revisionName, context.RequestAborted).ConfigureAwait(false)
            ?? throw XetServerException.NotFound($"Repository {repository} has no revision '{revisionName}'.");

        await Responses.JsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", repository.Id);
            writer.WriteString("sha", revision.CommitId);
            writer.WriteBoolean("private", false);
            writer.WriteBoolean("xetEnabled", true);
            writer.WriteStartArray("siblings");
            foreach (var file in revision.Files.Values.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("rfilename", file.Path);
                writer.WriteNumber("size", file.Size);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private static string RepositoryPathPrefix(RepositoryId repository) => repository.Type switch
    {
        "dataset" => "datasets/",
        "space" => "spaces/",
        _ => string.Empty,
    };
}
