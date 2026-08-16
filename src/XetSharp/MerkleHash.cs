using System.Buffers.Binary;

namespace XetSharp;

/// <summary>
/// A 256-bit hash value used throughout the Xet protocol to identify chunks, xorbs and files.
/// Stored as four 64-bit words, each read little-endian from the raw 32 digest bytes — mirroring
/// the reference implementation's <c>[u64; 4]</c> layout. The canonical string form is 64 lowercase
/// hex characters produced by formatting each word, which byte-reverses each 8-byte group relative
/// to plain hex encoding of the digest. This form is required everywhere a hash appears as a string
/// (API paths, JSON keys and values, internal-node hash inputs).
/// </summary>
public readonly struct MerkleHash : IEquatable<MerkleHash>, ISpanFormattable
{
    public const int Size = 32;

    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;
    private readonly ulong _d;

    public MerkleHash(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A {nameof(MerkleHash)} requires exactly {Size} bytes, got {bytes.Length}.", nameof(bytes));
        }

        _a = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        _b = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        _c = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        _d = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    }

    public static MerkleHash Zero => default;

    public void CopyTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, _a);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], _b);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], _c);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], _d);
    }

    public byte[] ToByteArray()
    {
        var bytes = new byte[Size];
        CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// The value of this hash modulo <paramref name="divisor"/>, defined by the protocol as the
    /// last 8 bytes of the digest interpreted as a little-endian 64-bit integer, mod the divisor.
    /// Used for Merkle tree group cuts and global-deduplication eligibility.
    /// </summary>
    public ulong Mod(ulong divisor) => _d % divisor;

    public static MerkleHash Parse(ReadOnlySpan<char> hex)
    {
        return TryParse(hex, out var hash)
            ? hash
            : throw new FormatException($"A {nameof(MerkleHash)} requires exactly {Size * 2} valid hex characters.");
    }

    public static bool TryParse(ReadOnlySpan<char> hex, out MerkleHash hash)
    {
        hash = default;
        if (hex.Length != Size * 2)
        {
            return false;
        }

        Span<ulong> words = stackalloc ulong[4];
        for (var i = 0; i < 4; i++)
        {
            if (!ulong.TryParse(hex.Slice(i * 16, 16), System.Globalization.NumberStyles.HexNumber, null, out words[i]))
            {
                return false;
            }
        }

        Span<byte> bytes = stackalloc byte[Size];
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[(i * 8)..], words[i]);
        }

        hash = new MerkleHash(bytes);
        return true;
    }

    public bool Equals(MerkleHash other) => (_a, _b, _c, _d) == (other._a, other._b, other._c, other._d);

    public override bool Equals(object? obj) => obj is MerkleHash other && Equals(other);

    public override int GetHashCode() => _a.GetHashCode();

    public static bool operator ==(MerkleHash left, MerkleHash right) => left.Equals(right);

    public static bool operator !=(MerkleHash left, MerkleHash right) => !left.Equals(right);

    public override string ToString() => string.Create(64, this, static (span, hash) => hash.TryFormat(span, out _, default, null));

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        charsWritten = 0;
        if (destination.Length < 64)
        {
            return false;
        }

        _a.TryFormat(destination, out _, "x16");
        _b.TryFormat(destination[16..], out _, "x16");
        _c.TryFormat(destination[32..], out _, "x16");
        _d.TryFormat(destination[48..], out _, "x16");
        charsWritten = 64;
        return true;
    }
}
