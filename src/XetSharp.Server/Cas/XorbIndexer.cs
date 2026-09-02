using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using XetSharp.Hashing;
using XetSharp.Server.Storage;
using XetSharp.Xorbs;

namespace XetSharp.Server.Cas;

/// <summary>
/// Reads an uploaded xorb the hard way — every chunk decompressed and hashed — to prove the hash
/// it was uploaded under and to build the index the server serves reconstructions from. Nothing a
/// client says about a xorb is taken on trust; the bytes are the only source.
/// </summary>
internal static class XorbIndexer
{
    /// <exception cref="XetServerException">The bytes are not a xorb, or not the xorb they claim to be.</exception>
    public static StoredXorb Index(ReadOnlySpan<byte> serialized, MerkleHash expectedHash)
    {
        if (serialized.Length > XorbSerializer.MaxSerializedSize)
        {
            throw XetServerException.BadRequest(
                $"The xorb is {serialized.Length} bytes; the limit is {XorbSerializer.MaxSerializedSize}.");
        }

        var reader = new XorbChunkReader(serialized);
        var chunks = ImmutableArray.CreateBuilder<StoredChunk>();
        var nodes = new List<(MerkleHash Hash, ulong Length)>();
        var buffer = new ArrayBufferWriter<byte>(XorbChunkHeader.MaxUncompressedSize);

        while (true)
        {
            var offset = reader.BytesConsumed;
            buffer.ResetWrittenCount();

            bool more;
            try
            {
                more = reader.TryReadChunk(buffer);
            }
            catch (InvalidDataException exception)
            {
                throw XetServerException.BadRequest($"The xorb is malformed at chunk {chunks.Count} (byte {offset}): {exception.Message}");
            }

            if (!more)
            {
                break;
            }

            var hash = XetHashes.ChunkHash(buffer.WrittenSpan);
            chunks.Add(new StoredChunk(hash, buffer.WrittenCount, offset, reader.BytesConsumed - offset));
            nodes.Add((hash, (ulong)buffer.WrittenCount));
        }

        if (chunks.Count == 0)
        {
            throw XetServerException.BadRequest("The xorb holds no chunks.");
        }

        var actualHash = XetHashes.XorbHash(CollectionsMarshal.AsSpan(nodes));
        if (actualHash != expectedHash)
        {
            throw XetServerException.BadRequest(
                $"The xorb was uploaded as {expectedHash} but its chunks hash to {actualHash}.");
        }

        return new StoredXorb(expectedHash, serialized.Length, chunks.ToImmutable());
    }
}
