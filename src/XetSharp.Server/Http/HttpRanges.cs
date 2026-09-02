namespace XetSharp.Server.Http;

/// <summary>Parsing and writing of HTTP byte ranges, whose ends are inclusive.</summary>
internal static class HttpRanges
{
    /// <summary>
    /// Parses a single-range <c>Range</c> header of the form the reconstruction endpoints accept:
    /// <c>bytes=start-end</c>, or <c>bytes=start-</c> for "to the end". Null for no header;
    /// throws 400 for anything else, and 416 for a start past <paramref name="length"/>.
    /// </summary>
    public static (long Start, long End)? ParseSingle(string? header, long length)
    {
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
        {
            throw XetServerException.BadRequest($"Unsupported Range header '{header}'; expected a single 'bytes=start-end'.");
        }

        var (start, end) = ParseSpec(header.AsSpan("bytes=".Length), length);
        if (start >= length && length > 0)
        {
            throw new XetServerException(416, $"The range starts at byte {start}, past the end of the {length}-byte file.");
        }

        return (start, end);
    }

    /// <summary>Parses <c>bytes=a-b,c-d,…</c> into inclusive ranges, every one of which must lie inside <paramref name="length"/>.</summary>
    public static List<(long Start, long End)> ParseMany(string header, long length)
    {
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            throw XetServerException.BadRequest($"Unsupported Range header '{header}'.");
        }

        var ranges = new List<(long, long)>();
        foreach (var spec in header.AsSpan("bytes=".Length).ToString().Split(','))
        {
            var (start, end) = ParseSpec(spec.Trim(), length);
            if (start >= length)
            {
                throw new XetServerException(416, $"Range {start}-{end} lies past the end of the {length}-byte object.");
            }

            ranges.Add((start, end));
        }

        return ranges;
    }

    private static (long Start, long End) ParseSpec(ReadOnlySpan<char> spec, long length)
    {
        var dash = spec.IndexOf('-');
        if (dash <= 0 || !long.TryParse(spec[..dash], out var start) || start < 0)
        {
            throw XetServerException.BadRequest($"Malformed byte range '{spec}'.");
        }

        var endText = spec[(dash + 1)..];
        long end;
        if (endText.IsEmpty)
        {
            end = length - 1;
        }
        else if (!long.TryParse(endText, out end) || end < start)
        {
            throw XetServerException.BadRequest($"Malformed byte range '{spec}'.");
        }

        return (start, Math.Min(end, Math.Max(length - 1, 0)));
    }
}
