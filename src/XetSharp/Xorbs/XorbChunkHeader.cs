using System.Buffers.Binary;

namespace XetSharp.Xorbs;

/// <summary>
/// The 8-byte header preceding every chunk's data in a serialized xorb:
/// <c>version(1) | compressed_size(3, LE) | compression_scheme(1) | uncompressed_size(3, LE)</c>.
/// </summary>
public readonly record struct XorbChunkHeader(
    byte Version,
    int CompressedSize,
    ChunkCompressionScheme CompressionScheme,
    int UncompressedSize)
{
    public const int Size = 8;

    /// <summary>The protocol version written into every chunk header.</summary>
    public const byte CurrentVersion = 0;

    /// <summary>
    /// The largest raw chunk the protocol permits, and so the largest an
    /// <see cref="UncompressedSize"/> may legally be.
    /// </summary>
    public const int MaxUncompressedSize = 128 * 1024;

    /// <summary>The largest value the 3-byte size fields can hold.</summary>
    private const int MaxSizeField = (1 << 24) - 1;

    public XorbChunkHeader(int compressedSize, ChunkCompressionScheme scheme, int uncompressedSize)
        : this(CurrentVersion, compressedSize, scheme, uncompressedSize)
    {
    }

    /// <summary>The total serialized length of this chunk record: header plus compressed data.</summary>
    public int RecordSize => Size + CompressedSize;

    public void WriteTo(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(CompressedSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CompressedSize, MaxSizeField);
        ArgumentOutOfRangeException.ThrowIfNegative(UncompressedSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(UncompressedSize, MaxUncompressedSize);

        destination[0] = Version;
        WriteUInt24LittleEndian(destination[1..], CompressedSize);
        destination[4] = (byte)CompressionScheme;
        WriteUInt24LittleEndian(destination[5..], UncompressedSize);
    }

    /// <summary>
    /// Reads a header from the start of <paramref name="source"/>, returning false when there are
    /// fewer than <see cref="Size"/> bytes available. Throws when the header itself is malformed.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out XorbChunkHeader header)
    {
        if (source.Length < Size)
        {
            header = default;
            return false;
        }

        var version = source[0];
        if (version != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported xorb chunk header version {version}; expected {CurrentVersion}.");
        }

        var scheme = (ChunkCompressionScheme)source[4];
        if (!Enum.IsDefined(scheme))
        {
            throw new InvalidDataException($"Unknown xorb chunk compression scheme {source[4]}.");
        }

        var uncompressedSize = ReadUInt24LittleEndian(source[5..]);
        if (uncompressedSize > MaxUncompressedSize)
        {
            throw new InvalidDataException(
                $"Chunk claims an uncompressed size of {uncompressedSize} bytes, above the {MaxUncompressedSize}-byte protocol maximum.");
        }

        header = new XorbChunkHeader(version, ReadUInt24LittleEndian(source[1..]), scheme, uncompressedSize);
        return true;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> source) =>
        source[0] | (source[1] << 8) | (source[2] << 16);

    private static void WriteUInt24LittleEndian(Span<byte> destination, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        buffer[..3].CopyTo(destination);
    }
}
