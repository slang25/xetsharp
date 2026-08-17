using System.Buffers;

namespace XetSharp.Buffers;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> backed by a pooled array, for the short-lived scratch buffers
/// the format layers need (compressing a chunk before its header can be written, for example).
/// Behaves like <see cref="ArrayBufferWriter{T}"/> but returns its storage to the pool on dispose.
/// </summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;

    public PooledBufferWriter(int initialCapacity = 0)
    {
        _buffer = initialCapacity > 0 ? ArrayPool<byte>.Shared.Rent(initialCapacity) : [];
    }

    public int WrittenCount => _written;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Advance(int count) => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Reset() => _written = 0;

    /// <summary>
    /// Hands the pooled array over to the caller, who becomes responsible for returning it, and
    /// leaves this writer empty — so a <c>using</c> around it turns into a no-op. Lets a compressed
    /// chunk outlive the writer it was compressed into without copying it out.
    /// </summary>
    public byte[] Detach()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;
        return buffer;
    }

    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = [];
        _written = 0;
    }

    private void EnsureCapacity(int sizeHint)
    {
        var needed = _written + Math.Max(sizeHint, 1);
        if (needed <= _buffer.Length)
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _buffer.Length * 2));
        _buffer.AsSpan(0, _written).CopyTo(replacement);
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = replacement;
    }
}
