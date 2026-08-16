using System.Net;
using System.Security.Cryptography;
using System.Text;
using XetSharp.Shards;
using XetSharp.Xorbs;

namespace XetSharp.Tests;

/// <summary>
/// A stand-in Hub and CAS service that accepts uploads and can then serve back what it was given.
/// Storing the xorbs and shards is what makes the round trip possible: a file uploaded through the
/// real pipeline can be downloaded again through the real pipeline, with nothing in between but
/// bytes the client itself produced.
/// </summary>
internal sealed class FakeCas
{
    private readonly Lock _gate = new();

    /// <summary>Every xorb uploaded, by hash, in the order they arrived.</summary>
    public Dictionary<MerkleHash, byte[]> Xorbs { get; } = [];

    /// <summary>Every shard uploaded, parsed.</summary>
    public List<MdbShard> Shards { get; } = [];

    /// <summary>The raw bytes of every shard uploaded, for tests that care about the wire form.</summary>
    public List<byte[]> ShardBodies { get; } = [];

    /// <summary>Method and path of every request, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>
    /// The shard to answer global-deduplication queries with. Null answers 404 — the ordinary case
    /// of a chunk the service has never seen.
    /// </summary>
    public MdbShard? DedupeResponse { get; set; }

    /// <summary>Whether the service serves the streaming <c>/v2/shards</c> endpoint.</summary>
    public bool ServesStreamingShardUpload { get; set; } = true;

