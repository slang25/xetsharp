using System.Buffers;
using K4os.Compression.LZ4.Streams;
using XetSharp.Buffers;

namespace XetSharp.Xorbs;

/// <summary>
/// Applies and reverses the three chunk compression schemes. The LZ4 variants use the LZ4
/// <em>frame</em> format, which is what the reference implementation writes.
/// </summary>
internal static class ChunkCompression
{
    /// <summary>
    /// Frame settings chosen to match the reference implementation's output shape: independent
    /// blocks, no checksums, and a block size large enough that any legal chunk is a single block.
    /// </summary>
    /// <remarks>
    /// Shared by every thread packing a xorb. <c>LZ4Frame.Encode</c> only reads its settings — it
    /// builds a fresh encoder and descriptor per call, and shares a static settings instance itself
    /// when none is passed — so one instance is safe as long as nothing here mutates it.
    /// </remarks>
    private static readonly LZ4EncoderSettings EncoderSettings = new()
    {
        ChainBlocks = false,
        BlockSize = 256 * 1024,
        ContentChecksum = false,
        BlockChecksum = false,
    };

    /// <summary>Compresses <paramref name="source"/> and returns the number of bytes written.</summary>
    public static int Compress(ReadOnlySpan<byte> source, ChunkCompressionScheme scheme, IBufferWriter<byte> destination)
    {
        switch (scheme)
        {
            case ChunkCompressionScheme.None:
                destination.Write(source);
                return source.Length;

            case ChunkCompressionScheme.Lz4:
                return Encode(source, destination);

            case ChunkCompressionScheme.ByteGrouping4Lz4:
                var grouped = ArrayPool<byte>.Shared.Rent(source.Length);
                try
                {
                    ByteGrouping4.Group(source, grouped.AsSpan(0, source.Length));
                    return Encode(grouped.AsSpan(0, source.Length), destination);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(grouped);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(scheme), scheme, "Unknown chunk compression scheme.");
        }
    }

    /// <summary>
    /// Decompresses <paramref name="source"/> into <paramref name="destination"/>, verifying that
    /// it expands to exactly <paramref name="uncompressedSize"/> bytes.
    /// </summary>
    public static void Decompress(
        ReadOnlySpan<byte> source,
        ChunkCompressionScheme scheme,
        int uncompressedSize,
        IBufferWriter<byte> destination)
    {
        switch (scheme)
        {
            case ChunkCompressionScheme.None:
                if (source.Length != uncompressedSize)
                {
                    throw new InvalidDataException(
                        $"Uncompressed chunk holds {source.Length} bytes but its header claims {uncompressedSize}.");
                }

                destination.Write(source);
                return;

            case ChunkCompressionScheme.Lz4:
                Decode(source, uncompressedSize, destination);
                return;

            case ChunkCompressionScheme.ByteGrouping4Lz4:
                // The grouping has to be undone over the whole chunk at once, so it cannot be
                // streamed straight into the destination.
                using (var grouped = new PooledBufferWriter(uncompressedSize))
                {
                    Decode(source, uncompressedSize, grouped);
                    ByteGrouping4.Ungroup(grouped.WrittenSpan, destination.GetSpan(uncompressedSize)[..uncompressedSize]);
                    destination.Advance(uncompressedSize);
                }

                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(scheme), scheme, "Unknown chunk compression scheme.");
        }
    }

    private static int Encode(ReadOnlySpan<byte> source, IBufferWriter<byte> destination)
    {
        var counting = new CountingWriter(destination, int.MaxValue);
        LZ4Frame.Encode(source, counting, EncoderSettings);
        return counting.BytesWritten;
    }

    private static void Decode(ReadOnlySpan<byte> source, int uncompressedSize, IBufferWriter<byte> destination)
    {
        var counting = new CountingWriter(destination, uncompressedSize);
        LZ4Frame.Decode(source, counting);
        if (counting.BytesWritten != uncompressedSize)
        {
            throw new InvalidDataException(
                $"Chunk decompressed to {counting.BytesWritten} bytes but its header claims {uncompressedSize}.");
        }
    }

    /// <summary>
    /// Forwards to an inner writer while tracking how many bytes passed through, refusing to let
    /// more than <paramref name="limit"/> of them through.
    /// </summary>
    /// <remarks>
    /// The limit is what stops a hostile xorb from being a decompression bomb: chunk data arrives
    /// from a CDN, and the frame's own header says nothing trustworthy about how far it expands, so
    /// waiting until the frame finishes to compare against the declared size is too late. The check
    /// lands on <see cref="Advance"/> rather than <see cref="GetSpan"/> because a decoder legitimately
    /// asks for a whole block's worth of room and then fills only part of it.
    /// </remarks>
    private sealed class CountingWriter(IBufferWriter<byte> inner, int limit) : IBufferWriter<byte>
    {
        public int BytesWritten { get; private set; }

        public void Advance(int count)
        {
            if (count > limit - BytesWritten)
            {
                throw new InvalidDataException(
                    $"Chunk decompressed past the {limit} bytes its header claims; refusing to continue.");
            }

            BytesWritten += count;
            inner.Advance(count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
    }
}
