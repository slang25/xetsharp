using System.Buffers.Binary;

namespace XetSharp.Benchmarks;

/// <summary>
/// Deterministic inputs, so a benchmark run measured today compares with one measured last month.
/// The generator is the same SplitMix64 the reference test vectors use.
/// </summary>
internal static class BenchmarkData
{
    /// <summary>Pseudo-random, incompressible bytes: the worst case for the compression path.</summary>
    public static byte[] Random(ulong seed, int count)
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

            BinaryPrimitives.WriteUInt64LittleEndian(block, z);
            block[..Math.Min(8, count - i)].CopyTo(data.AsSpan(i));
        }

        return data;
    }

    /// <summary>
    /// Little-endian float32 values drifting slowly, which is what model weights look like: highly
    /// compressible once the bytes are grouped by position, barely compressible before.
    /// </summary>
    public static byte[] Weights(ulong seed, int count)
    {
        var data = new byte[count];
        var noise = Random(seed, count);
        var value = 0.0f;
        for (var i = 0; i + 4 <= count; i += 4)
        {
            value += (noise[i] - 128) / 4096.0f;
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i), value);
        }

        return data;
    }
}
