namespace XetSharp.Xorbs;

/// <summary>
/// The byte-grouping transform behind <see cref="ChunkCompressionScheme.ByteGrouping4Lz4"/>:
/// the input is split into four groups by each byte's position modulo 4, and the groups are
/// concatenated in order. When the length is not a multiple of 4, the trailing 1–3 bytes continue
/// the pattern, so they land one each in the first 1–3 groups (making those groups one byte longer).
/// </summary>
internal static class ByteGrouping4
{
    private const int GroupCount = 4;

    /// <summary>Regroups <paramref name="source"/> into <paramref name="destination"/> (same length).</summary>
    public static void Group(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        Span<int> positions = stackalloc int[GroupCount];
        GroupStarts(source.Length, positions);

        for (var i = 0; i < source.Length; i++)
        {
            destination[positions[i % GroupCount]++] = source[i];
        }
    }

    /// <summary>Inverts <see cref="Group"/>.</summary>
    public static void Ungroup(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        Span<int> positions = stackalloc int[GroupCount];
        GroupStarts(source.Length, positions);

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = source[positions[i % GroupCount]++];
        }
    }

    /// <summary>Writes the offset at which each group begins in the grouped layout.</summary>
    private static void GroupStarts(int length, Span<int> starts)
    {
        var (quotient, remainder) = Math.DivRem(length, GroupCount);
        var offset = 0;
        for (var group = 0; group < GroupCount; group++)
        {
            starts[group] = offset;
            offset += quotient + (group < remainder ? 1 : 0);
        }
    }
}
