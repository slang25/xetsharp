using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using XetSharp.Hashing;

namespace XetSharp.Shards;

/// <summary>
/// An MDB shard: the binary container that carries file reconstructions and xorb chunk listings.
/// It is the request body for shard upload, the response body of the global-deduplication API, and
/// the reference implementation's on-disk dedup cache.
/// </summary>
public sealed record MdbShard
{
    /// <summary>File reconstructions. Empty in a global-deduplication response.</summary>
    public required IReadOnlyList<ShardFileInfo> Files { get; init; }

    /// <summary>Chunk listings, one per xorb.</summary>
    public required IReadOnlyList<ShardCasInfo> Xorbs { get; init; }

    /// <summary>
    /// The footer, or null when the shard has none. A shard serialized as an upload body MUST NOT
    /// have one, so leave this null when building a shard to upload.
    /// </summary>
    public ShardFooter? Footer { get; init; }

    /// <summary>Reads a shard from its serialized bytes, validating magic, versions and offsets.</summary>
    public static MdbShard Parse(ReadOnlySpan<byte> shard)
    {
        if (shard.Length < ShardFormat.RecordSize)
        {
            throw new InvalidDataException($"A shard is at least {ShardFormat.RecordSize} bytes; got {shard.Length}.");
        }

        if (!shard[..32].SequenceEqual(ShardFormat.HeaderTag))
        {
            throw new InvalidDataException("Shard does not start with the MDB shard header tag.");
        }

        var version = BinaryPrimitives.ReadUInt64LittleEndian(shard[32..]);
        if (version != ShardFormat.HeaderVersion)
        {
            throw new InvalidDataException($"Unsupported shard header version {version}; expected {ShardFormat.HeaderVersion}.");
        }

        var footerSize = BinaryPrimitives.ReadUInt64LittleEndian(shard[40..]);
        if (footerSize is not (0 or ShardFormat.FooterSize))
        {
            throw new InvalidDataException($"Shard header declares a {footerSize}-byte footer; expected 0 or {ShardFormat.FooterSize}.");
        }

        var offset = ShardFormat.RecordSize;
        var files = ReadFileInfoSection(shard, ref offset);
        var casInfoOffset = offset;
        var xorbs = ReadCasInfoSection(shard, ref offset);

        if (footerSize == 0)
        {
            return new MdbShard { Files = files, Xorbs = xorbs };
        }

        if (offset != shard.Length - ShardFormat.FooterSize)
        {
            throw new InvalidDataException(
                $"Shard sections end at offset {offset} but the footer starts at {shard.Length - ShardFormat.FooterSize}.");
        }

        return new MdbShard
        {
            Files = files,
            Xorbs = xorbs,
            Footer = ReadFooter(shard[offset..], casInfoOffset, offset),
        };
    }

    /// <summary>
    /// Serializes the shard, including a footer only when <see cref="Footer"/> is set. Returns the
    /// number of bytes written.
    /// </summary>
    public int WriteTo(IBufferWriter<byte> destination)
    {
        ValidateForWriting();

        var fileInfoSize = Files.Sum(RecordCount) * ShardFormat.RecordSize + ShardFormat.RecordSize;
        var casInfoSize = Xorbs.Sum(xorb => xorb.Chunks.Count + 1) * ShardFormat.RecordSize + ShardFormat.RecordSize;
        var footerSize = Footer is null ? 0 : ShardFormat.FooterSize;

        var casInfoOffset = ShardFormat.RecordSize + fileInfoSize;
        var footerOffset = casInfoOffset + casInfoSize;
        var total = footerOffset + footerSize;

        var buffer = destination.GetSpan(total)[..total];
        buffer.Clear();

        ShardFormat.HeaderTag.CopyTo(buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[32..], ShardFormat.HeaderVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[40..], (ulong)footerSize);

        var offset = ShardFormat.RecordSize;
        WriteFileInfoSection(buffer, ref offset);
        WriteCasInfoSection(buffer, ref offset);

        if (Footer is not null)
        {
            WriteFooter(buffer[offset..], casInfoOffset, footerOffset);
        }

        destination.Advance(total);
        return total;
    }

