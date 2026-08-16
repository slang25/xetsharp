using System.Net;
using XetSharp.Cas;
using XetSharp.Download;
using XetSharp.Hashing;
using XetSharp.Xorbs;

namespace XetSharp.Tests;

/// <summary>
/// The download pipeline, driven end to end against a stand-in CDN that serves the real reference
/// xorb bytes. The vendored prefix is byte-identical to what the live CDN returns for the same
/// range, so "assemble a file from xorb data" is exercised on genuine protocol output.
/// </summary>
public class DownloadPipelineTests
{
    private static readonly byte[] Serialized = ReferenceFiles.Read("ev-population.xorb.first-10-chunks");

    /// <summary>Byte offsets of chunk records 0–10, so a chunk range maps to a byte range.</summary>
    private static readonly int[] RecordOffsets = IndexRecords();

    private static readonly List<byte[]> Chunks = XorbSerializer.Deserialize(Serialized);

    private static readonly MerkleHash Xorb = MerkleHash.Parse(ReferenceFiles.XorbHash);

    [Test]
    public async Task Reassembles_a_file_from_one_term()
    {
        var result = await DownloadAsync(Reconstruction(Term(0, 10)));

        await Assert.That(result.Bytes).IsEquivalentTo(Expected(0, 10), CollectionOrdering.Matching);
        await Assert.That(result.Download.BytesWritten).IsEqualTo(Expected(0, 10).Length);
    }

    /// <summary>
    /// Terms may revisit chunks another term already used — that is what deduplication produces —
    /// and the data is fetched once and written twice.
    /// </summary>
    [Test]
    public async Task Reuses_chunks_across_terms_without_refetching()
    {
        var reconstruction = Reconstruction(Term(0, 3), Term(1, 3), Term(0, 1));

        var result = await DownloadAsync(reconstruction);

        await Assert.That(result.Bytes)
            .IsEquivalentTo([.. Expected(0, 3), .. Expected(1, 3), .. Expected(0, 1)], CollectionOrdering.Matching);
        await Assert.That(result.Handler.Requests.Count).IsEqualTo(1);
    }

    /// <summary>
    /// A range request lands mid-chunk at both ends: the head is dropped by
    /// <c>offset_into_first_range</c> and the tail by the caller's byte limit.
    /// </summary>
    [Test]
    public async Task Trims_the_head_and_tail_of_a_range_request()
    {
        var whole = Expected(0, 4);
        const int offset = 40_000;
        const int length = 30_000;

        var result = await DownloadAsync(Reconstruction(Term(0, 4)) with { OffsetIntoFirstRange = offset }, maxBytes: length);

        await Assert.That(result.Bytes).IsEquivalentTo(whole[offset..(offset + length)], CollectionOrdering.Matching);
    }

    /// <summary>The skip can be longer than the first chunk, in which case whole chunks fall away.</summary>
    [Test]
    public async Task Skips_whole_chunks_when_the_offset_covers_them()
    {
        var offset = Chunks[0].Length + Chunks[1].Length + 17;

        var result = await DownloadAsync(Reconstruction(Term(0, 4)) with { OffsetIntoFirstRange = offset });

        await Assert.That(result.Bytes).IsEquivalentTo(Expected(0, 4)[offset..], CollectionOrdering.Matching);
    }

    /// <summary>
    /// Several ranges of one xorb travel in a single request, and the response comes back as
    /// multipart — the case a client cannot lean on HttpClient for.
    /// </summary>
    [Test]
    public async Task Fetches_several_ranges_of_a_xorb_in_one_multipart_request()
    {
        var fetch = new XorbFetch(new Uri("https://cdn.invalid/xorb"), [Descriptor(0, 2), Descriptor(5, 8)]);
        var reconstruction = new FileReconstruction
        {
            OffsetIntoFirstRange = 0,
            Terms = [Term(0, 2), Term(5, 8)],
            Xorbs = new Dictionary<MerkleHash, IReadOnlyList<XorbFetch>> { [Xorb] = [fetch] },
        };

        var result = await DownloadAsync(reconstruction);

        await Assert.That(result.Bytes).IsEquivalentTo([.. Expected(0, 2), .. Expected(5, 8)], CollectionOrdering.Matching);
        await Assert.That(result.Handler.Requests.Count).IsEqualTo(1);
        await Assert.That(result.Handler.RangeHeaders.Single())
            .IsEqualTo($"bytes={RecordOffsets[0]}-{RecordOffsets[2] - 1},{RecordOffsets[5]}-{RecordOffsets[8] - 1}");
    }

