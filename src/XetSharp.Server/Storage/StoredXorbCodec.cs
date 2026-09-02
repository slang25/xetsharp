using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using XetSharp.Shards;

namespace XetSharp.Server.Storage;

/// <summary>
/// How the disk and S3 stores persist their records. Xorb indexes get a small binary format —
/// a full-size xorb has thousands of chunks, and the index is read on every reconstruction, so
/// it should be as small as its content. File records reuse the shard format, which already
/// describes exactly a file's terms; repository revisions are JSON, since they are small and a
/// person may want to read them.
/// </summary>
internal static class StoredXorbCodec
{
    private static ReadOnlySpan<byte> Magic => "XSXI"u8;

    private const int Version = 1;

    private const int HeaderSize = 4 + 4 + 8 + 4;

    private const int RecordSize = MerkleHash.Size + 4 + 8 + 4;

    public static byte[] Encode(StoredXorb xorb)
    {
        var bytes = new byte[HeaderSize + xorb.Chunks.Length * RecordSize];
        var span = bytes.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], xorb.SerializedLength);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], xorb.Chunks.Length);

        var offset = HeaderSize;
        foreach (var chunk in xorb.Chunks)
        {
            chunk.Hash.CopyTo(span[offset..]);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 32)..], chunk.UnpackedLength);
            BinaryPrimitives.WriteInt64LittleEndian(span[(offset + 36)..], chunk.SerializedOffset);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 44)..], chunk.SerializedLength);
            offset += RecordSize;
        }

        return bytes;
    }

    public static StoredXorb Decode(MerkleHash hash, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes[..4].SequenceEqual(Magic) || BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != Version)
        {
            throw new InvalidDataException($"The stored index of xorb {hash} is not in a form this server reads.");
        }

        var serializedLength = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        if (count < 0 || bytes.Length != HeaderSize + (long)count * RecordSize)
        {
            throw new InvalidDataException($"The stored index of xorb {hash} is truncated.");
        }

        var chunks = ImmutableArray.CreateBuilder<StoredChunk>(count);
        var offset = HeaderSize;
        for (var i = 0; i < count; i++)
        {
            chunks.Add(new StoredChunk(
                new MerkleHash(bytes.Slice(offset, 32)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 32)..]),
                BinaryPrimitives.ReadInt64LittleEndian(bytes[(offset + 36)..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 44)..])));
            offset += RecordSize;
        }

        return new StoredXorb(hash, serializedLength, chunks.MoveToImmutable());
    }
}

internal static class StoredFileCodec
{
    /// <summary>A file record as a one-file shard with no CAS-info blocks and no footer.</summary>
    public static byte[] Encode(StoredFile file)
    {
        var terms = file.Terms
            .Select(term => new ShardFileTerm(term.Xorb, (uint)term.UnpackedLength, (uint)term.ChunkStart, (uint)term.ChunkEnd))
            .ToArray();
        var shard = new MdbShard
        {
            Files = [new ShardFileInfo(file.FileId, terms) { Sha256 = file.Sha256 }],
            Xorbs = [],
        };
        return shard.ToByteArray();
    }

    public static StoredFile Decode(ReadOnlySpan<byte> bytes)
    {
        var info = MdbShard.Parse(bytes).Files.Single();
        var terms = info.Terms
            .Select(term => new StoredTerm(term.XorbHash, (int)term.ChunkIndexStart, (int)term.ChunkIndexEnd, term.UnpackedLength))
            .ToImmutableArray();
        return new StoredFile(info.FileHash, terms.Sum(term => term.UnpackedLength), info.Sha256, terms);
    }
}

internal static class RepositoryRevisionCodec
{
    public static byte[] Encode(RepositoryRevision revision)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("commit", revision.CommitId);
            writer.WriteStartArray("files");
            foreach (var file in revision.Files.Values.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("fileId", file.FileId.ToString());
                writer.WriteNumber("size", file.Size);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static RepositoryRevision Decode(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        var files = new Dictionary<string, RepositoryFile>();
        foreach (var element in root.GetProperty("files").EnumerateArray())
        {
            var file = new RepositoryFile(
                element.GetProperty("path").GetString()!,
                MerkleHash.Parse(element.GetProperty("fileId").GetString()!),
                element.GetProperty("size").GetInt64(),
                element.GetProperty("sha256").GetString()!);
            files[file.Path] = file;
        }

        return new RepositoryRevision(root.GetProperty("commit").GetString()!, files);
    }
}
