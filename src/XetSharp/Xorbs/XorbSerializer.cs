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
        using var compressed = CompressChunk(chunk);
        return WriteRecord(compressed, chunk, destination);
    }

    /// <summary>
    /// Compresses one chunk both ways and keeps whichever record came out smallest, without writing
    /// anything: the two halves of <see cref="SerializeChunk(ReadOnlySpan{byte}, IBufferWriter{byte})"/>
    /// split apart so the packer can do this part on the thread pool and append the result in order
    /// afterwards. Sharing the decision with the serial path is what keeps a parallel-packed xorb
    /// byte-identical to a serially packed one.
    /// </summary>
    internal static CompressedChunk CompressChunk(ReadOnlySpan<byte> chunk)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunk.Length, XorbChunkHeader.MaxUncompressedSize, nameof(chunk));

        using var lz4 = new PooledBufferWriter(chunk.Length);
        ChunkCompression.Compress(chunk, ChunkCompressionScheme.Lz4, lz4);

        using var byteGrouped = new PooledBufferWriter(chunk.Length);
        ChunkCompression.Compress(chunk, ChunkCompressionScheme.ByteGrouping4Lz4, byteGrouped);

        var smallest = Math.Min(lz4.WrittenCount, byteGrouped.WrittenCount);
        if (smallest >= chunk.Length)
        {
            return new CompressedChunk(new XorbChunkHeader(chunk.Length, ChunkCompressionScheme.None, chunk.Length));
        }

        var scheme = lz4.WrittenCount <= byteGrouped.WrittenCount
            ? ChunkCompressionScheme.Lz4
            : ChunkCompressionScheme.ByteGrouping4Lz4;
        var winner = scheme == ChunkCompressionScheme.Lz4 ? lz4 : byteGrouped;

        // Taken over rather than copied out: the writer's pooled array becomes the record's, and the
        // `using` above turns into a no-op for it.
        var length = winner.WrittenCount;
        return new CompressedChunk(new XorbChunkHeader(length, scheme, chunk.Length), winner.Detach(), length);
    }

    /// <summary>
    /// Writes a compressed chunk's record — header then payload — falling back to
    /// <paramref name="chunk"/> itself for the chunks that are stored uncompressed. Returns the
    /// number of bytes written.
    /// </summary>
    internal static int WriteRecord(in CompressedChunk compressed, ReadOnlySpan<byte> chunk, IBufferWriter<byte> destination) =>
        WriteRecord(compressed.Header, compressed.IsStoredUncompressed ? chunk : compressed.Payload, destination);

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
