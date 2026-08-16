namespace XetSharp.Tests;

/// <summary>
/// The chunking rule written out exactly as the specification states it: one byte at a time, no
/// skipping ahead, no unrolling, no cleverness of any kind. It exists to be slow and obviously
/// right, so the shipping chunker can be checked against it byte for byte on data the published
/// vectors do not cover.
/// </summary>
internal static class NaiveChunker
{
    private const int TargetChunkSize = 64 * 1024;
    private const int MinimumChunkSize = TargetChunkSize / 8;
    private const int MaximumChunkSize = TargetChunkSize * 2;
    private const ulong Mask = 0xFFFF_0000_0000_0000;

    /// <summary>The length of each chunk <paramref name="data"/> splits into.</summary>
    public static List<int> ChunkLengths(ReadOnlySpan<byte> data)
    {
        var lengths = new List<int>();
        var hash = 0ul;
        var length = 0;

        for (var index = 0; index < data.Length; index++)
        {
            hash = unchecked((hash << 1) + GearTable[data[index]]);
            length++;

            var atBoundary = (hash & Mask) == 0 && length >= MinimumChunkSize;
            if (atBoundary || length == MaximumChunkSize)
            {
                lengths.Add(length);
                length = 0;
                hash = 0;
            }
        }

        if (length > 0)
        {
            lengths.Add(length);
        }

        return lengths;
    }

    /// <summary>
    /// The same table the library uses rather than a copy of it: this file is about the algorithm,
    /// and transcribing 256 constants a second time would only invite a typo.
    /// </summary>
    private static ReadOnlySpan<ulong> GearTable => Chunking.GearTable.Table;
}
