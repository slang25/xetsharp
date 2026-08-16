using System.Buffers;
using System.Text.Json;

namespace XetSharp.Hub;

/// <summary>
/// Builds the Hub commit endpoint's newline-delimited JSON body: one <c>{ "key", "value" }</c>
/// envelope per line, a header first and then one line per file.
/// </summary>
/// <remarks>
/// Written by hand rather than serialized from a model. The envelope's <c>value</c> is a different
/// shape per <c>key</c>, which a typed model would only express as a union, and writing the bytes
/// directly keeps the library free of reflection.
/// </remarks>
internal static class CommitPayload
{
    public static byte[] Build(XetCommitRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>(256 + (request.Files.Count * 160));

        WriteLine(buffer, "header", value =>
        {
            value.WriteString("summary", request.Summary);
            value.WriteString("description", request.Description ?? string.Empty);
            if (request.ParentCommit is { Length: > 0 } parent)
            {
                value.WriteString("parentCommit", parent);
            }
        });

        foreach (var file in request.Files)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(file.Path);
            WriteLine(buffer, "lfsFile", value =>
            {
                value.WriteString("path", file.Path);
                value.WriteString("algo", "sha256");
                value.WriteString("oid", file.Sha256);
                value.WriteNumber("size", file.Size);
            });
        }

        foreach (var path in request.DeletedFiles)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            WriteLine(buffer, "deletedFile", value => value.WriteString("path", path));
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteLine(IBufferWriter<byte> buffer, string key, Action<Utf8JsonWriter> writeValue)
    {
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("key", key);
            writer.WriteStartObject("value");
            writeValue(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        buffer.Write("\n"u8);
    }
}