    /// <summary>Chunk hashes asked about through the global-deduplication API.</summary>
    public List<MerkleHash> DedupeQueries { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Handler => Respond;

    /// <summary>The file IDs registered by the shards uploaded so far.</summary>
    public IEnumerable<ShardFileInfo> RegisteredFiles => Shards.SelectMany(shard => shard.Files);

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (_gate)
        {
            Requests.Add($"{request.Method} {path}");
        }

        if (path.Contains("xet-write-token") || path.Contains("xet-read-token"))
        {
            return FakeHttpHandler.Json(
                $$"""{"accessToken":"xet_test","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}},"casUrl":"https://cas.invalid"}""");
        }

        if (path.StartsWith("/v1/xorbs/default/"))
        {
            var hash = MerkleHash.Parse(path.AsSpan("/v1/xorbs/default/".Length));
            var body = Body(request);
            lock (_gate)
            {
                var inserted = Xorbs.TryAdd(hash, body);
                return FakeHttpHandler.Json($$"""{"was_inserted":{{(inserted ? "true" : "false")}}}""");
            }
        }

        if (path is "/v1/shards" or "/v2/shards")
        {
            if (path == "/v2/shards" && !ServesStreamingShardUpload)
            {
                return FakeHttpHandler.Status(HttpStatusCode.NotFound);
            }

            var body = Body(request);
            var shard = MdbShard.Parse(body);
            RejectShardsNamingMissingXorbs(shard);
            lock (_gate)
            {
                ShardBodies.Add(body);
                Shards.Add(shard);
            }

            return path == "/v2/shards"
                ? Ndjson("""
                    {"type":"validating","verified":0,"total":2}
                    {"type":"committing","stage":"uploading"}
                    {"type":"result","result":1}
                    """)
                : FakeHttpHandler.Json("""{"result":1}""");
        }

        if (path.StartsWith("/v1/chunks/default-merkledb/"))
        {
            var chunkHash = MerkleHash.Parse(path.AsSpan("/v1/chunks/default-merkledb/".Length));
            lock (_gate)
            {
                DedupeQueries.Add(chunkHash);
            }

            if (DedupeResponse is not { } dedupe || !dedupe.TryFindChunk(chunkHash, out _, out _))
            {
                return FakeHttpHandler.Status(HttpStatusCode.NotFound);
            }

            return FakeHttpHandler.Bytes(dedupe.ToByteArray(), HttpStatusCode.OK);
        }

        if (path.Contains("/resolve/"))
        {
            return Resolve(path);
        }

        if (path.StartsWith("/v2/reconstructions/"))
        {
            return Reconstruct(MerkleHash.Parse(path.AsSpan("/v2/reconstructions/".Length)));
        }

        if (path.StartsWith("/xorb/"))
        {
            var xorb = Xorbs[MerkleHash.Parse(path.AsSpan("/xorb/".Length))];
            var bounds = request.Headers.NonValidated["Range"].ToString()["bytes=".Length..].Split('-');
            var start = int.Parse(bounds[0]);
            return FakeHttpHandler.Bytes(xorb[start..(int.Parse(bounds[1]) + 1)], rangeStart: start);
        }

        return FakeHttpHandler.Status(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The rule the real service enforces and the one an upload is most likely to get wrong: a
    /// shard may not name a xorb that has not been stored yet.
    /// </summary>
    private void RejectShardsNamingMissingXorbs(MdbShard shard)
    {
        var referenced = shard.Files
            .SelectMany(file => file.Terms.Select(term => term.XorbHash))
            .Concat(shard.Xorbs.Select(xorb => xorb.XorbHash));

        lock (_gate)
        {
            foreach (var xorbHash in referenced.Where(hash => !Xorbs.ContainsKey(hash)).Distinct())
            {
                throw new InvalidOperationException($"The shard names xorb {xorbHash}, which has not been uploaded.");
            }
        }
    }

    /// <summary>
    /// Answers a resolve request for a file the uploads registered. A shard carries no repository
    /// paths, so tests resolve a file by passing its file ID where a path would go.
    /// </summary>
    private HttpResponseMessage Resolve(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (!MerkleHash.TryParse(name, out var fileId))
        {
            return FakeHttpHandler.Status(HttpStatusCode.NotFound);
        }

        var file = RegisteredFiles.FirstOrDefault(candidate => candidate.FileHash == fileId);
        if (file is null)
        {
            return FakeHttpHandler.Status(HttpStatusCode.NotFound);
        }

        var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new ByteArrayContent([]) };
        response.Headers.Add("X-Xet-Hash", fileId.ToString());
        response.Headers.Add("X-Linked-Size", file.Terms.Sum(term => (long)term.UnpackedLength).ToString());
        response.Headers.Add("X-Linked-Etag", $"\"{file.Sha256!.Value.ToString()}\"");
        return response;
    }

    /// <summary>
    /// Turns a registered file's shard entry back into a reconstruction, the way the real service
    /// does — including working out each term's byte range by walking the stored xorb's records,
    /// since the shard records uncompressed offsets rather than serialized ones.
    /// </summary>
    private HttpResponseMessage Reconstruct(MerkleHash fileId)
    {
        var file = RegisteredFiles.FirstOrDefault(candidate => candidate.FileHash == fileId);
        if (file is null)
        {
            return FakeHttpHandler.Status(HttpStatusCode.NotFound);
        }

        var terms = new StringBuilder();
        var xorbs = new Dictionary<MerkleHash, List<string>>();
        foreach (var term in file.Terms)
        {
            terms.Append(terms.Length == 0 ? string.Empty : ",");
            var chunkRange = $$"""{ "start": {{term.ChunkIndexStart}}, "end": {{term.ChunkIndexEnd}} }""";
            terms.Append($$"""{ "hash": "{{term.XorbHash}}", "unpacked_length": {{term.UnpackedLength}}, "range": {{chunkRange}} }""");

            var offsets = RecordOffsets(Xorbs[term.XorbHash]);
            var byteRange = $$"""{ "start": {{offsets[(int)term.ChunkIndexStart]}}, "end": {{offsets[(int)term.ChunkIndexEnd] - 1}} }""";
            if (!xorbs.TryGetValue(term.XorbHash, out var ranges))
            {
                xorbs[term.XorbHash] = ranges = [];
            }

            // One fetch per range rather than one per xorb: it keeps the stand-in CDN a plain
            // single-range reader, and the client has its own multi-range coverage elsewhere.
            ranges.Add($$"""
                { "url": "https://cdn.invalid/xorb/{{term.XorbHash}}", "ranges": [ { "chunks": {{chunkRange}}, "bytes": {{byteRange}} } ] }
                """);
        }

        var fetches = xorbs.Select(entry => $$""" "{{entry.Key}}": [ {{string.Join(",", entry.Value)}} ] """);

        return FakeHttpHandler.Json($$"""
            { "offset_into_first_range": 0, "terms": [ {{terms}} ], "xorbs": { {{string.Join(",", fetches)}} } }
            """);
    }

    /// <summary>The serialized offset of every chunk record in a xorb, plus the end of the last one.</summary>
    private static int[] RecordOffsets(byte[] serialized)
    {
        var offsets = new List<int> { 0 };
        var reader = new XorbChunkReader(serialized);
        while (reader.TryReadRawChunk(out _, out _))
        {
            offsets.Add(reader.BytesConsumed);
        }

        return [.. offsets];
    }

    private static byte[] Body(HttpRequestMessage request) =>
        request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();

    private static HttpResponseMessage Ndjson(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ReplaceLineEndings("\n") + "\n", Encoding.UTF8, "application/x-ndjson"),
        };

    /// <summary>The SHA-256 of some bytes in the hex form the Hub reports and the shard stores.</summary>
    public static string Sha256Of(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
