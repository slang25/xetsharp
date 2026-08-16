namespace XetSharp.Chunking;

/// <summary>
/// Splits a byte stream into content-defined chunks using the gearhash rolling hash, exactly as
/// specified by the Xet protocol: target chunk size 64 KiB, minimum 8 KiB, maximum 128 KiB, with a
/// boundary wherever the top 16 bits of the rolling hash are all zero. Deterministic: identical
/// input produces identical boundaries regardless of how the data is fed in.
/// </summary>
public sealed class Chunker
{
    /// <summary>The gearhash window: only the last 64 bytes influence the hash value.</summary>
    private const int HashWindowSize = 64;

    public const int TargetChunkSize = 64 * 1024;
    public const int MinimumChunkSize = TargetChunkSize / 8;
    public const int MaximumChunkSize = TargetChunkSize * 2;

    private readonly int _minimumChunk;
    private readonly int _maximumChunk;
    private readonly ulong _mask;

    private ulong _hash;
    private readonly MemoryStream _chunkBuffer = new();

    public Chunker() : this(TargetChunkSize)
    {
    }

    /// <summary>
    /// Creates a chunker with a custom target chunk size (must be a power of two, &gt; 64).
    /// The protocol requires the default; other sizes exist for testing against the reference
    /// implementation, which is parameterized the same way.
    /// </summary>
    public Chunker(int targetChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetChunkSize, HashWindowSize);
        if (!int.IsPow2(targetChunkSize))
        {
            throw new ArgumentException("Target chunk size must be a power of two.", nameof(targetChunkSize));
        }

        var mask = (ulong)(targetChunkSize - 1);
        _mask = mask << System.Numerics.BitOperations.LeadingZeroCount(mask);
        _minimumChunk = targetChunkSize / 8;
        _maximumChunk = targetChunkSize * 2;
    }

    /// <summary>
    /// Consumes <paramref name="data"/>, appending any completed chunks to <paramref name="chunks"/>.
    /// Pass <paramref name="isFinal"/> = true with the last block (or an empty span after it) to flush
    /// the remaining bytes as the final chunk.
    /// </summary>
    public void NextBlock(ReadOnlySpan<byte> data, bool isFinal, List<byte[]> chunks)
    {
        while (true)
        {
            var boundary = NextBoundary(data);
            if (boundary is int cut)
            {
                if (_chunkBuffer.Length == 0)
                {
                    chunks.Add(data[..cut].ToArray());
                }
                else
                {
                    _chunkBuffer.Write(data[..cut]);
                    chunks.Add(_chunkBuffer.ToArray());
                    _chunkBuffer.SetLength(0);
                }

                data = data[cut..];
                continue;
            }

            _chunkBuffer.Write(data);
            break;
        }

        if (isFinal)
        {
            if (_chunkBuffer.Length > 0)
            {
                chunks.Add(_chunkBuffer.ToArray());
                _chunkBuffer.SetLength(0);
            }

            _hash = 0;
        }
    }

    /// <summary>Convenience over <see cref="NextBlock"/> for chunking a complete buffer in one call.</summary>
    public static List<byte[]> ChunkAll(ReadOnlySpan<byte> data)
    {
        var chunks = new List<byte[]>();
        new Chunker().NextBlock(data, isFinal: true, chunks);
        return chunks;
    }

    /// <summary>
    /// Finds the next chunk boundary in <paramref name="data"/> (a continuation of any buffered
    /// bytes), or null if more data is needed. Advances the rolling hash state.
    /// </summary>
    private int? NextBoundary(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return null;
        }

        var previousLength = (int)_chunkBuffer.Length;
        var index = 0;

        // The hash depends only on the last 64 bytes, so the prefix of each chunk below the
        // minimum size need not be hashed at all — as long as the hash is fully warmed up
        // (>= 64 bytes processed) before the first admissible boundary test.
        if (previousLength + HashWindowSize < _minimumChunk)
        {
            index = Math.Min(_minimumChunk - previousLength - HashWindowSize - 1, data.Length);
        }

        // No point scanning past the point where the maximum chunk size forces a boundary.
        var readEnd = Math.Min(data.Length, index + _maximumChunk - previousLength);

        var createChunk = false;
        var hash = _hash;
        while (index < readEnd)
        {
            hash = unchecked((hash << 1) + GearTable.Table[data[index]]);
            index++;
            if ((hash & _mask) == 0)
            {
                // A match before the minimum chunk size can only arise from stale hasher state
                // (fewer than 64 fresh bytes processed); the protocol requires ignoring it.
                if (index + previousLength < _minimumChunk)
                {
                    continue;
                }

                createChunk = true;
                break;
            }
        }

        _hash = hash;

        if (index + previousLength >= _maximumChunk)
        {
            index = _maximumChunk - previousLength;
            createChunk = true;
        }

        if (!createChunk)
        {
            return null;
        }

        _hash = 0;
        return index;
    }
}
