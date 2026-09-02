using XetSharp.Chunking;
using XetSharp.Hashing;
using XetSharp.Server.Storage;

namespace XetSharp.Server.Tests;

/// <summary>Deterministic test data, the same generator the client's suite and xet-core's use.</summary>
internal static class TestData
{
    public static byte[] SplitMix64Bytes(ulong seed, int count)
    {
        var data = new byte[count];
        var state = seed;
        Span<byte> block = stackalloc byte[8];
        for (var i = 0; i < count; i += 8)
        {
            state += 0x9E3779B97F4A7C15;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            z ^= z >> 31;

            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(block, z);
            block[..Math.Min(8, count - i)].CopyTo(data.AsSpan(i));
        }

        return data;
    }

    /// <summary>
    /// Data at least one of whose chunks is in the global-deduplication sample — the client only
    /// asks the index about those, so a test of the index needs one. Found by trying seeds, which
    /// is quick: a chunk is eligible one time in 1024.
    /// </summary>
    public static byte[] WithEligibleChunk(ulong firstSeed, int count)
    {
        for (var seed = firstSeed; ; seed++)
        {
            var data = SplitMix64Bytes(seed, count);
            if (Chunker.ChunkAll(data).Any(chunk => GlobalDeduplication.IsEligible(XetHashes.ChunkHash(chunk))))
            {
                return data;
            }
        }
    }
}
