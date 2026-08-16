using System.Text.Json;
using XetSharp.Json;

namespace XetSharp.Cas;

/// <summary>A range of chunk indices within a xorb, <c>[Start, End)</c> — the end is exclusive.</summary>
public readonly record struct ChunkRange(int Start, int End)
{
    public int Count => End - Start;

    public bool Contains(int chunkIndex) => chunkIndex >= Start && chunkIndex < End;

    public override string ToString() => $"[{Start}, {End})";
}

/// <summary>
/// A byte range with an <em>inclusive</em> end, matching HTTP's <c>Range</c> header and the CAS
/// API's <c>bytes</c> descriptors.
/// </summary>
public readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;

    public override string ToString() => $"{Start}-{End}";
}

/// <summary>
/// One piece of a file: a run of chunks from one xorb. Concatenating every term's decompressed
/// chunks, in order, reproduces the file.
/// </summary>
/// <param name="Xorb">The xorb holding the chunks.</param>
/// <param name="Chunks">The chunk index range, end-exclusive.</param>
/// <param name="UnpackedLength">
/// What the term's chunks MUST decompress to in total. The protocol requires clients to check this.
/// </param>
public sealed record ReconstructionTerm(MerkleHash Xorb, ChunkRange Chunks, long UnpackedLength);

/// <summary>A run of chunks in a xorb, and the byte range of the serialized xorb that holds them.</summary>
public sealed record XorbRangeDescriptor(ChunkRange Chunks, ByteRange Bytes);

/// <summary>
/// One signed request against a xorb. The URL authorizes exactly the byte ranges listed in
/// <see cref="Ranges"/>, in that order, so they have to be fetched together and unaltered.
/// </summary>
public sealed record XorbFetch(Uri Url, IReadOnlyList<XorbRangeDescriptor> Ranges)
{
    /// <summary>Total bytes this fetch will transfer.</summary>
    public long ByteCount => Ranges.Sum(range => range.Bytes.Length);
}

/// <summary>
/// A CAS reconstruction response: how to rebuild a file (or a range of one) from xorb data. Both
/// the v2 shape (<c>xorbs</c>) and the deprecated v1 shape (<c>fetch_info</c>) parse into this.
/// </summary>
public sealed record FileReconstruction
{
    /// <summary>
    /// How many bytes of the first term's decompressed data to discard. Non-zero only for range
    /// queries, because data always arrives in whole chunks and a range may start mid-chunk.
    /// </summary>
    public required long OffsetIntoFirstRange { get; init; }

    /// <summary>The terms, in file order.</summary>
    public required IReadOnlyList<ReconstructionTerm> Terms { get; init; }

    /// <summary>Where to fetch each referenced xorb's data from, keyed by xorb hash.</summary>
    public required IReadOnlyDictionary<MerkleHash, IReadOnlyList<XorbFetch>> Xorbs { get; init; }

    /// <summary>Bytes the terms decompress to in total, before <see cref="OffsetIntoFirstRange"/>.</summary>
    public long UnpackedLength => Terms.Sum(term => term.UnpackedLength);

    /// <summary>Bytes this reconstruction yields once the leading offset is dropped.</summary>
    public long ContentLength => UnpackedLength - OffsetIntoFirstRange;

    /// <summary>Parses either API version's JSON body, validating it before it is acted on.</summary>
    public static FileReconstruction Parse(ReadOnlySpan<byte> json)
    {
        ReconstructionJson? payload;
        try
        {
            payload = JsonSerializer.Deserialize(json, XetJsonContext.Default.ReconstructionJson);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The reconstruction response is not valid JSON.", exception);
        }

        return FromJson(payload ?? throw new InvalidDataException("The reconstruction response is empty."));
    }

    internal static FileReconstruction FromJson(ReconstructionJson payload)
    {
        var terms = (payload.Terms ?? []).Select(ToTerm).ToArray();
        var xorbs = payload.Xorbs is not null
            ? payload.Xorbs.ToDictionary(entry => ParseHash(entry.Key), entry => ToFetches(entry.Value))
            : (payload.FetchInfo ?? []).ToDictionary(entry => ParseHash(entry.Key), entry => ToFetchesV1(entry.Value));

        var reconstruction = new FileReconstruction
        {
            OffsetIntoFirstRange = payload.OffsetIntoFirstRange,
            Terms = terms,
            Xorbs = xorbs,
        };

        reconstruction.Validate();
        return reconstruction;
    }

