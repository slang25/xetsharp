using System.Buffers;

namespace XetSharp.Xorbs;

/// <summary>
/// A forward reader over serialized xorb bytes, yielding decompressed chunks in order. The bytes
/// need not be a whole xorb: a chunk-aligned byte range works just as well, which is exactly what
/// the download path fetches.
/// </summary>
public ref struct XorbChunkReader(ReadOnlySpan<byte> serialized)
{
    private readonly ReadOnlySpan<byte> _serialized = serialized;
    private int _position;

    /// <summary>Bytes consumed by the chunk records read so far.</summary>
    public readonly int BytesConsumed => _position;

    /// <summary>
    /// The bytes after the last complete chunk record. Empty for a well-formed range — but note
    /// that the reference sample xorb carries four trailing bytes the spec does not describe, so
    /// a non-empty remainder shorter than a header is not necessarily corruption.
    /// </summary>
    public readonly ReadOnlySpan<byte> Remaining => _serialized[_position..];

    /// <summary>
    /// Decompresses the next chunk into <paramref name="destination"/>, returning false once fewer
    /// than <see cref="XorbChunkHeader.Size"/> bytes remain. Throws when a header is present but
    /// its data is truncated, or when the data fails to decompress to the declared length.
    /// </summary>
    public bool TryReadChunk(IBufferWriter<byte> destination)
    {
        if (!XorbChunkHeader.TryRead(Remaining, out var header))
        {
            return false;
        }

        var data = Remaining[XorbChunkHeader.Size..];
        if (data.Length < header.CompressedSize)
        {
            throw new InvalidDataException(
                $"Chunk data is truncated: the header declares {header.CompressedSize} bytes but only {data.Length} remain.");
        }

        ChunkCompression.Decompress(
            data[..header.CompressedSize],
            header.CompressionScheme,
            header.UncompressedSize,
            destination);

        _position += header.RecordSize;
        return true;
    }

    /// <summary>
    /// Reads the next chunk's header and its still-compressed data without decompressing, for
    /// callers that only need to walk or copy records.
    /// </summary>
    public bool TryReadRawChunk(out XorbChunkHeader header, out ReadOnlySpan<byte> compressedData)
    {
        compressedData = default;
        if (!XorbChunkHeader.TryRead(Remaining, out header))
        {
            return false;
        }

        var data = Remaining[XorbChunkHeader.Size..];
        if (data.Length < header.CompressedSize)
        {
            throw new InvalidDataException(
                $"Chunk data is truncated: the header declares {header.CompressedSize} bytes but only {data.Length} remain.");
        }

        compressedData = data[..header.CompressedSize];
        _position += header.RecordSize;
        return true;
    }
}
