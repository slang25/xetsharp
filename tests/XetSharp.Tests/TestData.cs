namespace XetSharp.Tests;

/// <summary>
/// Deterministic test-data generators matching those used by the xet-core reference test suite,
/// so its expected values can be asserted here verbatim.
/// </summary>
public static class TestData
{
    /// <summary>
    /// Pseudo-random bytes from a SplitMix64 generator (https://prng.di.unimi.it/splitmix64.c),
    /// emitting each 64-bit output as little-endian bytes. Portable across languages by design.
    /// </summary>
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
    /// The 128-byte pattern known to trigger chunk boundaries, padded with <paramref name="padding"/>
    /// zero bytes and repeated (by doubling, as in the reference) to fill <paramref name="count"/> bytes.
    /// </summary>
    public static byte[] TriggeringPatternData(int count, int padding)
    {
        ReadOnlySpan<byte> pattern =
        [
            154, 52, 42, 34, 159, 75, 126, 224, 70, 236, 12, 196, 79, 236, 178, 124, 127, 50, 99, 178, 44, 176, 174,
            126, 250, 235, 205, 174, 252, 122, 35, 10, 20, 101, 214, 69, 193, 8, 115, 105, 158, 228, 120, 111, 136,
            162, 198, 251, 211, 183, 253, 252, 164, 147, 63, 16, 186, 162, 117, 23, 170, 36, 205, 187, 174, 76, 210,
            174, 211, 175, 12, 173, 145, 59, 2, 70, 222, 181, 159, 227, 182, 156, 189, 51, 226, 106, 24, 50, 183, 157,
            140, 10, 8, 23, 212, 70, 10, 234, 23, 33, 219, 254, 39, 236, 70, 49, 191, 116, 9, 115, 15, 101, 26, 159,
            165, 220, 15, 170, 56, 125, 92, 163, 94, 235, 38, 40, 49, 81,
        ];

        var data = new List<byte>(count + pattern.Length + padding);
        data.AddRange(pattern);
        data.AddRange(Enumerable.Repeat((byte)0, padding));

        while (data.Count < count)
        {
            var take = Math.Min(count - data.Count, data.Count);
            data.AddRange(data.Take(take).ToArray());
        }

        return data.ToArray();
    }

    public static int[] Boundaries(IEnumerable<byte[]> chunks)
    {
        var boundaries = new List<int>();
        var offset = 0;
        foreach (var chunk in chunks)
        {
            offset += chunk.Length;
            boundaries.Add(offset);
        }

        return boundaries.ToArray();
    }
}
