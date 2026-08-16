using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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

        // Both forms have to account for every byte. Anything left over is either a lookup section
        // (which this library does not read) or trailing junk, and silently dropping it would mean
        // re-serializing to something shorter than what came in.
        var expectedEnd = shard.Length - (int)footerSize;
        if (offset != expectedEnd)
        {
            throw new InvalidDataException(
                $"Shard sections end at offset {offset}, but the {(footerSize == 0 ? "shard itself" : "footer")} ends at {expectedEnd}.");
        }

        if (footerSize == 0)
        {
            return new MdbShard { Files = files, Xorbs = xorbs };
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

        if (ChunkIndexFor(Xorbs).TryGetValue(wanted, out var location))
        {
            (xorb, chunkIndex) = location;
            return true;
        }

        (xorb, chunkIndex) = (null, -1);
        return false;
    }

    /// <summary>
    /// The chunk-hash index for a CAS-info listing, built on first use and reused thereafter.
    /// Deduplication walks every local chunk past the same shard, so rescanning the entries per
    /// lookup would make matching quadratic in the two chunk counts.
    /// </summary>
    /// <remarks>
    /// Keyed off the listing rather than held in a field: a record field would be copied by a
    /// <c>with</c> expression that replaces <see cref="Xorbs"/>, leaving the copy answering from
    /// the list it did not get, and would drag a cache into the generated equality.
    /// </remarks>
    private static Dictionary<MerkleHash, (ShardCasInfo Xorb, int Index)> ChunkIndexFor(IReadOnlyList<ShardCasInfo> xorbs) =>
        ChunkIndexes.GetValue(xorbs, static listing =>
        {
            var entries = new Dictionary<MerkleHash, (ShardCasInfo, int)>();
            foreach (var xorb in listing)
            {
                for (var i = 0; i < xorb.Chunks.Count; i++)
                {
                    // A chunk may be listed more than once; the first place it appears will do.
                    entries.TryAdd(xorb.Chunks[i].Hash, (xorb, i));
                }
            }

            return entries;
        });

    private static readonly ConditionalWeakTable<IReadOnlyList<ShardCasInfo>, Dictionary<MerkleHash, (ShardCasInfo Xorb, int Index)>> ChunkIndexes = new();

    /// <summary>
    /// Validates a record count a shard declares about itself before anything is allocated for it.
    /// The count is attacker-controlled — a 48-byte header can claim four billion entries — so it
    /// has to be checked against the bytes actually left, not trusted and then discovered to be
    /// short one record at a time.
    /// </summary>
    private static uint CheckedRecordCount(uint declared, int recordsPerItem, int trailingRecords, int bytesRemaining, string what)
    {
        var needed = ((long)declared * recordsPerItem + trailingRecords) * ShardFormat.RecordSize;
        if (needed > bytesRemaining)
        {
            throw new InvalidDataException(
                $"Shard declares {declared} {what}, needing {needed} bytes, but only {bytesRemaining} remain.");
        }

        return declared;
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
            var declaredTerms = BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);

            var recordsPerTerm = flags.HasFlag(ShardFormat.FileFlags.WithVerification) ? 2 : 1;
            var trailingRecords = flags.HasFlag(ShardFormat.FileFlags.WithMetadataExt) ? 1 : 0;
            var termCount = (int)CheckedRecordCount(
                declaredTerms,
                recordsPerTerm,
                trailingRecords,
                shard.Length - offset,
                "terms");

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
            var chunkCount = (int)CheckedRecordCount(
                BinaryPrimitives.ReadUInt32LittleEndian(header[36..]),
                recordsPerItem: 1,
                trailingRecords: 0,
                shard.Length - offset,
                "chunks");
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
