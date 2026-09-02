using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using XetSharp.Cas;
using XetSharp.Chunking;
using XetSharp.Hashing;
using XetSharp.Hub;
using XetSharp.Server.Storage;
using XetSharp.Shards;
using XetSharp.Upload;
using XetSharp.Xorbs;

namespace XetSharp.Server.Tests;

/// <summary>
/// The CAS endpoints one request at a time: the shapes they answer with, and — more importantly
/// for a server — what they refuse. Every refusal here is a rule the spec states or the real
/// service enforces, and each is checked by sending exactly the request that breaks it.
/// </summary>
public class CasApiTests
{
    private static readonly XetRepository Repository = XetRepository.Model("acme/scratch");

    private static async Task<string> ReadTokenAsync(TestXetServer host, string scope = "read")
    {
        using var response = await host.HttpClient.GetAsync($"/api/models/acme/scratch/xet-{scope}-token/main");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token, byte[]? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } };
        }

        return request;
    }

    /// <summary>A xorb of a few chunks, serialized, with its hash and chunk listing.</summary>
    private static (byte[] Serialized, MerkleHash Hash, List<byte[]> Chunks) MakeXorb(ulong seed, int bytes = 300_000)
    {
        var chunks = Chunker.ChunkAll(TestData.SplitMix64Bytes(seed, bytes));
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        XorbSerializer.Serialize(chunks.Select(chunk => (ReadOnlyMemory<byte>)chunk), writer);
        var hash = XetHashes.XorbHash(chunks.Select(chunk => (XetHashes.ChunkHash(chunk), (ulong)chunk.Length)).ToArray());
        return (writer.WrittenSpan.ToArray(), hash, chunks);
    }

    [Test]
    public async Task Every_cas_endpoint_needs_a_token()
    {
        await using var host = await TestXetServer.StartAsync();
        var hash = new string('0', 64);

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, $"/v2/reconstructions/{hash}"),
            (HttpMethod.Get, $"/v1/reconstructions/{hash}"),
            (HttpMethod.Get, $"/v1/chunks/default-merkledb/{hash}"),
            (HttpMethod.Post, $"/v1/xorbs/default/{hash}"),
            (HttpMethod.Head, $"/v1/xorbs/default/{hash}"),
            (HttpMethod.Head, $"/v1/files/{hash}"),
            (HttpMethod.Post, "/v1/shards"),
            (HttpMethod.Post, "/v2/shards"),
        })
        {
            using var response = await host.HttpClient.SendAsync(new HttpRequestMessage(method, path) { Content = new ByteArrayContent([]) });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized).Because($"{method} {path}");
        }
    }

    [Test]
    public async Task Upload_endpoints_refuse_a_read_token()
    {
        await using var host = await TestXetServer.StartAsync();
        var token = await ReadTokenAsync(host);
        var (serialized, hash, _) = MakeXorb(1);

        using var xorb = await host.HttpClient.SendAsync(Request(HttpMethod.Post, $"/v1/xorbs/default/{hash}", token, serialized));
        await Assert.That(xorb.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        using var shard = await host.HttpClient.SendAsync(Request(HttpMethod.Post, "/v1/shards", token, []));
        await Assert.That(shard.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task A_token_minted_by_another_server_is_refused()
    {
        await using var host = await TestXetServer.StartAsync();
        await using var other = await TestXetServer.StartAsync();
        var foreign = await ReadTokenAsync(other);

        using var response = await host.HttpClient.SendAsync(Request(HttpMethod.Head, $"/v1/files/{new string('0', 64)}", foreign));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task An_expired_token_is_refused()
    {
        var clock = new FakeTimeProvider();
        await using var host = await TestXetServer.StartAsync(new XetServerOptions { TimeProvider = clock, TokenLifetime = TimeSpan.FromMinutes(5) });
        var token = await ReadTokenAsync(host);
        clock.Advance(TimeSpan.FromMinutes(6));

        using var response = await host.HttpClient.SendAsync(Request(HttpMethod.Head, $"/v1/files/{new string('0', 64)}", token));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Stores_a_xorb_once_and_reports_whether_it_was_new()
    {
        await using var host = await TestXetServer.StartAsync();
        var token = await ReadTokenAsync(host, "write");
        var (serialized, hash, chunks) = MakeXorb(2);

        using var first = await host.HttpClient.SendAsync(Request(HttpMethod.Post, $"/v1/xorbs/default/{hash}", token, serialized));
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await first.Content.ReadAsStringAsync()).IsEqualTo("""{"was_inserted":true}""");

        using var second = await host.HttpClient.SendAsync(Request(HttpMethod.Post, $"/v1/xorbs/default/{hash}", token, serialized));
        await Assert.That(await second.Content.ReadAsStringAsync()).IsEqualTo("""{"was_inserted":false}""");

        using var head = await host.HttpClient.SendAsync(Request(HttpMethod.Head, $"/v1/xorbs/default/{hash}", token));
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(head.Content.Headers.ContentLength).IsEqualTo(serialized.Length);

        var stored = await host.Store.GetXorbAsync(hash);
        await Assert.That(stored!.Chunks.Length).IsEqualTo(chunks.Count);
        await Assert.That(stored.Chunks.Select(chunk => chunk.Hash)).IsEquivalentTo(chunks.Select(chunk => XetHashes.ChunkHash(chunk)), CollectionOrdering.Matching);
        await Assert.That(stored.Chunks[0].SerializedOffset).IsEqualTo(0L);
        await Assert.That(stored.Chunks[^1].SerializedOffset + stored.Chunks[^1].SerializedLength).IsEqualTo((long)serialized.Length);
    }

    [Test]
    public async Task Refuses_a_xorb_uploaded_under_the_wrong_hash()
    {
        await using var host = await TestXetServer.StartAsync();
        var token = await ReadTokenAsync(host, "write");
        var (serialized, _, _) = MakeXorb(3);
        var wrong = XetHashes.ChunkHash("not this xorb"u8);

        using var response = await host.HttpClient.SendAsync(Request(HttpMethod.Post, $"/v1/xorbs/default/{wrong}", token, serialized));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("hash to");
        await Assert.That(await host.Store.GetXorbAsync(wrong)).IsNull();
    }

    [Test]
    public async Task Refuses_bytes_that_are_not_a_xorb()
    {
        await using var host = await TestXetServer.StartAsync();
        var token = await ReadTokenAsync(host, "write");
        var garbage = TestData.SplitMix64Bytes(99, 1000);
        garbage[0] = 7; // an unknown chunk header version

        using var response = await host.HttpClient.SendAsync(Request(HttpMethod.Post, $"/v1/xorbs/default/{XetHashes.ChunkHash(garbage)}", token, garbage));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Refuses_the_wrong_prefixes()
    {
        await using var host = await TestXetServer.StartAsync();
        var token = await ReadTokenAsync(host, "write");
        var hash = new string('0', 64);

        using var xorb = await host.HttpClient.SendAsync(Request(HttpMethod.Post, $"/v1/xorbs/other/{hash}", token, []));
        await Assert.That(xorb.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        using var chunk = await host.HttpClient.SendAsync(Request(HttpMethod.Get, $"/v1/chunks/other/{hash}", token));
        await Assert.That(chunk.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // The reference client asks with 'default' where the spec says 'default-merkledb'; both work.
        using var referencePrefix = await host.HttpClient.SendAsync(Request(HttpMethod.Get, $"/v1/chunks/default/{hash}", token));
        await Assert.That(referencePrefix.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        using var malformed = await host.HttpClient.SendAsync(Request(HttpMethod.Get, "/v2/reconstructions/not-a-hash", token));
        await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Refuses_a_shard_naming_a_xorb_that_was_not_uploaded()
    {
        await using var host = await TestXetServer.StartAsync();
        var token = await ReadTokenAsync(host, "write");
        var (_, hash, chunks) = MakeXorb(4);
        var shard = ShardFor(hash, chunks);

        using var v1 = await host.HttpClient.SendAsync(Request(HttpMethod.Post, "/v1/shards", token, shard.ToByteArray()));
        await Assert.That(v1.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await v1.Content.ReadAsStringAsync()).Contains("has not been uploaded");

        // The streaming form has already said 200 by the time it knows; the refusal is an event.
        using var v2 = await host.HttpClient.SendAsync(Request(HttpMethod.Post, "/v2/shards", token, shard.ToByteArray()));
        await Assert.That(v2.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var events = (await v2.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(events[^1]).Contains("\"type\":\"error\"");
        await Assert.That(events[^1]).Contains("has not been uploaded");
    }

    [Test]
    public async Task Refuses_a_shard_whose_verification_hashes_are_wrong()
    {
        await using var host = await TestXetServer.StartAsync();
        var token = await ReadTokenAsync(host, "write");
        var (serialized, hash, chunks) = MakeXorb(5);
        using (await host.HttpClient.SendAsync(Request(HttpMethod.Post, $"/v1/xorbs/default/{hash}", token, serialized)))
        {
        }

        var honest = ShardFor(hash, chunks);
        var forged = honest with
        {
            Files = [honest.Files[0] with { Terms = [honest.Files[0].Terms[0] with { VerificationHash = XetHashes.ChunkHash("forged"u8) }] }],
        };

        using var response = await host.HttpClient.SendAsync(Request(HttpMethod.Post, "/v1/shards", token, forged.ToByteArray()));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("verification hash");
        await Assert.That(await host.Store.GetFileAsync(honest.Files[0].FileHash)).IsNull();
    }

    [Test]
    public async Task Refuses_a_shard_whose_file_hash_is_wrong()
    {
        await using var host = await TestXetServer.StartAsync();
        var token = await ReadTokenAsync(host, "write");
        var (serialized, hash, chunks) = MakeXorb(6);
        using (await host.HttpClient.SendAsync(Request(HttpMethod.Post, $"/v1/xorbs/default/{hash}", token, serialized)))
        {
        }

        var honest = ShardFor(hash, chunks);
        var forged = honest with { Files = [honest.Files[0] with { FileHash = XetHashes.ChunkHash("forged"u8) }] };

        using var response = await host.HttpClient.SendAsync(Request(HttpMethod.Post, "/v1/shards", token, forged.ToByteArray()));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("not the hash of its terms");
    }

    [Test]
    public async Task Refuses_a_shard_whose_chunk_listing_disagrees_with_the_xorb()
    {
        await using var host = await TestXetServer.StartAsync();
        var token = await ReadTokenAsync(host, "write");
        var (serialized, hash, chunks) = MakeXorb(7);
        using (await host.HttpClient.SendAsync(Request(HttpMethod.Post, $"/v1/xorbs/default/{hash}", token, serialized)))
        {
        }

        var honest = ShardFor(hash, chunks);
        var listing = honest.Xorbs[0];
        var forged = honest with
        {
            Xorbs = [listing with { Chunks = [.. listing.Chunks.Take(listing.Chunks.Count - 1), listing.Chunks[^1] with { UnpackedLength = 1 }] }],
        };

        using var response = await host.HttpClient.SendAsync(Request(HttpMethod.Post, "/v1/shards", token, forged.ToByteArray()));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("disagrees with the xorb");
    }

    [Test]
    public async Task Registers_a_valid_shard_through_both_endpoints()
    {
        await using var host = await TestXetServer.StartAsync();
        var token = await ReadTokenAsync(host, "write");
        var (serialized, hash, chunks) = MakeXorb(8);
        using (await host.HttpClient.SendAsync(Request(HttpMethod.Post, $"/v1/xorbs/default/{hash}", token, serialized)))
        {
        }

        var shard = ShardFor(hash, chunks);

        using var v2 = await host.HttpClient.SendAsync(Request(HttpMethod.Post, "/v2/shards", token, shard.ToByteArray()));
        await Assert.That(v2.Content.Headers.ContentType!.MediaType).IsEqualTo("application/x-ndjson");
        var events = (await v2.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(events[0]).Contains("\"type\":\"validating\"");
        await Assert.That(events[^1]).IsEqualTo("""{"type":"result","result":1}""");

        using var v1 = await host.HttpClient.SendAsync(Request(HttpMethod.Post, "/v1/shards", token, shard.ToByteArray()));
        await Assert.That(await v1.Content.ReadAsStringAsync()).IsEqualTo("""{"result":0}""");

        using var head = await host.HttpClient.SendAsync(Request(HttpMethod.Head, $"/v1/files/{shard.Files[0].FileHash}", token));
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(head.Content.Headers.ContentLength).IsEqualTo(chunks.Sum(chunk => (long)chunk.Length));
    }

    [Test]
    public async Task Reconstructions_come_in_both_shapes_and_parse_with_the_client()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var content = TestData.SplitMix64Bytes(9, 500_000);
        var upload = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);
        var fileId = upload.Files.Single().FileId;
        var token = await ReadTokenAsync(host);

        foreach (var version in new[] { "v1", "v2" })
        {
            using var response = await host.HttpClient.SendAsync(Request(HttpMethod.Get, $"/{version}/reconstructions/{fileId}", token));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var reconstruction = FileReconstruction.Parse(await response.Content.ReadAsByteArrayAsync());
            await Assert.That(reconstruction.UnpackedLength).IsEqualTo(content.Length);
            await Assert.That(reconstruction.Terms.Count).IsEqualTo(1);
            await Assert.That(reconstruction.Xorbs.Single().Value.Single().Url.Query).Contains("X-Xet-Signed-Range=");
        }

        using var unknown = await host.HttpClient.SendAsync(Request(HttpMethod.Get, $"/v2/reconstructions/{XetHashes.ChunkHash("nope"u8)}", token));
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A range request is answered the way the reference service answers one: terms trimmed to
    /// the chunks that overlap the range, and the distance from the first kept chunk to the
    /// requested start in <c>offset_into_first_range</c>.
    /// </summary>
    [Test]
    public async Task Range_reconstructions_are_trimmed_to_whole_chunks()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var content = TestData.SplitMix64Bytes(10, 500_000);
        var upload = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);
        var fileId = upload.Files.Single().FileId;
        var token = await ReadTokenAsync(host);

        var chunks = Chunker.ChunkAll(content);
        var secondChunkStart = chunks[0].Length;
        var start = secondChunkStart + 100;
        var end = secondChunkStart + chunks[1].Length + chunks[2].Length + 5; // a few bytes into the fourth chunk

        var request = Request(HttpMethod.Get, $"/v2/reconstructions/{fileId}", token);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await host.HttpClient.SendAsync(request);
        var reconstruction = FileReconstruction.Parse(await response.Content.ReadAsByteArrayAsync());

        await Assert.That(reconstruction.OffsetIntoFirstRange).IsEqualTo(100L);
        await Assert.That(reconstruction.Terms.Single().Chunks).IsEqualTo(new ChunkRange(1, 4));
        await Assert.That(reconstruction.Terms.Single().UnpackedLength).IsEqualTo((long)chunks[1].Length + chunks[2].Length + chunks[3].Length);

        var past = Request(HttpMethod.Get, $"/v2/reconstructions/{fileId}", token);
        past.Headers.Range = new RangeHeaderValue(content.Length, content.Length + 10);
        using var refused = await host.HttpClient.SendAsync(past);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    [Test]
    public async Task Reconstructions_are_gzipped_when_asked()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var upload = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(11, 200_000))]);
        var token = await ReadTokenAsync(host);

        var request = Request(HttpMethod.Get, $"/v2/reconstructions/{upload.Files.Single().FileId}", token);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var response = await host.HttpClient.SendAsync(request);

        await Assert.That(response.Content.Headers.ContentEncoding).Contains("gzip");
        await using var decoded = new System.IO.Compression.GZipStream(await response.Content.ReadAsStreamAsync(), System.IO.Compression.CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        await decoded.CopyToAsync(buffer);
        FileReconstruction.Parse(buffer.ToArray());
    }

    [Test]
    public async Task Batch_reconstructions_share_fetch_info_across_files()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var a = TestData.SplitMix64Bytes(12, 300_000);
        var b = TestData.SplitMix64Bytes(13, 300_000);
        var upload = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("a.bin", a), XetUploadFile.FromBytes("b.bin", (byte[])[.. a, .. b])]);
        var token = await ReadTokenAsync(host);

        var body = Encoding.UTF8.GetBytes(
            $$"""[{"prefix":"default","hash":"{{upload.Files[0].FileId}}"},{"prefix":"default","hash":"{{upload.Files[1].FileId}}"}]""");
        var request = Request(HttpMethod.Post, "/v1/reconstructions", token, body);
        request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await host.HttpClient.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var files = json.RootElement.GetProperty("files");
        await Assert.That(files.EnumerateObject().Count()).IsEqualTo(2);
        await Assert.That(files.GetProperty(upload.Files[1].FileId.ToString()).GetProperty("terms").GetArrayLength()).IsGreaterThanOrEqualTo(1);
        await Assert.That(json.RootElement.GetProperty("fetch_info").EnumerateObject().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Global_deduplication_answers_with_an_hmac_keyed_shard_for_the_chunks_xorb()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var content = TestData.WithEligibleChunk(30, 400_000);
        var upload = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);
        var token = await ReadTokenAsync(host);

        var chunks = Chunker.ChunkAll(content);
        var eligible = chunks.Select(chunk => XetHashes.ChunkHash(chunk)).First(GlobalDeduplication.IsEligible);
        var other = chunks.Select(chunk => XetHashes.ChunkHash(chunk)).First(hash => !GlobalDeduplication.IsEligible(hash));

        using var response = await host.HttpClient.SendAsync(Request(HttpMethod.Get, $"/v1/chunks/default-merkledb/{eligible}", token));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/octet-stream");

        var shard = MdbShard.Parse(await response.Content.ReadAsByteArrayAsync());
        await Assert.That(shard.Files).IsEmpty();
        await Assert.That(shard.Xorbs.Count).IsEqualTo(1);
        await Assert.That(shard.Footer!.IsHmacProtected).IsTrue();
        await Assert.That(shard.Footer.ExpiresAt).IsNotNull();
        await Assert.That(shard.Xorbs[0].Chunks.Count).IsEqualTo(chunks.Count);
        await Assert.That(shard.Xorbs[0].SerializedLength).IsEqualTo((uint)(await host.Store.GetXorbAsync(shard.Xorbs[0].XorbHash))!.SerializedLength);

        // The listing is keyed, so the plain hash is not in it and the keyed one is: exactly the
        // matching step the client performs.
        await Assert.That(shard.Xorbs[0].Chunks.Any(chunk => chunk.Hash == eligible)).IsFalse();
        await Assert.That(shard.TryFindChunk(eligible, out _, out var index)).IsTrue();
        await Assert.That(index).IsEqualTo(chunks.FindIndex(chunk => XetHashes.ChunkHash(chunk) == eligible));

        // The response describes the whole xorb, so its neighbours match too, sampled or not.
        await Assert.That(shard.TryFindChunk(other, out _, out _)).IsTrue();

        using var miss = await host.HttpClient.SendAsync(Request(HttpMethod.Get, $"/v1/chunks/default-merkledb/{other}", token));
        await Assert.That(miss.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The dedupe response has the same shape as the one the reference service published: a
    /// footer, an HMAC key, no files, one xorb. Its bytes differ only where they must — the key,
    /// the timestamps and the xorb.
    /// </summary>
    [Test]
    public async Task Deduplication_shard_has_the_shape_of_the_reference_one()
    {
        var reference = MdbShard.Parse(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "ReferenceFiles", "ev-population.csv.shard.dedupe")));
        var (serialized, hash, _) = MakeXorb(14);
        var xorb = Cas.XorbIndexer.Index(serialized, hash);
        var now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

        var ours = Cas.DeduplicationShardBuilder.Build(xorb, XetHashes.ChunkHash("key"u8), now, TimeSpan.FromDays(14));
        var reparsed = MdbShard.Parse(ours.ToByteArray());

        await Assert.That(reparsed.Files.Count).IsEqualTo(reference.Files.Count);
        await Assert.That(reparsed.Xorbs.Count).IsEqualTo(reference.Xorbs.Count);
        await Assert.That(reparsed.Footer!.IsHmacProtected).IsEqualTo(reference.Footer!.IsHmacProtected);
        await Assert.That(reparsed.Footer.MaterializedBytes).IsEqualTo(reference.Footer.MaterializedBytes);
        await Assert.That(reparsed.Footer.ExpiresAt).IsEqualTo(now.AddDays(14));
        await Assert.That(reparsed.Xorbs[0].Chunks[1].ByteRangeStart).IsEqualTo(reparsed.Xorbs[0].Chunks[0].UnpackedLength);
    }

    [Test]
    public async Task Xorb_data_is_served_only_for_the_signed_range()
    {
        await using var host = await TestXetServer.StartAsync();
        using var client = host.CreateClient();
        var upload = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(15, 400_000))]);
        var token = await ReadTokenAsync(host);
        using var response = await host.HttpClient.SendAsync(Request(HttpMethod.Get, $"/v2/reconstructions/{upload.Files.Single().FileId}", token));
        var fetch = FileReconstruction.Parse(await response.Content.ReadAsByteArrayAsync()).Xorbs.Single().Value.Single();
        var signedRange = Download.XorbRangeFetcher.RangeHeaderFor(fetch);

        // As signed: the bytes, with a Content-Range saying which.
        var honest = new HttpRequestMessage(HttpMethod.Get, fetch.Url);
        honest.Headers.TryAddWithoutValidation("Range", signedRange);
        using var served = await host.HttpClient.SendAsync(honest);
        await Assert.That(served.StatusCode).IsEqualTo(HttpStatusCode.PartialContent);
        await Assert.That(served.Content.Headers.ContentRange!.From).IsEqualTo(fetch.Ranges[0].Bytes.Start);
        await Assert.That(served.Content.Headers.ContentLength).IsEqualTo(fetch.ByteCount);

        // A different range under the same signature: refused.
        var altered = new HttpRequestMessage(HttpMethod.Get, fetch.Url);
        altered.Headers.TryAddWithoutValidation("Range", "bytes=0-10");
        using var refused = await host.HttpClient.SendAsync(altered);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // No range at all: refused, not the whole object.
        using var whole = await host.HttpClient.GetAsync(fetch.Url);
        await Assert.That(whole.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // A tampered signature: refused.
        var tampered = new HttpRequestMessage(HttpMethod.Get, fetch.Url.ToString().Replace("Signature=", "Signature=x"));
        tampered.Headers.TryAddWithoutValidation("Range", signedRange);
        using var forged = await host.HttpClient.SendAsync(tampered);
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Signed_urls_expire()
    {
        var clock = new FakeTimeProvider();
        await using var host = await TestXetServer.StartAsync(new XetServerOptions { TimeProvider = clock, DownloadUrlLifetime = TimeSpan.FromMinutes(10) });
        using var client = host.CreateClient();
        var upload = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(16, 200_000))]);
        var token = await ReadTokenAsync(host);
        using var response = await host.HttpClient.SendAsync(Request(HttpMethod.Get, $"/v2/reconstructions/{upload.Files.Single().FileId}", token));
        var fetch = FileReconstruction.Parse(await response.Content.ReadAsByteArrayAsync()).Xorbs.Single().Value.Single();

        clock.Advance(TimeSpan.FromMinutes(11));
        var late = new HttpRequestMessage(HttpMethod.Get, fetch.Url);
        late.Headers.TryAddWithoutValidation("Range", Download.XorbRangeFetcher.RangeHeaderFor(fetch));
        using var refused = await host.HttpClient.SendAsync(late);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Telemetry_is_accepted_and_ignored()
    {
        await using var host = await TestXetServer.StartAsync();
        using var response = await host.HttpClient.PostAsync("/v1/telemetry", new StringContent("{}", Encoding.UTF8, "application/json"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>An upload-form shard for one file made of every chunk of one xorb, verification hashes and all.</summary>
    private static MdbShard ShardFor(MerkleHash xorbHash, List<byte[]> chunks)
    {
        var hashes = chunks.Select(chunk => XetHashes.ChunkHash(chunk)).ToArray();
        var nodes = chunks.Select((chunk, i) => (hashes[i], (ulong)chunk.Length)).ToArray();
        var total = (uint)chunks.Sum(chunk => chunk.Length);
        var listing = new List<ShardCasChunk>();
        var offset = 0u;
        foreach (var (hash, length) in nodes)
        {
            listing.Add(new ShardCasChunk(hash, offset, (uint)length));
            offset += (uint)length;
        }

        return new MdbShard
        {
            Files =
            [
                new ShardFileInfo(
                    XetHashes.FileHash(nodes),
                    [new ShardFileTerm(xorbHash, total, 0, (uint)chunks.Count, XetHashes.VerificationHash(hashes))])
                {
                    Sha256 = MerkleHash.FromHexOrder(System.Security.Cryptography.SHA256.HashData(chunks.SelectMany(chunk => chunk).ToArray())),
                },
            ],
            Xorbs = [new ShardCasInfo(xorbHash, total, 0, listing)],
        };
    }
}

/// <summary>A clock the test moves.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
