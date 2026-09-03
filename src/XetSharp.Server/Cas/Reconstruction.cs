using System.Text.Json;
using XetSharp.Server.Storage;

namespace XetSharp.Server.Cas;

/// <summary>A reconstruction the server is about to send, before it is shaped into v1 or v2 JSON.</summary>
internal sealed record Reconstruction(
    long OffsetIntoFirstRange,
    IReadOnlyList<StoredTerm> Terms,
    IReadOnlyDictionary<MerkleHash, IReadOnlyList<XorbFetchPlan>> Xorbs)
{
    /// <summary>The v2 shape: <c>xorbs</c>, each URL carrying possibly many ranges.</summary>
    public void WriteV2(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("offset_into_first_range", OffsetIntoFirstRange);
        WriteTerms(writer);

        writer.WriteStartObject("xorbs");
        foreach (var (xorb, fetches) in Xorbs)
        {
            writer.WriteStartArray(xorb.ToString());
            foreach (var fetch in fetches)
            {
                writer.WriteStartObject();
                writer.WriteString("url", fetch.Url.ToString());
                writer.WriteStartArray("ranges");
                foreach (var range in fetch.Ranges)
                {
                    writer.WriteStartObject();
                    WriteRange(writer, "chunks", range.ChunkStart, range.ChunkEnd);
                    WriteRange(writer, "bytes", range.ByteStart, range.ByteEnd);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>
    /// The deprecated v1 shape: <c>fetch_info</c>, one URL per range. Only well-formed when every
    /// fetch carries a single range, which is how the builder shapes a v1 response.
    /// </summary>
    public void WriteV1(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("offset_into_first_range", OffsetIntoFirstRange);
        WriteTerms(writer);
        WriteFetchInfo(writer, Xorbs);
        writer.WriteEndObject();
    }

    internal static void WriteFetchInfo(Utf8JsonWriter writer, IReadOnlyDictionary<MerkleHash, IReadOnlyList<XorbFetchPlan>> xorbs)
    {
        writer.WriteStartObject("fetch_info");
        foreach (var (xorb, fetches) in xorbs)
        {
            writer.WriteStartArray(xorb.ToString());
            foreach (var fetch in fetches)
            {
                foreach (var range in fetch.Ranges)
                {
                    writer.WriteStartObject();
                    WriteRange(writer, "range", range.ChunkStart, range.ChunkEnd);
                    writer.WriteString("url", fetch.Url.ToString());
                    WriteRange(writer, "url_range", range.ByteStart, range.ByteEnd);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    internal void WriteTerms(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("terms");
        foreach (var term in Terms)
        {
            writer.WriteStartObject();
            writer.WriteString("hash", term.Xorb.ToString());
            writer.WriteNumber("unpacked_length", term.UnpackedLength);
            WriteRange(writer, "range", term.ChunkStart, term.ChunkEnd);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRange(Utf8JsonWriter writer, string name, long start, long end)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("start", start);
        writer.WriteNumber("end", end);
        writer.WriteEndObject();
    }
}

/// <summary>One signed URL and the ranges it authorizes, in ascending order.</summary>
internal sealed record XorbFetchPlan(Uri Url, IReadOnlyList<XorbRangePlan> Ranges);

/// <summary>A chunk range (end-exclusive) and the serialized byte range (end-inclusive) holding it.</summary>
internal readonly record struct XorbRangePlan(int ChunkStart, int ChunkEnd, long ByteStart, long ByteEnd)
{
    public string ByteRangeSpec => $"{ByteStart}-{ByteEnd}";
}
