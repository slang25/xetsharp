using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XetSharp.Cas;
using XetSharp.Diagnostics;

namespace XetSharp.Download;

/// <summary>
/// Fetches xorb data from the signed URLs a reconstruction hands out. Every range of one
/// <see cref="XorbFetch"/> travels in a single request, because the URL's signature authorizes
/// exactly that set of ranges and nothing else — altering or splitting them fails authorization.
/// </summary>
internal sealed class XorbRangeFetcher(HttpClient httpClient, int maxConcurrency, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// The most a single response may occupy. A xorb is capped at 64 MiB serialized; the slack
    /// covers multipart framing and a server that answers a range request with the whole object.
    /// </summary>
    private const long MaxResponseBytes = 80L * 1024 * 1024;

    private readonly SemaphoreSlim _concurrency = new(Math.Max(1, maxConcurrency));

    public async Task<XorbRangeData[]> FetchAsync(XorbFetch fetch, CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, fetch.Url);

            // Added unvalidated so the header keeps the exact form the signature was computed over;
            // the typed Range header would reformat a multi-range value.
            request.Headers.TryAddWithoutValidation("Range", RangeHeaderFor(fetch));

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new XetApiException(
                    $"Fetching xorb data failed with {(int)response.StatusCode} {response.ReasonPhrase}." +
                    (response.StatusCode == HttpStatusCode.Forbidden
                        ? " Signed xorb URLs authorize one exact set of byte ranges, and expire quickly."
                        : string.Empty),
                    response.StatusCode,
                    fetch.Url);
            }

            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            _logger.FetchedXorbData(body.Length, fetch.Ranges.Count, fetch.Url.Host);
            return response.Content.Headers.ContentType?.MediaType == "multipart/byteranges"
                ? FromMultipart(fetch, response, body)
                : FromSingleRange(fetch, response, body);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// The <c>Range</c> header for a fetch. When the URL carries the signed range verbatim we echo
    /// that, since it is exactly the string the CDN checks against; otherwise the ranges are
    /// formatted in the order the reconstruction listed them.
    /// </summary>
    internal static string RangeHeaderFor(XorbFetch fetch) =>
        SignedRange(fetch.Url) ?? $"bytes={string.Join(',', fetch.Ranges.Select(range => range.Bytes.ToString()))}";

    private static string? SignedRange(Uri url)
    {
        foreach (var parameter in url.Query.TrimStart('?').Split('&'))
        {
            var separator = parameter.IndexOf('=');
            if (separator > 0 && parameter.AsSpan(0, separator).Equals("X-Xet-Signed-Range", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parameter[(separator + 1)..]);
            }
        }

        return null;
    }

    private static XorbRangeData[] FromSingleRange(XorbFetch fetch, HttpResponseMessage response, byte[] body)
    {
        if (fetch.Ranges.Count != 1)
        {
            throw new InvalidDataException(
                $"Asked for {fetch.Ranges.Count} byte ranges but the server answered with a single {response.StatusCode} body.");
        }

        var descriptor = fetch.Ranges[0];

        // A server is allowed to ignore Range and send the whole object; take the slice we asked for.
        if (response.StatusCode == HttpStatusCode.OK && body.Length > descriptor.Bytes.Length)
        {
            if (descriptor.Bytes.End >= body.Length)
            {
                throw new InvalidDataException(
                    $"The server ignored the range request and returned {body.Length} bytes, too few to cover {descriptor.Bytes}.");
            }

            return [new XorbRangeData(descriptor, body.AsMemory((int)descriptor.Bytes.Start, (int)descriptor.Bytes.Length))];
        }

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            EnsureContentRangeMatches(response, descriptor.Bytes, fetch.Url);
        }

        return [new XorbRangeData(descriptor, body)];
    }

    /// <summary>
    /// Checks that a 206 answers the range that was asked for. The length is checked further down
    /// the line, but a cache or proxy that serves a differently-offset slice of the same size would
    /// pass that and quietly put the wrong bytes into the file — a range download has no whole-file
    /// hash to catch it later.
    /// </summary>
    private static void EnsureContentRangeMatches(HttpResponseMessage response, ByteRange expected, Uri url)
    {
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange is null || !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The server answered {(int)response.StatusCode} for {url} without a usable Content-Range for the requested range {expected}.");
        }

        if (contentRange.From != expected.Start || contentRange.To != expected.End)
        {
            throw new InvalidDataException(
                $"The server answered with bytes {contentRange.From}-{contentRange.To}, not the requested range {expected}.");
        }
    }

    private static XorbRangeData[] FromMultipart(XorbFetch fetch, HttpResponseMessage response, byte[] body)
    {
        var boundary = response.Content.Headers.ContentType?.Parameters
            .FirstOrDefault(parameter => string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim('"');

        if (string.IsNullOrEmpty(boundary))
        {
            throw new InvalidDataException("The multipart/byteranges response has no boundary parameter.");
        }

        var parts = MultipartByteRanges.Parse(body, boundary);
        if (parts.Count != fetch.Ranges.Count)
        {
            throw new InvalidDataException($"Asked for {fetch.Ranges.Count} byte ranges but the response carries {parts.Count}.");
        }

        // Match on the declared range rather than on position, then check every range was answered.
        var byRange = parts.ToDictionary(part => part.Range);
        var data = new XorbRangeData[fetch.Ranges.Count];
        for (var index = 0; index < fetch.Ranges.Count; index++)
        {
            var descriptor = fetch.Ranges[index];
            if (!byRange.TryGetValue(descriptor.Bytes, out var part))
            {
                throw new InvalidDataException($"The multipart response does not contain the requested range {descriptor.Bytes}.");
            }

            data[index] = new XorbRangeData(descriptor, part.Body);
        }

        return data;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > MaxResponseBytes)
        {
            throw new InvalidDataException($"A xorb data response declared {declared} bytes, past the {MaxResponseBytes}-byte limit.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream(capacity: (int)Math.Clamp(response.Content.Headers.ContentLength ?? 0, 4096, MaxResponseBytes));
            var window = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(window, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                // Checked as it arrives, not afterwards: a response with no Content-Length would
                // otherwise be free to fill memory before anyone looked at how big it had become.
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw new InvalidDataException($"A xorb data response ran past the {MaxResponseBytes}-byte limit.");
                }

                buffer.Write(window, 0, read);
            }
        }
    }
}
