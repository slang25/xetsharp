using System.Net;
using System.Security.Cryptography;
using XetSharp.Hashing;
using XetSharp.Hub;
using XetSharp.Xorbs;

namespace XetSharp.Tests;

/// <summary>
/// The whole client, offline: a stand-in Hub and CDN answer with the shapes the live services use,
/// and the reference xorb supplies the data. Everything from "repository and path" to "verified
/// bytes" runs, including the token exchange and the resolve step.
/// </summary>
public class XetClientTests
{
    private static readonly byte[] Serialized = ReferenceFiles.Read("ev-population.xorb.first-10-chunks");

    private static readonly List<byte[]> Chunks = XorbSerializer.Deserialize(Serialized);

    private static readonly byte[] FileContent = [.. Chunks.SelectMany(chunk => chunk)];

    /// <summary>The file ID the ten reference chunks add up to — what the stand-in Hub reports.</summary>
    private static readonly MerkleHash FileId = XetHashes.FileHash(ReferenceFiles.Chunks.AsSpan(0, 10));

    private static readonly XetRepository Repository = XetRepository.Dataset("xet-team/xet-spec-reference-files");

    [Test]
    public async Task Downloads_and_verifies_a_whole_file()
    {
        var handler = new FakeHttpHandler(Serve());
        using var client = NewClient(handler);
        using var destination = new MemoryStream();

        var result = await client.DownloadAsync(Repository, "reference.csv", destination);

        await Assert.That(destination.ToArray()).IsEquivalentTo(FileContent, CollectionOrdering.Matching);
        await Assert.That(result.BytesWritten).IsEqualTo(FileContent.Length);
        await Assert.That(result.FileHash).IsEqualTo(FileId);
        await Assert.That(result.Sha256).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(FileContent)));
    }

    /// <summary>Resolve, token, reconstruction, data — in that order, and nothing repeated.</summary>
    [Test]
    public async Task Makes_one_request_per_protocol_step()
    {
        var handler = new FakeHttpHandler(Serve());
        using var client = NewClient(handler);
        using var destination = new MemoryStream();

        await client.DownloadAsync(Repository, "reference.csv", destination);

        await Assert.That(handler.Requests.Select(request => $"{request.Method} {request.RequestUri!.AbsolutePath}"))
            .IsEquivalentTo(
                [
                    "HEAD /datasets/xet-team/xet-spec-reference-files/resolve/main/reference.csv",
                    "GET /api/datasets/xet-team/xet-spec-reference-files/xet-read-token/main",
                    $"GET /v2/reconstructions/{FileId}",
                    "GET /xorb",
                ],
                CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reuses_the_token_across_downloads()
    {
        var handler = new FakeHttpHandler(Serve());
        using var client = NewClient(handler);

        using (var first = new MemoryStream())
        {
            await client.DownloadAsync(Repository, "reference.csv", first);
        }

        using (var second = new MemoryStream())
        {
            await client.DownloadAsync(Repository, "reference.csv", second);
        }

        await Assert.That(handler.Requests.Count(request => request.RequestUri!.AbsolutePath.Contains("xet-read-token"))).IsEqualTo(1);
    }

    [Test]
    public async Task Downloads_a_byte_range()
    {
        var handler = new FakeHttpHandler(Serve());
        using var client = NewClient(handler);
        using var destination = new MemoryStream();

        var result = await client.DownloadRangeAsync(Repository, "reference.csv", destination, offset: 100_000, length: 50_000);

        await Assert.That(destination.ToArray()).IsEquivalentTo(FileContent[100_000..150_000], CollectionOrdering.Matching);
        await Assert.That(result.BytesWritten).IsEqualTo(50_000);

        // Whole-file verification cannot apply to a slice of the file.
        await Assert.That(result.FileHash).IsNull();
    }

    /// <summary>A range running past the end of the file yields what there is, not an error.</summary>
    [Test]
    public async Task Clamps_a_range_that_runs_past_the_end_of_the_file()
    {
        var handler = new FakeHttpHandler(Serve());
        using var client = NewClient(handler);
        using var destination = new MemoryStream();

        var offset = FileContent.Length - 1000;
        var result = await client.DownloadRangeAsync(Repository, "reference.csv", destination, offset, length: 999_999);

        await Assert.That(result.BytesWritten).IsEqualTo(1000);
        await Assert.That(destination.ToArray()).IsEquivalentTo(FileContent[offset..], CollectionOrdering.Matching);
    }

    /// <summary>
    /// Older CAS deployments serve only <c>/v1/reconstructions</c>, and its 404 is the only way to
    /// tell that apart from a file that does not exist.
    /// </summary>
    [Test]
    public async Task Falls_back_to_the_v1_reconstruction_endpoint()
    {
        var serve = Serve(reconstructionVersion: "v1");
        var handler = new FakeHttpHandler(serve);
        using var client = NewClient(handler);
        using var destination = new MemoryStream();

        var result = await client.DownloadAsync(Repository, "reference.csv", destination);

        await Assert.That(result.FileHash).IsEqualTo(FileId);
        await Assert.That(handler.Requests.Count(request => request.RequestUri!.AbsolutePath.StartsWith("/v2/"))).IsEqualTo(1);
        await Assert.That(handler.Requests.Count(request => request.RequestUri!.AbsolutePath.StartsWith("/v1/"))).IsEqualTo(1);
    }

    /// <summary>A rejected token is a refresh, not a failure — once.</summary>
    [Test]
    public async Task Refreshes_a_rejected_token_and_retries()
    {
        var rejectionsLeft = 1;
        var handler = new FakeHttpHandler(request =>
            request.RequestUri!.AbsolutePath.StartsWith("/v2/reconstructions") && rejectionsLeft-- > 0
                ? FakeHttpHandler.Status(HttpStatusCode.Unauthorized)
                : Serve()(request));
        using var client = NewClient(handler);
        using var destination = new MemoryStream();

        var result = await client.DownloadAsync(Repository, "reference.csv", destination);

        await Assert.That(result.BytesWritten).IsEqualTo(FileContent.Length);
        await Assert.That(handler.Requests.Count(request => request.RequestUri!.AbsolutePath.Contains("xet-read-token"))).IsEqualTo(2);
    }

    [Test]
    public async Task Reports_a_file_the_cas_service_has_never_heard_of()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.Contains("reconstructions")
            ? FakeHttpHandler.Status(HttpStatusCode.NotFound)
            : Serve()(request));
        using var client = NewClient(handler);
        using var destination = new MemoryStream();

        var exception = await Assert.That(async () => await client.DownloadAsync(Repository, "reference.csv", destination))
            .Throws<XetApiException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The inclusive end of such a range is past what a 64-bit offset can hold. Computed unchecked
    /// it wraps below the offset and reads as an empty range, so the call would "succeed" with
    /// nothing written.
    /// </summary>
    [Test]
    public async Task Rejects_a_range_whose_end_runs_past_the_64_bit_range()
    {
        var handler = new FakeHttpHandler(Serve());
        using var client = NewClient(handler);
        using var destination = new MemoryStream();
        var file = new XetFileInfo(FileId, FileContent.Length, null, null);

        await Assert.That(async () =>
                await client.DownloadAsync(Repository, file, destination, offset: long.MaxValue - 10, length: 100))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>A download that fails verification must not leave a file that looks complete.</summary>
    [Test]
    public async Task Leaves_no_file_behind_when_a_download_fails()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == "/xorb"
            ? FakeHttpHandler.Status(HttpStatusCode.Forbidden)
            : Serve()(request));
        using var client = NewClient(handler);
        var path = Path.Combine(Path.GetTempPath(), $"xetsharp-{Guid.NewGuid():n}.bin");

        await Assert.That(async () => await client.DownloadToFileAsync(Repository, "reference.csv", path)).Throws<XetApiException>();

        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(File.Exists(path + ".part")).IsFalse();
    }

    [Test]
    public async Task Writes_a_verified_file_to_disk()
    {
        var handler = new FakeHttpHandler(Serve());
        using var client = NewClient(handler);
        var path = Path.Combine(Path.GetTempPath(), $"xetsharp-{Guid.NewGuid():n}.bin");

        try
        {
            var result = await client.DownloadToFileAsync(Repository, "reference.csv", path);

            await Assert.That(result.FileHash).IsEqualTo(FileId);
            await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(FileContent, CollectionOrdering.Matching);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static XetClient NewClient(FakeHttpHandler handler) => new(new XetClientOptions
    {
        HubUrl = new Uri("https://hub.invalid"),
        UseAmbientCredentials = false,
        HttpClient = new HttpClient(handler),
    });

    /// <summary>A stand-in for the Hub, the CAS service and the CDN, wired to the reference xorb.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Serve(string reconstructionVersion = "v2") => request =>
    {
        var path = request.RequestUri!.AbsolutePath;

        if (path.Contains("xet-read-token"))
        {
            return FakeHttpHandler.Json(
                $$"""{"accessToken":"xet_test","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}},"casUrl":"https://cas.invalid"}""");
        }

        if (path.Contains("/resolve/"))
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new ByteArrayContent([]) };
            response.Headers.Add("X-Xet-Hash", FileId.ToString());
            response.Headers.Add("X-Linked-Size", FileContent.Length.ToString());
            response.Headers.Add("X-Linked-Etag", $"\"{Convert.ToHexStringLower(SHA256.HashData(FileContent))}\"");
            return response;
        }

        if (path.StartsWith("/v1/reconstructions") || path.StartsWith("/v2/reconstructions"))
        {
            if (!path.StartsWith($"/{reconstructionVersion}/"))
            {
                return FakeHttpHandler.Status(HttpStatusCode.NotFound);
            }

            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            return FakeHttpHandler.Json(ReconstructionFor(range?.From ?? 0, range?.To, reconstructionVersion));
        }

        if (path == "/xorb")
        {
            var header = request.Headers.NonValidated["Range"].ToString();
            var bounds = header["bytes=".Length..].Split('-');
            var start = int.Parse(bounds[0]);
            return FakeHttpHandler.Bytes(Serialized[start..(int.Parse(bounds[1]) + 1)], rangeStart: start);
        }

        return FakeHttpHandler.Status(HttpStatusCode.NotFound);
    };

    /// <summary>
    /// Builds the reconstruction the CAS service would return for a byte range: the chunks that
    /// cover it, and how far into the first of them the range starts.
    /// </summary>
    private static string ReconstructionFor(long start, long? end, string version)
    {
        var (firstChunk, offsetIntoFirst) = LocateChunk(start);
        var (lastChunk, _) = LocateChunk(Math.Min(end ?? (FileContent.Length - 1), FileContent.Length - 1));
        var chunkEnd = lastChunk + 1;

        var byteStart = 0;
        var byteEnd = 0;
        var offset = 0;
        var unpacked = 0L;
        var reader = new XorbChunkReader(Serialized);
        for (var index = 0; reader.TryReadRawChunk(out var chunkHeader, out _); index++)
        {
            if (index == firstChunk)
            {
                byteStart = offset;
            }

            if (index >= firstChunk && index < chunkEnd)
            {
                unpacked += chunkHeader.UncompressedSize;
                byteEnd = offset + chunkHeader.RecordSize - 1;
            }

            offset += chunkHeader.RecordSize;
        }

        var fetch = version == "v2"
            ? $$"""
              "xorbs": { "{{ReferenceFiles.XorbHash}}": [ { "url": "https://cdn.invalid/xorb",
                "ranges": [ { "chunks": { "start": {{firstChunk}}, "end": {{chunkEnd}} }, "bytes": { "start": {{byteStart}}, "end": {{byteEnd}} } } ] } ] }
              """
            : $$"""
              "fetch_info": { "{{ReferenceFiles.XorbHash}}": [ { "range": { "start": {{firstChunk}}, "end": {{chunkEnd}} },
                "url": "https://cdn.invalid/xorb", "url_range": { "start": {{byteStart}}, "end": {{byteEnd}} } } ] }
              """;

        return $$"""
        {
          "offset_into_first_range": {{offsetIntoFirst}},
          "terms": [ { "hash": "{{ReferenceFiles.XorbHash}}", "unpacked_length": {{unpacked}}, "range": { "start": {{firstChunk}}, "end": {{chunkEnd}} } } ],
          {{fetch}}
        }
        """;
    }

    private static (int Chunk, long Offset) LocateChunk(long fileOffset)
    {
        var remaining = fileOffset;
        for (var index = 0; index < Chunks.Count; index++)
        {
            if (remaining < Chunks[index].Length)
            {
                return (index, remaining);
            }

            remaining -= Chunks[index].Length;
        }

        return (Chunks.Count - 1, Chunks[^1].Length - 1);
    }
}