    /// <summary>Serializes the shard into a new array.</summary>
    public byte[] ToByteArray()
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteTo(writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Looks up a chunk by its unprotected hash, applying the footer's HMAC key when the shard is
    /// protected — the matching step of global deduplication. On a hit, the file being uploaded can
    /// reference <paramref name="xorb"/> at <paramref name="chunkIndex"/> instead of re-uploading.
    /// </summary>
    public bool TryFindChunk(MerkleHash chunkHash, [NotNullWhen(true)] out ShardCasInfo? xorb, out int chunkIndex)
    {
        var key = Footer?.ChunkHashHmacKey ?? MerkleHash.Zero;
        var wanted = key == MerkleHash.Zero ? chunkHash : XetHashes.Hmac(chunkHash, key);

        foreach (var candidate in Xorbs)
        {
            for (var i = 0; i < candidate.Chunks.Count; i++)
            {
                if (candidate.Chunks[i].Hash == wanted)
                {
                    (xorb, chunkIndex) = (candidate, i);
                    return true;
                }
            }
        }

        (xorb, chunkIndex) = (null, -1);
        return false;
    }

    private static int RecordCount(ShardFileInfo file) =>
        1 + file.Terms.Count + (file.HasVerification ? file.Terms.Count : 0) + (file.Sha256 is null ? 0 : 1);

    private void ValidateForWriting()
    {
        foreach (var file in Files)
        {
            if (file.Terms.Any(term => (term.VerificationHash is not null) != file.HasVerification))
            {
                throw new InvalidOperationException(
                    $"File {file.FileHash} mixes terms with and without verification hashes; the format requires all or none.");
            }
        }

        if (Files.Select(file => file.HasVerification).Distinct().Count() > 1)
        {
            throw new InvalidOperationException(
                "Some but not all files in the shard carry verification hashes; a shard must be consistent across files.");
        }
    }

    private static List<ShardFileInfo> ReadFileInfoSection(ReadOnlySpan<byte> shard, ref int offset)
    {
        var files = new List<ShardFileInfo>();
        while (!TryConsumeBookend(shard, ref offset))
        {
            var header = ReadRecord(shard, ref offset);
            var fileHash = new MerkleHash(header[..32]);
            var flags = (ShardFormat.FileFlags)BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
            var termCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);

            var xorbHashes = new MerkleHash[termCount];
            var lengths = new uint[termCount];
            var starts = new uint[termCount];
            var ends = new uint[termCount];
            for (var i = 0; i < termCount; i++)
            {
                var entry = ReadRecord(shard, ref offset);
                xorbHashes[i] = new MerkleHash(entry[..32]);
                lengths[i] = BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]);
                starts[i] = BinaryPrimitives.ReadUInt32LittleEndian(entry[40..]);
                ends[i] = BinaryPrimitives.ReadUInt32LittleEndian(entry[44..]);
            }

            var terms = new ShardFileTerm[termCount];
            for (var i = 0; i < termCount; i++)
            {
                MerkleHash? verification = flags.HasFlag(ShardFormat.FileFlags.WithVerification)
                    ? new MerkleHash(ReadRecord(shard, ref offset)[..32])
                    : null;
                terms[i] = new ShardFileTerm(xorbHashes[i], lengths[i], starts[i], ends[i], verification);
            }