    /// <summary>
    /// The signature on a xorb URL covers the exact Range header, so when the URL spells the range
    /// out we send that string back verbatim rather than one we formatted ourselves.
    /// </summary>
    [Test]
    public async Task Sends_the_signed_range_header_verbatim()
    {
        var signed = $"bytes%3D{RecordOffsets[0]}-{RecordOffsets[2] - 1}";
        var fetch = new XorbFetch(
            new Uri($"https://cdn.invalid/xorb?X-Xet-Signed-Range={signed}&Signature=abc"),
            [Descriptor(0, 2)]);
        var reconstruction = new FileReconstruction
        {
            OffsetIntoFirstRange = 0,
            Terms = [Term(0, 2)],
            Xorbs = new Dictionary<MerkleHash, IReadOnlyList<XorbFetch>> { [Xorb] = [fetch] },
        };

        var result = await DownloadAsync(reconstruction);

        await Assert.That(result.Handler.RangeHeaders.Single()).IsEqualTo($"bytes={RecordOffsets[0]}-{RecordOffsets[2] - 1}");
        await Assert.That(result.Bytes).IsEquivalentTo(Expected(0, 2), CollectionOrdering.Matching);
    }

    /// <summary>
    /// One xorb can carry several fetch entries — the spec splits them when a single URL would grow
    /// too long — and a term may draw on more than one of them, so it has to be written out as
    /// several segments in order.
    /// </summary>
    [Test]
    public async Task Assembles_a_term_that_spans_two_fetches_of_one_xorb()
    {
        var reconstruction = new FileReconstruction
        {
            OffsetIntoFirstRange = 0,
            Terms = [Term(0, 10)],
            Xorbs = new Dictionary<MerkleHash, IReadOnlyList<XorbFetch>>
            {
                [Xorb] =
                [
                    new XorbFetch(new Uri("https://cdn.invalid/xorb#first"), [Descriptor(0, 3)]),
                    new XorbFetch(new Uri("https://cdn.invalid/xorb#rest"), [Descriptor(3, 10)]),
                ],
            },
        };

        var result = await DownloadAsync(reconstruction);

        await Assert.That(result.Bytes).IsEquivalentTo(Expected(0, 10), CollectionOrdering.Matching);
        await Assert.That(result.Handler.Requests.Count).IsEqualTo(2);
    }

    /// <summary>
    /// Re-deriving the file hash from the chunks that arrived is the strongest check available: it
    /// proves the assembled bytes are the file that was asked for.
    /// </summary>
    [Test]
    public async Task Verifies_the_assembled_data_against_the_file_id()
    {
        var fileId = XetHashes.FileHash(ReferenceFiles.Chunks.AsSpan(0, 10));

        var result = await DownloadAsync(Reconstruction(Term(0, 10)), expectedFileId: fileId);

        await Assert.That(result.Download.FileHash).IsEqualTo(fileId);
    }

    [Test]
    public async Task Rejects_data_that_does_not_hash_to_the_expected_file_id()
    {
        var wrongFileId = XetHashes.FileHash(ReferenceFiles.Chunks.AsSpan(0, 9));

        await Assert.That(() => DownloadAsync(Reconstruction(Term(0, 10)), expectedFileId: wrongFileId))
            .Throws<XetVerificationException>();
    }

    [Test]
    public async Task Rejects_a_sha256_that_does_not_match()
    {
        await Assert.That(() => DownloadAsync(Reconstruction(Term(0, 2)), expectedSha256: new string('a', 64)))
            .Throws<XetVerificationException>();
    }