    /// <summary>
    /// Finds the fetched range holding <paramref name="chunkIndex"/> of <paramref name="xorb"/>.
    /// Chunks are commonly reused across terms, so this is a lookup rather than a walk.
    /// </summary>
    internal bool TryFindRange(MerkleHash xorb, int chunkIndex, out XorbFetch fetch, out XorbRangeDescriptor range)
    {
        if (Xorbs.TryGetValue(xorb, out var fetches))
        {
            foreach (var candidate in fetches)
            {
                foreach (var candidateRange in candidate.Ranges)
                {
                    if (candidateRange.Chunks.Contains(chunkIndex))
                    {
                        (fetch, range) = (candidate, candidateRange);
                        return true;
                    }
                }
            }
        }

        (fetch, range) = (null!, null!);
        return false;
    }

    private void Validate()
    {
        if (OffsetIntoFirstRange < 0)
        {
            throw new InvalidDataException($"offset_into_first_range is negative ({OffsetIntoFirstRange}).");
        }

        if (Terms.Count == 0)
        {
            if (OffsetIntoFirstRange != 0)
            {
                throw new InvalidDataException("A reconstruction with no terms cannot have a non-zero offset.");
            }

            return;
        }

        if (OffsetIntoFirstRange >= Terms[0].UnpackedLength && Terms[0].UnpackedLength > 0)
        {
            throw new InvalidDataException(
                $"offset_into_first_range ({OffsetIntoFirstRange}) does not fall inside the first term " +
                $"({Terms[0].UnpackedLength} bytes).");
        }

        foreach (var term in Terms)
        {
            if (term.Chunks.Count <= 0)
            {
                throw new InvalidDataException($"Term for xorb {term.Xorb} has an empty chunk range {term.Chunks}.");
            }

            if (term.UnpackedLength <= 0)
            {
                throw new InvalidDataException($"Term for xorb {term.Xorb} declares an unpacked length of {term.UnpackedLength}.");
            }

            for (var chunk = term.Chunks.Start; chunk < term.Chunks.End; chunk++)
            {
                if (!TryFindRange(term.Xorb, chunk, out _, out _))
                {
                    throw new InvalidDataException(
                        $"The reconstruction does not say where to fetch chunk {chunk} of xorb {term.Xorb} from.");
                }
            }
        }
    }

    private static ReconstructionTerm ToTerm(TermJson term) => new(
        ParseHash(term.Hash),
        ToChunkRange(term.Range),
        term.UnpackedLength);

    private static IReadOnlyList<XorbFetch> ToFetches(List<FetchJson>? fetches) =>
    [
        .. (fetches ?? []).Select(fetch => new XorbFetch(
            ParseUrl(fetch.Url),
            [.. (fetch.Ranges ?? []).Select(range => new XorbRangeDescriptor(ToChunkRange(range.Chunks), ToByteRange(range.Bytes)))])),
    ];

    private static IReadOnlyList<XorbFetch> ToFetchesV1(List<FetchInfoJson>? fetches) =>
    [
        .. (fetches ?? []).Select(fetch => new XorbFetch(
            ParseUrl(fetch.Url),
            [new XorbRangeDescriptor(ToChunkRange(fetch.Range), ToByteRange(fetch.UrlRange))])),
    ];

    private static ChunkRange ToChunkRange(RangeJson? range)
    {
        if (range is null)
        {
            throw new InvalidDataException("The reconstruction response is missing a chunk range.");
        }

        if (range.Start < 0 || range.End < range.Start)
        {
            throw new InvalidDataException($"The reconstruction response carries an invalid chunk range [{range.Start}, {range.End}).");
        }

        return new ChunkRange((int)range.Start, (int)range.End);
    }

    private static ByteRange ToByteRange(RangeJson? range)
    {
        if (range is null)
        {
            throw new InvalidDataException("The reconstruction response is missing a byte range.");
        }

        if (range.Start < 0 || range.End < range.Start)
        {
            throw new InvalidDataException($"The reconstruction response carries an invalid byte range {range.Start}-{range.End}.");
        }

        return new ByteRange(range.Start, range.End);
    }

    private static MerkleHash ParseHash(string? hash) =>
        MerkleHash.TryParse(hash, out var parsed)
            ? parsed
            : throw new InvalidDataException($"The reconstruction response carries a malformed hash: '{hash}'.");

    private static Uri ParseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"
            ? parsed
            : throw new InvalidDataException($"The reconstruction response carries a malformed fetch URL: '{url}'.");
}
