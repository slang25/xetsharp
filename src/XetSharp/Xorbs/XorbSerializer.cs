using System.Buffers;
using XetSharp.Buffers;

namespace XetSharp.Xorbs;

/// <summary>
/// Reads and writes the xorb serialization format: chunk records — an 8-byte header followed by
/// that chunk's compressed bytes — laid end to end with no container header, footer or index.
/// </summary>
public static class XorbSerializer
{
    /// <summary>
    /// The largest serialized xorb the CAS server accepts. Since chunks compress, packing should be
    /// driven by the total <em>uncompressed</em> length approaching this figure.
    /// </summary>
    public const int MaxSerializedSize = 64 * 1024 * 1024;

    /// <summary>
    /// Serializes one chunk, choosing the compression scheme that yields the smallest record and
    /// falling back to storing it uncompressed when neither scheme helps. Returns the number of
    /// bytes written.
    /// </summary>
    public static int SerializeChunk(ReadOnlySpan<byte> chunk, IBufferWriter<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunk.Length, XorbChunkHeader.MaxUncompressedSize, nameof(chunk));

        using var lz4 = new PooledBufferWriter(chunk.Length);
        ChunkCompression.Compress(chunk, ChunkCompressionScheme.Lz4, lz4);

        using var byteGrouped = new PooledBufferWriter(chunk.Length);
        ChunkCompression.Compress(chunk, ChunkCompressionScheme.ByteGrouping4Lz4, byteGrouped);

        var smallest = Math.Min(lz4.WrittenCount, byteGrouped.WrittenCount);
        if (smallest >= chunk.Length)
        {
            return WriteRecord(
                new XorbChunkHeader(chunk.Length, ChunkCompressionScheme.None, chunk.Length),
                chunk,
                destination);
        }

        var scheme = lz4.WrittenCount <= byteGrouped.WrittenCount
            ? ChunkCompressionScheme.Lz4
            : ChunkCompressionScheme.ByteGrouping4Lz4;
        var data = scheme == ChunkCompressionScheme.Lz4 ? lz4.WrittenSpan : byteGrouped.WrittenSpan;

        return WriteRecord(new XorbChunkHeader(data.Length, scheme, chunk.Length), data, destination);
    }

    /// <summary>Serializes one chunk with a caller-chosen compression scheme.</summary>
    public static int SerializeChunk(ReadOnlySpan<byte> chunk, ChunkCompressionScheme scheme, IBufferWriter<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunk.Length, XorbChunkHeader.MaxUncompressedSize, nameof(chunk));

        if (scheme == ChunkCompressionScheme.None)
        {
            return WriteRecord(new XorbChunkHeader(chunk.Length, scheme, chunk.Length), chunk, destination);
        }

        using var compressed = new PooledBufferWriter(chunk.Length);
        ChunkCompression.Compress(chunk, scheme, compressed);
        return WriteRecord(new XorbChunkHeader(compressed.WrittenCount, scheme, chunk.Length), compressed.WrittenSpan, destination);
    }

    /// <summary>
    /// Serializes a whole xorb: every chunk in order, each compressed with the scheme that suits it
    /// best. Returns the serialized length, which the caller records as the shard's
    /// <c>num_bytes_on_disk</c>. Throws once the result would exceed
    /// <see cref="MaxSerializedSize"/>, rather than emitting a xorb the CAS server will reject.
    /// </summary>
    public static int Serialize(IEnumerable<ReadOnlyMemory<byte>> chunks, IBufferWriter<byte> destination)
    {
        var written = 0;
        foreach (var chunk in chunks)
        {
            written += SerializeChunk(chunk.Span, destination);

            // Only the compressed size counts against the limit, so this can only be judged after
            // each chunk is compressed — an uncompressed-length estimate would reject xorbs that fit.
            if (written > MaxSerializedSize)
            {
                throw new ArgumentException(
                    $"These chunks serialize to at least {written} bytes, over the {MaxSerializedSize}-byte xorb limit; pack fewer per xorb.",
                    nameof(chunks));
            }
        }

        return written;
    }

    /// <summary>
    /// Decompresses every complete chunk record in <paramref name="serialized"/>. Convenience for
    /// tests and small xorbs; the download path uses <see cref="XorbChunkReader"/> so it can write
    /// chunks straight into the output.
    /// </summary>
    public static List<byte[]> Deserialize(ReadOnlySpan<byte> serialized)
    {
        var chunks = new List<byte[]>();
        var reader = new XorbChunkReader(serialized);
        using var chunk = new PooledBufferWriter(XorbChunkHeader.MaxUncompressedSize);
        while (true)
        {
            chunk.Reset();
            if (!reader.TryReadChunk(chunk))
            {
                break;
            }

            chunks.Add(chunk.WrittenSpan.ToArray());
        }

        return chunks;
    }

    private static int WriteRecord(XorbChunkHeader header, ReadOnlySpan<byte> data, IBufferWriter<byte> destination)
    {
        var record = destination.GetSpan(header.RecordSize);
        header.WriteTo(record);
        data.CopyTo(record[XorbChunkHeader.Size..]);
        destination.Advance(header.RecordSize);
        return header.RecordSize;
    }
}
