namespace XetSharp.Shards;

/// <summary>Fixed constants of the MDB shard binary format.</summary>
internal static class ShardFormat
{
    /// <summary>Every structural record in a shard — header, entry, bookend — is 48 bytes.</summary>
    public const int RecordSize = 48;

    public const int FooterSize = 200;

    public const ulong HeaderVersion = 2;

    public const ulong FooterVersion = 1;

    /// <summary>32-byte magic opening every shard: "HFRepoMetaData" followed by 18 fixed bytes.</summary>
    public static ReadOnlySpan<byte> HeaderTag =>
    [
        (byte)'H', (byte)'F', (byte)'R', (byte)'e', (byte)'p', (byte)'o',
        (byte)'M', (byte)'e', (byte)'t', (byte)'a', (byte)'D', (byte)'a', (byte)'t', (byte)'a',
        0, 85, 105, 103, 69, 106, 123, 129, 87, 131, 165, 189, 217, 92, 205, 209, 74, 169,
    ];

    /// <summary>
    /// Terminates both the file-info and CAS-info sections: a would-be record whose 32-byte hash
    /// field is all one-bits, followed by 16 zero bytes.
    /// </summary>
    public static ReadOnlySpan<byte> Bookend =>
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    [Flags]
    public enum FileFlags : uint
    {
        None = 0,
        WithVerification = 1u << 31,
        WithMetadataExt = 1u << 30,
    }
}
