using System.Text.Json.Serialization;

namespace XetSharp.Cas;

/// <summary>The body of a successful <c>POST /v1/xorbs/{prefix}/{hash}</c>.</summary>
internal sealed class UploadXorbResponseJson
{
    /// <summary>
    /// False when the server already held this xorb. Content addressing makes that an ordinary
    /// outcome rather than a failure.
    /// </summary>
    [JsonPropertyName("was_inserted")]
    public bool WasInserted { get; set; }
}

/// <summary>The body of a successful <c>POST /v1/shards</c>.</summary>
internal sealed class UploadShardResponseJson
{
    /// <summary>0 when the shard already existed, 1 when this call registered it.</summary>
    [JsonPropertyName("result")]
    public int Result { get; set; }
}

/// <summary>
/// One line of the <c>POST /v2/shards</c> newline-delimited event stream. Every event type shares
/// this shape; which fields are set depends on <see cref="Type"/>.
/// </summary>
internal sealed class ShardUploadEventJson
{
    /// <summary><c>validating</c>, <c>committing</c>, <c>result</c> or <c>error</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Chunks verified so far, on a <c>validating</c> event.</summary>
    [JsonPropertyName("verified")]
    public long Verified { get; set; }

    /// <summary>
    /// Chunks to verify, on a <c>validating</c> event. It grows while the shard is still arriving,
    /// so it is a live ratio rather than a percentage.
    /// </summary>
    [JsonPropertyName("total")]
    public long Total { get; set; }

    /// <summary><c>uploading</c> or <c>syncing</c>, on a <c>committing</c> event.</summary>
    [JsonPropertyName("stage")]
    public string? Stage { get; set; }

    /// <summary>Same values as the v1 response, on the terminal <c>result</c> event.</summary>
    [JsonPropertyName("result")]
    public int Result { get; set; }

    /// <summary>A sanitized failure message, on the terminal <c>error</c> event.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
