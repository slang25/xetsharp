using System.Globalization;
using System.Text;
using XetSharp.Cas;

namespace XetSharp.Download;

/// <summary>One part of a <c>multipart/byteranges</c> body: what it covers, and the bytes.</summary>
internal readonly record struct MultipartByteRangePart(ByteRange Range, ReadOnlyMemory<byte> Body);

/// <summary>
/// A reader for <c>multipart/byteranges</c> responses (RFC 9110 §14.6), which is what a CDN sends
/// back when a single request asks for several byte ranges. <see cref="HttpClient"/> hands the body
/// over unparsed, so the client has to split it itself.
/// </summary>
internal static class MultipartByteRanges
{
    /// <summary>Splits a body into its parts, in the order the server sent them.</summary>
    public static List<MultipartByteRangePart> Parse(ReadOnlyMemory<byte> body, string boundary)
    {
        ArgumentException.ThrowIfNullOrEmpty(boundary);
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var span = body.Span;

        var position = span.IndexOf(delimiter);
        if (position < 0)
        {
            throw new InvalidDataException($"The multipart response does not contain the boundary '{boundary}' the headers announced.");
        }

        var parts = new List<MultipartByteRangePart>();
        position += delimiter.Length;
        while (true)
        {
            // The closing delimiter is the boundary followed by two more dashes.
            if (span[position..].StartsWith("--"u8))
            {
                return parts;
            }

            var headersStart = position + LineBreakLength(span, position, required: true);
            var range = ReadContentRange(span, headersStart, out var contentStart);

            var next = span[contentStart..].IndexOf(delimiter);
            if (next < 0)
            {
                throw new InvalidDataException("The multipart response ends in the middle of a part.");
            }

            var delimiterStart = contentStart + next;
            var contentEnd = delimiterStart - TrailingLineBreakLength(span, delimiterStart);
            parts.Add(new MultipartByteRangePart(range, body[contentStart..contentEnd]));
            position = delimiterStart + delimiter.Length;
        }
    }

    /// <summary>
    /// Walks a part's headers, returning the range its <c>Content-Range</c> declares and where the
    /// part's data starts.
    /// </summary>
    private static ByteRange ReadContentRange(ReadOnlySpan<byte> span, int position, out int contentStart)
    {
        ByteRange? range = null;
        while (position < span.Length)
        {
            // An empty line closes the header block; whatever follows is the part's data.
            if (span[position] == (byte)'\n' ||
                (span[position] == (byte)'\r' && position + 1 < span.Length && span[position + 1] == (byte)'\n'))
            {
                contentStart = position + (span[position] == (byte)'\n' ? 1 : 2);
                return range ?? throw new InvalidDataException("A multipart part has no Content-Range header.");
            }

            var lineEnd = span[position..].IndexOf((byte)'\n');
            if (lineEnd < 0)
            {
                throw new InvalidDataException("A multipart part's headers are truncated.");
            }

            var line = span.Slice(position, lineEnd).TrimEnd((byte)'\r');
            if (StartsWithIgnoreCase(line, "content-range:"u8))
            {
                range = ParseContentRange(Encoding.ASCII.GetString(line["content-range:".Length..]));
            }

            position += lineEnd + 1;
        }

        throw new InvalidDataException("A multipart part's headers are truncated.");
    }

    /// <summary>Parses a <c>bytes {start}-{end}/{total}</c> value; the total may be <c>*</c>.</summary>
    private static ByteRange ParseContentRange(string value)
    {
        var text = value.AsSpan().Trim();
        if (!text.StartsWith("bytes", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported Content-Range unit in '{value.Trim()}'.");
        }

        text = text["bytes".Length..].Trim();
        var slash = text.IndexOf('/');
        if (slash >= 0)
        {
            text = text[..slash];
        }

        var dash = text.IndexOf('-');
        if (dash <= 0 ||
            !long.TryParse(text[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !long.TryParse(text[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
            end < start)
        {
            throw new InvalidDataException($"Malformed Content-Range '{value.Trim()}'.");
        }

        return new ByteRange(start, end);
    }

    /// <summary>
    /// The length of the line break at <paramref name="position"/>. Transport padding (spaces or
    /// tabs before the break) is tolerated, as the multipart grammar allows.
    /// </summary>
    private static int LineBreakLength(ReadOnlySpan<byte> span, int position, bool required)
    {
        var start = position;
        while (position < span.Length && span[position] is (byte)' ' or (byte)'\t')
        {
            position++;
        }

        if (position < span.Length && span[position] == (byte)'\r' && position + 1 < span.Length && span[position + 1] == (byte)'\n')
        {
            return position - start + 2;
        }

        if (position < span.Length && span[position] == (byte)'\n')
        {
            return position - start + 1;
        }

        return required
            ? throw new InvalidDataException("The multipart response is missing a line break where one is required.")
            : 0;
    }

    private static int TrailingLineBreakLength(ReadOnlySpan<byte> span, int delimiterStart)
    {
        if (delimiterStart >= 2 && span[delimiterStart - 2] == (byte)'\r' && span[delimiterStart - 1] == (byte)'\n')
        {
            return 2;
        }

        return delimiterStart >= 1 && span[delimiterStart - 1] == (byte)'\n' ? 1 : 0;
    }

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> line, ReadOnlySpan<byte> lowercasePrefix)
    {
        if (line.Length < lowercasePrefix.Length)
        {
            return false;
        }

        for (var i = 0; i < lowercasePrefix.Length; i++)
        {
            var character = line[i] is >= (byte)'A' and <= (byte)'Z' ? (byte)(line[i] + 32) : line[i];
            if (character != lowercasePrefix[i])
            {
                return false;
            }
        }

        return true;
    }
}