    /// <summary>
    /// The protocol requires the per-term length check; without it a truncated xorb would produce a
    /// short file that looks fine.
    /// </summary>
    [Test]
    public async Task Rejects_a_term_that_decompresses_to_the_wrong_length()
    {
        var term = new ReconstructionTerm(Xorb, new ChunkRange(0, 2), UnpackedLength: 999_999);
        var reconstruction = Reconstruction(term);

        await Assert.That(() => DownloadAsync(reconstruction))
            .Throws<XetVerificationException>();
    }

    [Test]
    public async Task Rejects_a_short_response_from_the_cdn()
    {
        var reconstruction = Reconstruction(Term(0, 2));

        await Assert.That(() => DownloadAsync(reconstruction, serve: _ => FakeHttpHandler.Bytes([1, 2, 3])))
            .Throws<InvalidDataException>();
    }

    /// <summary>
    /// A cache serving a same-sized slice from the wrong offset would survive every length check and
    /// put the wrong bytes into the file, and a range download has no whole-file hash to catch it.
    /// The 206 says which bytes it carries; that has to be the ones we asked for.
    /// </summary>
    [Test]
    public async Task Rejects_a_partial_response_covering_a_different_range()
    {
        var descriptor = Descriptor(0, 2);

        // Well-formed xorb data of exactly the right length — but the server says it starts
        // somewhere else, which is the one signal that it is not the slice that was asked for.
        await Assert.That(() => DownloadAsync(
                Reconstruction(Term(0, 2)),
                serve: _ => FakeHttpHandler.Bytes(
                    Serialized[(int)descriptor.Bytes.Start..(int)(descriptor.Bytes.End + 1)],
                    rangeStart: descriptor.Bytes.Start + 1)))
            .Throws<InvalidDataException>();
    }

    /// <summary>A 206 that does not say what it carries cannot be matched to what was asked for.</summary>
    [Test]
    public async Task Rejects_a_partial_response_without_a_content_range()
    {
        var descriptor = Descriptor(0, 2);

        await Assert.That(() => DownloadAsync(
                Reconstruction(Term(0, 2)),
                serve: _ => FakeHttpHandler.Bytes(Serialized[..(int)descriptor.Bytes.Length])))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Surfaces_a_rejected_signed_url()
    {
        await Assert.That(() => DownloadAsync(Reconstruction(Term(0, 2)), serve: _ => FakeHttpHandler.Status(HttpStatusCode.Forbidden)))
            .Throws<XetApiException>();
    }

    /// <summary>
    /// A memory budget smaller than a single range must not deadlock: the range the writer is
    /// waiting on is always allowed through, whatever the budget says.
    /// </summary>
    [Test]
    public async Task Downloads_a_range_larger_than_the_buffer_budget()
    {
        var options = new XetDownloadOptions { MaxBufferedBytes = 1024, MaxConcurrentDownloads = 2 };

        var result = await DownloadAsync(Reconstruction(Term(0, 4), Term(4, 10)), options: options);

        await Assert.That(result.Bytes).IsEquivalentTo(Expected(0, 10), CollectionOrdering.Matching);
    }

    /// <summary>Independent terms in different fetches are downloaded concurrently, not one by one.</summary>
    [Test]
    public async Task Fetches_upcoming_ranges_ahead_of_the_writer()
    {
        var fetches = new Dictionary<MerkleHash, IReadOnlyList<XorbFetch>>
        {
            [Xorb] =
            [
                new XorbFetch(new Uri("https://cdn.invalid/xorb#a"), [Descriptor(0, 4)]),
                new XorbFetch(new Uri("https://cdn.invalid/xorb#b"), [Descriptor(4, 10)]),
            ],
        };
        var reconstruction = new FileReconstruction
        {
            OffsetIntoFirstRange = 0,
            Terms = [Term(0, 4), Term(4, 10)],
            Xorbs = fetches,
        };

        var started = 0;
        var bothStarted = new TaskCompletionSource();
        var result = await DownloadAsync(reconstruction, serve: request =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.SetResult();
            }

            // Neither response is produced until both requests have arrived, so the test hangs
            // rather than passes if the second fetch waits for the first to be written out.
            bothStarted.Task.Wait(TimeSpan.FromSeconds(10));
            return ServeFromXorb(request);
        });