            files.Add(new ShardFileInfo(fileHash, terms)
            {
                Sha256 = flags.HasFlag(ShardFormat.FileFlags.WithMetadataExt)
                    ? new MerkleHash(ReadRecord(shard, ref offset)[..32])
                    : null,
            });
        }

        return files;
    }

    private static List<ShardCasInfo> ReadCasInfoSection(ReadOnlySpan<byte> shard, ref int offset)
    {
        var xorbs = new List<ShardCasInfo>();
        while (!TryConsumeBookend(shard, ref offset))
        {
            var header = ReadRecord(shard, ref offset);
            var xorbHash = new MerkleHash(header[..32]);
            var chunkCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);
            var totalUncompressed = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
            var serializedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[44..]);

            var chunks = new ShardCasChunk[chunkCount];
            for (var i = 0; i < chunkCount; i++)
            {
                var entry = ReadRecord(shard, ref offset);
                chunks[i] = new ShardCasChunk(
                    new MerkleHash(entry[..32]),
                    BinaryPrimitives.ReadUInt32LittleEndian(entry[32..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]));
            }

            xorbs.Add(new ShardCasInfo(xorbHash, totalUncompressed, serializedLength, chunks));
        }

        return xorbs;
    }

    private static ShardFooter ReadFooter(ReadOnlySpan<byte> footer, int casInfoOffset, int footerOffset)
    {
        var version = BinaryPrimitives.ReadUInt64LittleEndian(footer);
        if (version != ShardFormat.FooterVersion)
        {
            throw new InvalidDataException($"Unsupported shard footer version {version}; expected {ShardFormat.FooterVersion}.");
        }

        Expect(BinaryPrimitives.ReadUInt64LittleEndian(footer[8..]), (ulong)ShardFormat.RecordSize, "file_info_offset");
        Expect(BinaryPrimitives.ReadUInt64LittleEndian(footer[16..]), (ulong)casInfoOffset, "cas_info_offset");
        Expect(BinaryPrimitives.ReadUInt64LittleEndian(footer[192..]), (ulong)footerOffset, "footer_offset");

        // The spec calls bytes 24-71 reserved; the reference implementation puts three
        // (offset, entry count) pairs there, one per optional lookup table. A shard carrying
        // lookup tables would already have failed the "sections end where the footer starts"
        // check, so all we can meet here is the empty form: each offset at the footer, no entries.
        for (var pair = 0; pair < 3; pair++)
        {
            Expect(BinaryPrimitives.ReadUInt64LittleEndian(footer[(24 + pair * 16)..]), (ulong)footerOffset, "a lookup table offset");
            Expect(BinaryPrimitives.ReadUInt64LittleEndian(footer[(32 + pair * 16)..]), 0, "a lookup table entry count");
        }

        return new ShardFooter
        {
            ChunkHashHmacKey = new MerkleHash(footer[72..104]),
            CreatedAt = ToTimestamp(BinaryPrimitives.ReadUInt64LittleEndian(footer[104..]), "shard_creation_timestamp"),
            ExpiresAt = ToTimestamp(BinaryPrimitives.ReadUInt64LittleEndian(footer[112..]), "shard_key_expiry"),
            StoredBytesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(footer[168..]),
            MaterializedBytes = BinaryPrimitives.ReadUInt64LittleEndian(footer[176..]),
            StoredBytes = BinaryPrimitives.ReadUInt64LittleEndian(footer[184..]),
        };

        static void Expect(ulong actual, ulong expected, string field)
        {
            if (actual != expected)
            {
                throw new InvalidDataException($"Shard footer {field} is {actual} but the sections place it at {expected}.");
            }
        }
    }

    private void WriteFileInfoSection(Span<byte> shard, ref int offset)
    {
        foreach (var file in Files)
        {
            var flags = ShardFormat.FileFlags.None;
            if (file.HasVerification)
            {
                flags |= ShardFormat.FileFlags.WithVerification;
            }

            if (file.Sha256 is not null)
            {
                flags |= ShardFormat.FileFlags.WithMetadataExt;
            }

            var header = TakeRecord(shard, ref offset);
            file.FileHash.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[32..], (uint)flags);
            BinaryPrimitives.WriteUInt32LittleEndian(header[36..], (uint)file.Terms.Count);

            foreach (var term in file.Terms)
            {
                var entry = TakeRecord(shard, ref offset);
                term.XorbHash.CopyTo(entry);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], term.UnpackedLength);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[40..], term.ChunkIndexStart);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[44..], term.ChunkIndexEnd);
            }

            if (file.HasVerification)
            {
                foreach (var term in file.Terms)
                {
                    term.VerificationHash!.Value.CopyTo(TakeRecord(shard, ref offset));
                }
            }

            if (file.Sha256 is { } sha256)
            {
                sha256.CopyTo(TakeRecord(shard, ref offset));
            }
        }

        WriteBookend(shard, ref offset);
    }

    private void WriteCasInfoSection(Span<byte> shard, ref int offset)
    {
        foreach (var xorb in Xorbs)
        {
            var header = TakeRecord(shard, ref offset);
            xorb.XorbHash.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[36..], (uint)xorb.Chunks.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..], xorb.TotalUncompressedBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(header[44..], xorb.SerializedLength);

            foreach (var chunk in xorb.Chunks)
            {
                var entry = TakeRecord(shard, ref offset);
                chunk.Hash.CopyTo(entry);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[32..], chunk.ByteRangeStart);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], chunk.UnpackedLength);
            }
        }

        WriteBookend(shard, ref offset);
    }

    private void WriteFooter(Span<byte> footer, int casInfoOffset, int footerOffset)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(footer, ShardFormat.FooterVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(footer[8..], ShardFormat.RecordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(footer[16..], (ulong)casInfoOffset);

        // Empty lookup tables: each starts where the footer does and holds nothing.
        for (var pair = 0; pair < 3; pair++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(footer[(24 + pair * 16)..], (ulong)footerOffset);
        }

        Footer!.ChunkHashHmacKey.CopyTo(footer[72..]);
        BinaryPrimitives.WriteUInt64LittleEndian(footer[104..], FromTimestamp(Footer.CreatedAt));
        BinaryPrimitives.WriteUInt64LittleEndian(footer[112..], FromTimestamp(Footer.ExpiresAt));
        BinaryPrimitives.WriteUInt64LittleEndian(footer[168..], Footer.StoredBytesOnDisk);
        BinaryPrimitives.WriteUInt64LittleEndian(footer[176..], Footer.MaterializedBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(footer[184..], Footer.StoredBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(footer[192..], (ulong)footerOffset);
    }

    private static ReadOnlySpan<byte> ReadRecord(ReadOnlySpan<byte> shard, ref int offset)
    {
        if (offset + ShardFormat.RecordSize > shard.Length)
        {
            throw new InvalidDataException($"Shard is truncated: a {ShardFormat.RecordSize}-byte record at offset {offset} runs past its end.");
        }

        var record = shard.Slice(offset, ShardFormat.RecordSize);
        offset += ShardFormat.RecordSize;
        return record;
    }

    private static Span<byte> TakeRecord(Span<byte> shard, ref int offset)
    {
        var record = shard.Slice(offset, ShardFormat.RecordSize);
        offset += ShardFormat.RecordSize;
        return record;
    }

    /// <summary>
    /// Consumes and reports the section-terminating bookend. Only the 32-byte hash field is tested,
    /// as the spec directs: an all-one-bits hash where a header would be marks the end.
    /// </summary>
    private static bool TryConsumeBookend(ReadOnlySpan<byte> shard, ref int offset)
    {
        if (offset + ShardFormat.RecordSize > shard.Length)
        {
            throw new InvalidDataException($"Shard is truncated: no section bookend at offset {offset}.");
        }

        if (!shard.Slice(offset, 32).ContainsAnyExcept((byte)0xFF))
        {
            offset += ShardFormat.RecordSize;
            return true;
        }

        return false;
    }

    private static void WriteBookend(Span<byte> shard, ref int offset) =>
        ShardFormat.Bookend.CopyTo(TakeRecord(shard, ref offset));

    private static DateTimeOffset? ToTimestamp(ulong seconds, string field)
    {
        if (seconds == 0)
        {
            return null;
        }

        if (seconds > (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            throw new InvalidDataException($"Shard footer {field} of {seconds} is not a representable time.");
        }

        return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
    }

    private static ulong FromTimestamp(DateTimeOffset? value) =>
        value is null ? 0 : (ulong)value.Value.ToUnixTimeSeconds();
}
