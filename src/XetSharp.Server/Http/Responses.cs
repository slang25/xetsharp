using System.Buffers;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using XetSharp.Server.Diagnostics;

namespace XetSharp.Server.Http;

/// <summary>The few ways a handler answers, shared by both halves of the server.</summary>
internal static class Responses
{
    /// <summary>
    /// Runs a handler, turning a <see cref="XetServerException"/> into the status it names and an
    /// unexpected failure into a 500, each with a small JSON body saying why. Nothing is written
    /// if the response has already started: a streaming endpoint has its own way of reporting.
    /// </summary>
    public static async Task GuardedAsync(HttpContext context, ILogger logger, Func<HttpContext, Task> handler)
    {
        try
        {
            await handler(context).ConfigureAwait(false);
        }
        catch (XetServerException exception) when (!context.Response.HasStarted)
        {
            logger.RequestRefused(context.Request.Method, context.Request.Path, exception.StatusCode, exception.Message);
            await ErrorAsync(context, exception.StatusCode, exception.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            logger.RequestRefused(context.Request.Method, context.Request.Path, 500, exception.ToString());
            await ErrorAsync(context, 500, "The server failed to handle the request.").ConfigureAwait(false);
        }
    }

    public static Task ErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        return JsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("error", message);
            writer.WriteEndObject();
        });
    }

    /// <summary>Writes a JSON body, gzipped when the request said it would take one.</summary>
    public static async Task JsonAsync(HttpContext context, Action<Utf8JsonWriter> write, bool compressible = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        context.Response.ContentType = "application/json";
        if (compressible && AcceptsGzip(context))
        {
            context.Response.Headers.ContentEncoding = "gzip";
            var gzip = new GZipStream(context.Response.Body, CompressionLevel.Fastest, leaveOpen: true);
            await using (gzip.ConfigureAwait(false))
            {
                await gzip.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
            }

            return;
        }

        context.Response.ContentLength = buffer.WrittenCount;
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool AcceptsGzip(HttpContext context) =>
        context.Request.Headers.AcceptEncoding.Any(value =>
            value is not null && value.Split(',').Any(encoding =>
                StringWithQualityHeaderValue.TryParse(encoding, out var parsed) &&
                string.Equals(parsed.Value, "gzip", StringComparison.OrdinalIgnoreCase) &&
                parsed.Quality is not 0));

    /// <summary>
    /// Reads the whole request body into memory, refusing one over <paramref name="maxBytes"/>.
    /// The server's bodies are xorbs and shards, each capped by the protocol at 64 MiB, so a limit
    /// is known before the first byte arrives.
    /// </summary>
    public static async Task<byte[]> ReadBodyAsync(HttpContext context, int maxBytes)
    {
        // Kestrel's default cap is below a full-size xorb; lift it for this request only.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = maxBytes + 1;
        }

        if (context.Request.ContentLength is { } declared && declared > maxBytes)
        {
            throw XetServerException.BadRequest($"The body is {declared} bytes; the limit is {maxBytes}.");
        }

        using var buffer = new MemoryStream((int)Math.Clamp(context.Request.ContentLength ?? 64 * 1024, 1024, maxBytes));
        var window = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(window, context.RequestAborted).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > maxBytes)
                {
                    throw XetServerException.BadRequest($"The body ran past the {maxBytes}-byte limit.");
                }

                buffer.Write(window, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(window);
        }
    }

    public static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }

    public static string RouteValue(HttpContext context, string name) =>
        context.Request.RouteValues[name]?.ToString() ?? throw XetServerException.BadRequest($"Missing '{name}' in the path.");

    public static MerkleHash RouteHash(HttpContext context, string name)
    {
        var text = RouteValue(context, name);
        return MerkleHash.TryParse(text, out var hash)
            ? hash
            : throw XetServerException.BadRequest($"'{text}' is not a hash: expected 64 lowercase hex characters.");
    }
}
