namespace XetSharp.Xorbs;

/// <summary>
/// How a single chunk's bytes are compressed inside a serialized xorb. Chosen per chunk by the
/// uploader, so one xorb may mix schemes.
/// </summary>
public enum ChunkCompressionScheme : byte
{
    /// <summary>Stored as-is; compressed and uncompressed sizes are equal.</summary>
    None = 0,

    /// <summary>
    /// LZ4, in the <em>frame</em> format (magic <c>04 22 4d 18</c>) rather than a bare LZ4 block —
    /// the spec says only "standard LZ4 compression", but the reference xorb settles it.
    /// </summary>
    Lz4 = 1,

    /// <summary>
    /// Bytes regrouped by their position within each 4-byte group, then LZ4-framed. Helps
    /// floating-point and other column-like data where a byte position has a narrow value range.
    /// </summary>
    ByteGrouping4Lz4 = 2,
}
