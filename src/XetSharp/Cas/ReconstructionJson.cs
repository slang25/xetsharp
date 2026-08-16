using System.Text.Json.Serialization;

namespace XetSharp.Cas;

/// <summary>
/// The wire shape of a reconstruction response. One type covers both API versions: v2 fills
/// <see cref="Xorbs"/>, the deprecated v1 fills <see cref="FetchInfo"/>, and the rest is shared.
/// </summary>
internal sealed class ReconstructionJson
{
    [JsonPropertyName("offset_into_first_range")]
    public long OffsetIntoFirstRange { get; set; }

    [JsonPropertyName("terms")]
    public List<TermJson>? Terms { get; set; }

    [JsonPropertyName("xorbs")]
    public Dictionary<string, List<FetchJson>>? Xorbs { get; set; }

    [JsonPropertyName("fetch_info")]
    public Dictionary<string, List<FetchInfoJson>>? FetchInfo { get; set; }
}

internal sealed class TermJson
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("unpacked_length")]
    public long UnpackedLength { get; set; }

    [JsonPropertyName("range")]
    public RangeJson? Range { get; set; }
}

/// <summary>A <c>{ start, end }</c> pair: chunk indices are end-exclusive, byte offsets inclusive.</summary>
internal sealed class RangeJson
{
    [JsonPropertyName("start")]
    public long Start { get; set; }

    [JsonPropertyName("end")]
    public long End { get; set; }
}

/// <summary>A v2 <c>XorbMultiRangeFetch</c>.</summary>
internal sealed class FetchJson
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("ranges")]
    public List<RangeDescriptorJson>? Ranges { get; set; }
}

internal sealed class RangeDescriptorJson
{
    [JsonPropertyName("chunks")]
    public RangeJson? Chunks { get; set; }

    [JsonPropertyName("bytes")]
    public RangeJson? Bytes { get; set; }
}

/// <summary>A v1 <c>CASReconstructionFetchInfo</c>: one chunk range and one byte range per URL.</summary>
internal sealed class FetchInfoJson
{
    [JsonPropertyName("range")]
    public RangeJson? Range { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("url_range")]
    public RangeJson? UrlRange { get; set; }
}