        await Assert.That(result.Bytes).IsEquivalentTo(Expected(0, 10), CollectionOrdering.Matching);
        await Assert.That(started).IsEqualTo(2);
    }

    private static async Task<(byte[] Bytes, XetDownloadResult Download, FakeHttpHandler Handler)> DownloadAsync(
        FileReconstruction reconstruction,
        long? maxBytes = null,
        MerkleHash? expectedFileId = null,
        string? expectedSha256 = null,
        XetDownloadOptions? options = null,
        Func<HttpRequestMessage, HttpResponseMessage>? serve = null)
    {
        var handler = new FakeHttpHandler(serve ?? ServeFromXorb);
        using var httpClient = new HttpClient(handler);
        options ??= XetDownloadOptions.Default;
        var writer = new ReconstructionWriter(new XorbRangeFetcher(httpClient, options.MaxConcurrentDownloads), options);

        using var destination = new MemoryStream();
        var download = await writer.WriteAsync(reconstruction, destination, maxBytes, expectedFileId, expectedSha256);
        return (destination.ToArray(), download, handler);
    }

    /// <summary>Serves byte ranges out of the reference xorb, the way the CDN does.</summary>
    private static HttpResponseMessage ServeFromXorb(HttpRequestMessage request)
    {
        var header = request.Headers.GetValues("Range").Single();
        var ranges = header["bytes=".Length..]
            .Split(',')
            .Select(part => part.Split('-'))
            .Select(part => (Start: long.Parse(part[0]), End: long.Parse(part[1])))
            .ToArray();

        static byte[] Slice((long Start, long End) range) => Serialized[(int)range.Start..(int)(range.End + 1)];

        return ranges.Length == 1
            ? FakeHttpHandler.Bytes(Slice(ranges[0]), rangeStart: ranges[0].Start)
            : FakeHttpHandler.Multipart("boundary42", [.. ranges.Select(range => (range.Start, range.End, Slice(range)))]);
    }

    private static FileReconstruction Reconstruction(params ReconstructionTerm[] terms)
    {
        var covered = new ChunkRange(terms.Min(term => term.Chunks.Start), terms.Max(term => term.Chunks.End));
        return new FileReconstruction
        {
            OffsetIntoFirstRange = 0,
            Terms = terms,
            Xorbs = new Dictionary<MerkleHash, IReadOnlyList<XorbFetch>>
            {
                [Xorb] = [new XorbFetch(new Uri("https://cdn.invalid/xorb"), [Descriptor(covered.Start, covered.End)])],
            },
        };
    }

    private static ReconstructionTerm Term(int chunkStart, int chunkEnd) => new(
        Xorb,
        new ChunkRange(chunkStart, chunkEnd),
        Enumerable.Range(chunkStart, chunkEnd - chunkStart).Sum(index => (long)Chunks[index].Length));

    private static XorbRangeDescriptor Descriptor(int chunkStart, int chunkEnd) => new(
        new ChunkRange(chunkStart, chunkEnd),
        new ByteRange(RecordOffsets[chunkStart], RecordOffsets[chunkEnd] - 1));

    private static byte[] Expected(int chunkStart, int chunkEnd) =>
        [.. Chunks.Skip(chunkStart).Take(chunkEnd - chunkStart).SelectMany(chunk => chunk)];

    private static int[] IndexRecords()
    {
        var offsets = new List<int> { 0 };
        var reader = new XorbChunkReader(Serialized);
        while (reader.TryReadRawChunk(out _, out _))
        {
            offsets.Add(reader.BytesConsumed);
        }

        return [.. offsets];
    }
}
