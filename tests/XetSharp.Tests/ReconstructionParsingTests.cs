using System.Text;
using XetSharp.Cas;

namespace XetSharp.Tests;

/// <summary>
/// Parsing checks against reconstruction responses captured from the live CAS service; see
/// CapturedResponses/README.md.
/// </summary>
public class ReconstructionParsingTests
{
    public static byte[] Captured(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "CapturedResponses", name));

    [Test]
    public async Task Parses_a_whole_file_reconstruction()
    {
        var reconstruction = FileReconstruction.Parse(Captured("ev-population.v2.json"));

        await Assert.That(reconstruction.OffsetIntoFirstRange).IsEqualTo(0);
        await Assert.That(reconstruction.UnpackedLength).IsEqualTo(ReferenceFiles.FileLength);
        var term = reconstruction.Terms.Single();
        await Assert.That(term.Xorb).IsEqualTo(MerkleHash.Parse(ReferenceFiles.XorbHash));
        await Assert.That(term.Chunks).IsEqualTo(new ChunkRange(0, ReferenceFiles.Chunks.Length));

        var fetch = reconstruction.Xorbs[term.Xorb].Single();
        await Assert.That(fetch.Url.Host).IsEqualTo("us.aws.cdn.hf.co");
        await Assert.That(fetch.Ranges.Single().Chunks).IsEqualTo(term.Chunks);
        await Assert.That(fetch.Ranges.Single().Bytes.Start).IsEqualTo(0);
    }

    /// <summary>
    /// The deprecated v1 shape carries the same information in a different arrangement, so the two
    /// must parse to the same reconstruction — modulo the signed URLs, which differ per request.
    /// </summary>
    [Test]
    public async Task Parses_the_v1_shape_to_the_same_model_as_v2()
    {
        var v1 = FileReconstruction.Parse(Captured("ev-population-range.v1.json"));
        var v2 = FileReconstruction.Parse(Captured("ev-population-range.v2.json"));

        await Assert.That(v1.OffsetIntoFirstRange).IsEqualTo(v2.OffsetIntoFirstRange);
        await Assert.That(v1.Terms).IsEquivalentTo(v2.Terms, CollectionOrdering.Matching);
        await Assert.That(v1.Xorbs.Keys).IsEquivalentTo(v2.Xorbs.Keys, CollectionOrdering.Matching);
        foreach (var (xorb, fetches) in v1.Xorbs)
        {
            await Assert.That(fetches.SelectMany(fetch => fetch.Ranges))
                .IsEquivalentTo(v2.Xorbs[xorb].SelectMany(fetch => fetch.Ranges), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Parses_a_file_spread_over_several_xorbs()
    {
        var reconstruction = FileReconstruction.Parse(Captured("gpt2-model-safetensors.v2.json"));

        await Assert.That(reconstruction.Terms.Count).IsEqualTo(9);
        await Assert.That(reconstruction.Xorbs.Count).IsEqualTo(9);
        await Assert.That(reconstruction.Terms.Select(term => term.Xorb).Distinct().Count()).IsEqualTo(9);
        foreach (var term in reconstruction.Terms)
        {
            var ranges = reconstruction.Xorbs[term.Xorb].SelectMany(fetch => fetch.Ranges).ToList();
            await Assert.That(ranges.Any(range => range.Chunks.Contains(term.Chunks.Start))).IsTrue();
        }
    }

    /// <summary>A range starting mid-chunk is the case <c>offset_into_first_range</c> exists for.</summary>
    [Test]
    public async Task Parses_a_range_that_starts_inside_a_chunk()
    {
        var reconstruction = FileReconstruction.Parse(Captured("gpt2-model-safetensors-range.v2.json"));

        await Assert.That(reconstruction.OffsetIntoFirstRange).IsEqualTo(24735);
        await Assert.That(reconstruction.Terms.Single().Chunks).IsEqualTo(new ChunkRange(417, 422));
        await Assert.That(reconstruction.ContentLength).IsEqualTo(261031 - 24735);
    }

    [Test]
    [Arguments("""{"offset_into_first_range":0,"terms":[{"hash":"00","unpacked_length":1,"range":{"start":0,"end":1}}],"xorbs":{}}""")]
    [Arguments("""{"offset_into_first_range":-1,"terms":[],"xorbs":{}}""")]
    [Arguments("""{"offset_into_first_range":0,"terms":[{"hash":"0000000000000000000000000000000000000000000000000000000000000000","unpacked_length":1,"range":{"start":3,"end":1}}],"xorbs":{}}""")]
    public async Task Rejects_a_malformed_response(string json) =>
        await Assert.That(() => FileReconstruction.Parse(Encoding.UTF8.GetBytes(json))).Throws<InvalidDataException>();

    /// <summary>
    /// A term whose chunks nothing tells us how to fetch would fail much later, mid-download, with a
    /// far less obvious error.
    /// </summary>
    [Test]
    public async Task Rejects_a_term_no_fetch_covers()
    {
        const string json = """
        {
          "offset_into_first_range": 0,
          "terms": [{ "hash": "eea25d6ee393ccae385820daed127b96ef0ea034dfb7cf6da3a950ce334b7632", "unpacked_length": 100, "range": { "start": 0, "end": 4 } }],
          "xorbs": {
            "eea25d6ee393ccae385820daed127b96ef0ea034dfb7cf6da3a950ce334b7632": [
              { "url": "https://example.invalid/xorb", "ranges": [{ "chunks": { "start": 0, "end": 2 }, "bytes": { "start": 0, "end": 99 } }] }
            ]
          }
        }
        """;

        await Assert.That(() => FileReconstruction.Parse(Encoding.UTF8.GetBytes(json)))
            .Throws<InvalidDataException>()
            .WithMessageContaining("chunk 2");
    }

    /// <summary>
    /// An absent protocol field is not an empty one. Treating it as empty turns a response that says
    /// nothing into a reconstruction of nothing, which a range download would report as a success
    /// having written no bytes.
    /// </summary>
    [Test]
    [Arguments("{}")]
    [Arguments("""{"offset_into_first_range":0}""")]
    [Arguments("""{"offset_into_first_range":0,"xorbs":{}}""")]
    [Arguments("""{"offset_into_first_range":0,"terms":[{"hash":"eea25d6ee393ccae385820daed127b96ef0ea034dfb7cf6da3a950ce334b7632","unpacked_length":100,"range":{"start":0,"end":4}}]}""")]
    public async Task Rejects_a_response_missing_a_protocol_field(string json) =>
        await Assert.That(() => FileReconstruction.Parse(Encoding.UTF8.GetBytes(json))).Throws<InvalidDataException>();

    /// <summary>An empty file has nothing to fetch, and says so explicitly.</summary>
    [Test]
    public async Task Parses_an_empty_file()
    {
        var reconstruction = FileReconstruction.Parse("""{"offset_into_first_range":0,"terms":[],"xorbs":{}}"""u8);

        await Assert.That(reconstruction.Terms).IsEmpty();
        await Assert.That(reconstruction.ContentLength).IsEqualTo(0);
    }

    /// <summary>
    /// Chunk indices live in a 32-bit space. A value past it would wrap into a negative index that
    /// reads as a perfectly ordinary range.
    /// </summary>
    [Test]
    public async Task Rejects_a_chunk_index_that_does_not_fit_in_an_int()
    {
        const string json = """
        {
          "offset_into_first_range": 0,
          "terms": [{ "hash": "eea25d6ee393ccae385820daed127b96ef0ea034dfb7cf6da3a950ce334b7632", "unpacked_length": 100, "range": { "start": 0, "end": 2147483648 } }],
          "xorbs": {}
        }
        """;

        await Assert.That(() => FileReconstruction.Parse(Encoding.UTF8.GetBytes(json))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Rejects_a_fetch_url_that_is_not_http()
    {
        const string json = """
        {
          "offset_into_first_range": 0,
          "terms": [],
          "xorbs": {
            "eea25d6ee393ccae385820daed127b96ef0ea034dfb7cf6da3a950ce334b7632": [
              { "url": "file:///etc/passwd", "ranges": [] }
            ]
          }
        }
        """;

        await Assert.That(() => FileReconstruction.Parse(Encoding.UTF8.GetBytes(json))).Throws<InvalidDataException>();
    }
}
