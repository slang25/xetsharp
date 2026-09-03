using XetSharp.Hashing;
using XetSharp.Server.Storage;
using XetSharp.Shards;

namespace XetSharp.Server.Cas;

/// <summary>
/// Builds the shard a global-deduplication query is answered with: no files, one CAS-info block
/// for the xorb holding the queried chunk, chunk hashes HMAC'd with the server's key so that a
/// client learns only about chunks it already holds, and a footer carrying that key.
/// </summary>
internal static class DeduplicationShardBuilder
{
    public static MdbShard Build(StoredXorb xorb, MerkleHash hmacKey, DateTimeOffset now, TimeSpan lifetime)
    {
        var chunks = new ShardCasChunk[xorb.Chunks.Length];
        var offset = 0L;
        for (var i = 0; i < chunks.Length; i++)
        {
            var chunk = xorb.Chunks[i];
            chunks[i] = new ShardCasChunk(XetHashes.Hmac(chunk.Hash, hmacKey), (uint)offset, (uint)chunk.UnpackedLength);
            offset += chunk.UnpackedLength;
        }

        return new MdbShard
        {
            Files = [],
            Xorbs = [new ShardCasInfo(xorb.Hash, (uint)offset, (uint)xorb.SerializedLength, chunks)],
            Footer = new ShardFooter
            {
                ChunkHashHmacKey = hmacKey,
                CreatedAt = now,
                ExpiresAt = now + lifetime,
                StoredBytes = (ulong)offset,
                StoredBytesOnDisk = (ulong)xorb.SerializedLength,
            },
        };
    }
}
