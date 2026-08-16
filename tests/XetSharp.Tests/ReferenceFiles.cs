namespace XetSharp.Tests;

/// <summary>
/// Access to the golden files vendored from the xet-team/xet-spec-reference-files dataset, plus the
/// constants those files' one-line companions carry. See ReferenceFiles/README.md for provenance.
/// </summary>
public static class ReferenceFiles
{
    /// <summary>The reference file's Xet file ID.</summary>
    public const string FileHash = "118a53328412787fee04011dcf82fdc4acf3a4a1eddec341c910d30a306aaf97";

    /// <summary>The hash of the single xorb holding all of the reference file's chunks.</summary>
    public const string XorbHash = "eea25d6ee393ccae385820daed127b96ef0ea034dfb7cf6da3a950ce334b7632";

    /// <summary>The verification hash over every chunk in that xorb.</summary>
    public const string XorbRangeHash = "d81c11b1fc9bc2a25587108c675bbfe65ca2e5d350b0cd92c58329fcc8444178";

    /// <summary>SHA-256 of the reference file, as carried in the shard's file-metadata record.</summary>
    public const string Sha256 = "f41255b252f776125f2b657136654e4d4d5d2ccf8ef4db0ec186bd1981b69734";

    /// <summary>The key the dedupe-variant shard's chunk hashes are HMAC'd with.</summary>
    public const string DedupeHmacKey = "1234567812345678123456781234567812345678123456781234567812345678";

    public const uint FileLength = 63_527_244;

    /// <summary>How many chunk records the vendored xorb prefix contains.</summary>
    public const int XorbPrefixChunkCount = 10;

    /// <summary>Every chunk of the reference file, in order, as listed by the reference implementation.</summary>
    public static (MerkleHash Hash, ulong Length)[] Chunks { get; } = ReadChunkListing();

    public static byte[] Read(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ReferenceFiles", name));

    private static (MerkleHash Hash, ulong Length)[] ReadChunkListing() =>
    [
        .. File.ReadLines(Path.Combine(AppContext.BaseDirectory, "ReferenceFiles", "ev-population.csv.chunks"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split(' '))
            .Select(parts => (MerkleHash.Parse(parts[0]), ulong.Parse(parts[1]))),
    ];
}
