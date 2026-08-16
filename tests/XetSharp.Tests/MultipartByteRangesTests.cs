using System.Text;
using XetSharp.Cas;
using XetSharp.Download;

namespace XetSharp.Tests;

/// <summary>
/// A multi-range GET comes back as <c>multipart/byteranges</c>, which HttpClient hands over
/// unparsed. Reference-format cases are covered here; the pipeline tests exercise the same code
/// through a real HttpClient.
/// </summary>
public class MultipartByteRangesTests
{
    private const string Boundary = "3d6b6a416f9b5";

    [Test]
    public async Task Parses_the_parts_of_a_two_range_response()
    {
        var body = Build(
            ("bytes 0-4/1000", "hello"u8.ToArray()),
            ("bytes 100-104/1000", "world"u8.ToArray()));

        var parts = MultipartByteRanges.Parse(body, Boundary);

        await Assert.That(parts.Count).IsEqualTo(2);
        await Assert.That(parts[0].Range).IsEqualTo(new ByteRange(0, 4));
        await Assert.That(parts[0].Body.ToArray()).IsEquivalentTo("hello"u8.ToArray(), CollectionOrdering.Matching);
        await Assert.That(parts[1].Range).IsEqualTo(new ByteRange(100, 104));
        await Assert.That(parts[1].Body.ToArray()).IsEquivalentTo("world"u8.ToArray(), CollectionOrdering.Matching);
    }

    /// <summary>Part bodies are arbitrary binary, including the bytes that make up a boundary line.</summary>
    [Test]
    public async Task Keeps_binary_bodies_intact()
    {
        var payload = TestData.SplitMix64Bytes(seed: 11, count: 5000);
        var body = Build(("bytes 0-4999/5000", payload));

        var parts = MultipartByteRanges.Parse(body, Boundary);

        await Assert.That(parts.Single().Body.ToArray()).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Xorb bytes are arbitrary binary, so a part's data can contain the delimiter's bytes. Only a
    /// delimiter that starts its own line ends a part; anything else is data.
    /// </summary>
    [Test]
    public async Task Keeps_a_body_that_contains_the_delimiter_bytes_intact()
    {
        var payload = Concat(
            "before"u8.ToArray(),
            Encoding.ASCII.GetBytes($"--{Boundary} not a delimiter line"),
            [0x00, 0x0d, 0x0a],
            Encoding.ASCII.GetBytes($"trailing --{Boundary}"),
            "after"u8.ToArray());
        var body = Build(($"bytes 0-{payload.Length - 1}/{payload.Length}", payload));

        var parts = MultipartByteRanges.Parse(body, Boundary);

        await Assert.That(parts.Single().Body.ToArray()).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Ignores_a_preamble_and_an_epilogue()
    {
        var body = Concat(
            Encoding.ASCII.GetBytes("This is a multi-part message in MIME format.\r\n"),
            Build(("bytes 7-11/1000", "payload"u8.ToArray())),
            Encoding.ASCII.GetBytes("trailing junk\r\n"));

        var parts = MultipartByteRanges.Parse(body, Boundary);

        await Assert.That(parts.Single().Body.ToArray()).IsEquivalentTo("payload"u8.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Accepts_bare_line_feeds_and_lowercase_headers()
    {
        var body = Encoding.ASCII.GetBytes(
            $"--{Boundary}\ncontent-range: bytes 5-9/50\ncontent-type: application/octet-stream\n\nabcde\n--{Boundary}--\n");

        var parts = MultipartByteRanges.Parse(body, Boundary);

        await Assert.That(parts.Single().Range).IsEqualTo(new ByteRange(5, 9));
        await Assert.That(parts.Single().Body.ToArray()).IsEquivalentTo("abcde"u8.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Accepts_an_unknown_total_length()
    {
        var body = Build(("bytes 0-2/*", "abc"u8.ToArray()));

        await Assert.That(MultipartByteRanges.Parse(body, Boundary).Single().Range).IsEqualTo(new ByteRange(0, 2));
    }

    [Test]
    public async Task Rejects_a_part_without_a_content_range()
    {
        var body = Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Type: application/octet-stream\r\n\r\nabc\r\n--{Boundary}--\r\n");

        await Assert.That(() => MultipartByteRanges.Parse(body, Boundary)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Rejects_a_body_that_ends_mid_part()
    {
        var body = Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Range: bytes 0-99/100\r\n\r\nabc");

        await Assert.That(() => MultipartByteRanges.Parse(body, Boundary)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Rejects_a_body_with_no_boundary_at_all()
    {
        await Assert.That(() => MultipartByteRanges.Parse("not multipart at all"u8.ToArray(), Boundary))
            .Throws<InvalidDataException>();
    }

    private static byte[] Build(params (string ContentRange, byte[] Body)[] parts)
    {
        var body = new List<byte>();
        foreach (var (contentRange, content) in parts)
        {
            body.AddRange(Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Type: application/octet-stream\r\nContent-Range: {contentRange}\r\n\r\n"));
            body.AddRange(content);
            body.AddRange("\r\n"u8.ToArray());
        }

        body.AddRange(Encoding.ASCII.GetBytes($"--{Boundary}--\r\n"));
        return [.. body];
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];
}
