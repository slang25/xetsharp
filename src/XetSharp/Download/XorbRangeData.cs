using System.Buffers;
using XetSharp.Cas;
using XetSharp.Xorbs;

namespace XetSharp.Download;

/// <summary>
/// A downloaded slice of a serialized xorb, indexed by chunk. The bytes are kept as they arrived —
/// compressed — and decompressed one chunk at a time as the file is written out, so a term that
/// spans a whole 64 MiB xorb costs 64 MiB of memory rather than what it expands to.
/// </summary>
internal sealed class XorbRangeData
{
    private readonly ReadOnlyMemory<byte> _serialized;

    /// <summary>Byte offset of each chunk record within <see cref="_serialized"/>, first to last.</summary>
    private readonly int[] _offsets;

    public XorbRangeData(XorbRangeDescriptor descriptor, ReadOnlyMemory<byte> serialized)
    {
        Descriptor = descriptor;
        _serialized = serialized;
        _offsets = IndexChunkRecords(serialized.Span, descriptor);
    }

    public XorbRangeDescriptor Descriptor { get; }

    /// <summary>Bytes held in memory for this range.</summary>
    public long ByteCount => _serialized.Length;

    /// <summary>
    /// Decompresses one chunk into <paramref name="destination"/> and returns its length. Chunks
    /// are addressed by their index in the xorb, not their position in this range.
    /// </summary>
    public int DecompressChunk(int chunkIndex, IBufferWriter<byte> destination)
    {
        if (!Descriptor.Chunks.Contains(chunkIndex))
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkIndex),
                $"Chunk {chunkIndex} is outside this range's chunks {Descriptor.Chunks}.");
        }

        var offset = _offsets[chunkIndex - Descriptor.Chunks.Start];
        var record = _serialized.Span[offset..];
        if (!XorbChunkHeader.TryRead(record, out var header) || !new XorbChunkReader(record).TryReadChunk(destination))
        {
            throw new InvalidDataException($"Chunk {chunkIndex} is missing from the downloaded xorb data.");
        }

        // Decompression writes exactly what the header declares or throws trying.
        return header.UncompressedSize;
    }

    /// <summary>
    /// Walks the chunk records once, both to build the offset index and to check that the server
    /// sent exactly the chunks it said it would.
    /// </summary>
    private static int[] IndexChunkRecords(ReadOnlySpan<byte> serialized, XorbRangeDescriptor descriptor)
    {
        var expected = descriptor.Chunks.Count;
        if (serialized.Length != descriptor.Bytes.Length)
        {
            throw new InvalidDataException(
                $"Expected {descriptor.Bytes.Length} bytes for chunks {descriptor.Chunks} but received {serialized.Length}.");
        }

        var offsets = new int[expected];
        var offset = 0;
        for (var index = 0; index < expected; index++)
        {
            if (!XorbChunkHeader.TryRead(serialized[offset..], out var header))
            {
                throw new InvalidDataException(
                    $"The downloaded xorb data holds {index} chunk records, but chunks {descriptor.Chunks} were requested.");
            }

            offsets[index] = offset;
            offset += header.RecordSize;
            if (offset > serialized.Length)
            {
                throw new InvalidDataException($"Chunk record {descriptor.Chunks.Start + index} runs past the end of the downloaded data.");
            }
        }

        // A whole-xorb range can carry a few trailing bytes that are not a chunk record; anything
        // longer means the range covered more chunks than the reconstruction accounted for.
        if (serialized.Length - offset >= XorbChunkHeader.Size)
        {
            throw new InvalidDataException(
                $"The downloaded xorb data holds more chunk records than the {expected} chunks {descriptor.Chunks} requested.");
        }

        return offsets;
    }
}
