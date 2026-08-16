using System.Buffers;

namespace XetSharp.Xorbs;

/// <summary>
/// One chunk compressed and ready to be written as a xorb record: the header it will carry, and the
/// payload that follows it — unless neither scheme beat storing the chunk as it is, in which case
/// the payload is the caller's own chunk and <see cref="Payload"/> is empty.
/// </summary>
/// <remarks>
/// Exists so that choosing a scheme can happen away from the buffer the record ends up in: the
/// packer compresses chunks on the thread pool and appends them, in order, later. Holds a pooled
/// array, so dispose it once the record is written.
/// </remarks>
internal readonly struct CompressedChunk : IDisposable
{
    private readonly byte[]? _pooled;
    private readonly int _length;

    /// <summary>A chunk that compression did not help: it is stored as it is.</summary>
    public CompressedChunk(XorbChunkHeader header)
    {
        Header = header;
        _pooled = null;
        _length = 0;
    }

    public CompressedChunk(XorbChunkHeader header, byte[] pooled, int length)
    {
        Header = header;
        _pooled = pooled;
        _length = length;
    }

    public XorbChunkHeader Header { get; }

    /// <summary>Whether the record's payload is the original chunk rather than <see cref="Payload"/>.</summary>
    public bool IsStoredUncompressed => _pooled is null;

    public ReadOnlySpan<byte> Payload => _pooled is null ? default : _pooled.AsSpan(0, _length);

    public void Dispose()
    {
        if (_pooled is { Length: > 0 })
        {
            ArrayPool<byte>.Shared.Return(_pooled);
        }
    }
}
