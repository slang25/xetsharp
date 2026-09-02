using XetSharp.Server.Auth;
using XetSharp.Server.Storage;

namespace XetSharp.Server.Cas;

/// <summary>
/// Turns a registered file into a reconstruction: the terms covering the requested bytes, trimmed
/// to whole chunks the way the reference service trims them, and a signed URL per xorb covering
/// every byte range those terms need.
/// </summary>
internal sealed class ReconstructionBuilder(IXetStore store, XorbUrlSigner signer, XetServerOptions options)
{
    /// <summary>
    /// Signed URLs are capped in length by what proxies and clients tolerate; a xorb needing more
    /// ranges than fit is handed out as several URLs, which the v2 shape allows.
    /// </summary>
    private const int MaxRangesPerUrl = 256;

    /// <param name="range">The requested bytes, inclusive end, or null for the whole file.</param>
    /// <param name="oneRangePerUrl">
    /// Whether every URL must carry exactly one range — what the v1 shape needs, and what a store
    /// serving presigned URLs always does.
    /// </param>
    /// <exception cref="XetServerException">416 when the range starts past the end of the file.</exception>
    public async Task<Reconstruction> BuildAsync(
        StoredFile file,
        (long Start, long End)? range,
        Uri baseUrl,
        bool oneRangePerUrl,
        CancellationToken cancellationToken)
    {
        var (offset, terms) = await SelectTermsAsync(file, range, cancellationToken).ConfigureAwait(false);

        var xorbs = new Dictionary<MerkleHash, IReadOnlyList<XorbFetchPlan>>();
        foreach (var group in terms.GroupBy(term => term.Xorb))
        {
            var xorb = (await store.GetXorbAsync(group.Key, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidOperationException($"File {file.FileId} references xorb {group.Key}, which is no longer stored.");
            var ranges = Coalesce(group.Select(term => (term.ChunkStart, term.ChunkEnd)))
                .Select(chunks =>
                {
                    var (byteStart, byteEnd) = xorb.ByteRangeOf(chunks.Start, chunks.End);
                    return new XorbRangePlan(chunks.Start, chunks.End, byteStart, byteEnd);
                })
                .ToList();

            xorbs[group.Key] = await PlanFetchesAsync(xorb.Hash, ranges, baseUrl, oneRangePerUrl, cancellationToken).ConfigureAwait(false);
        }

        return new Reconstruction(offset, terms, xorbs);
    }

    /// <summary>
    /// The terms that cover the requested bytes, each cut down to the chunks that overlap the
    /// request. The first kept chunk may start before the range does, which is what
    /// <c>offset_into_first_range</c> tells the client to skip.
    /// </summary>
    private async Task<(long Offset, List<StoredTerm> Terms)> SelectTermsAsync(
        StoredFile file,
        (long Start, long End)? range,
        CancellationToken cancellationToken)
    {
        if (range is null)
        {
            return (0, [.. file.Terms]);
        }

        var (start, end) = range.Value;
        if (start >= file.Size && file.Size > 0)
        {
            throw new XetServerException(416, $"The range starts at byte {start}, past the end of the {file.Size}-byte file.");
        }

        end = Math.Min(end, file.Size - 1);

        var selected = new List<StoredTerm>();
        var offsetIntoFirst = 0L;
        var position = 0L;
        foreach (var term in file.Terms)
        {
            var termEnd = position + term.UnpackedLength;
            if (termEnd <= start || position > end)
            {
                position = termEnd;
                continue;
            }

            var xorb = (await store.GetXorbAsync(term.Xorb, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidOperationException($"File {file.FileId} references xorb {term.Xorb}, which is no longer stored.");

            // Walk the term's chunks, keeping those that overlap [start, end].
            var chunkStart = term.ChunkStart;
            var chunkEnd = term.ChunkEnd;
            var chunkPosition = position;
            var keptStart = -1;
            var keptStartOffset = 0L;
            for (var i = term.ChunkStart; i < term.ChunkEnd; i++)
            {
                var chunkLength = xorb.Chunks[i].UnpackedLength;
                var chunkTail = chunkPosition + chunkLength;
                if (keptStart < 0 && chunkTail > start)
                {
                    keptStart = i;
                    keptStartOffset = chunkPosition;
                }

                if (chunkPosition > end)
                {
                    chunkEnd = i;
                    break;
                }

                chunkPosition = chunkTail;
            }

            chunkStart = keptStart;
            if (selected.Count == 0)
            {
                offsetIntoFirst = start - keptStartOffset;
            }

            selected.Add(new StoredTerm(term.Xorb, chunkStart, chunkEnd, xorb.UnpackedLengthOf(chunkStart, chunkEnd)));
            position = termEnd;
        }

        return (offsetIntoFirst, selected);
    }

    /// <summary>Merges overlapping and adjacent chunk ranges, so a chunk two terms share is fetched once.</summary>
    internal static List<(int Start, int End)> Coalesce(IEnumerable<(int Start, int End)> ranges)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private async Task<IReadOnlyList<XorbFetchPlan>> PlanFetchesAsync(
        MerkleHash xorb,
        List<XorbRangePlan> ranges,
        Uri baseUrl,
        bool oneRangePerUrl,
        CancellationToken cancellationToken)
    {
        var fetches = new List<XorbFetchPlan>();

        // A store that can hand out URLs of its own (S3 presigning) serves the bytes itself, one
        // URL per range: object stores do not answer multi-range requests.
        var direct = await store.CreateDirectDownloadUrlAsync(xorb, ranges[0].ByteStart, ranges[0].ByteEnd, options.DownloadUrlLifetime, cancellationToken)
            .ConfigureAwait(false);
        if (direct is not null)
        {
            fetches.Add(new XorbFetchPlan(direct, [ranges[0]]));
            foreach (var range in ranges.Skip(1))
            {
                var url = await store.CreateDirectDownloadUrlAsync(xorb, range.ByteStart, range.ByteEnd, options.DownloadUrlLifetime, cancellationToken)
                    .ConfigureAwait(false);
                fetches.Add(new XorbFetchPlan(url!, [range]));
            }

            return fetches;
        }

        var perUrl = oneRangePerUrl ? 1 : MaxRangesPerUrl;
        for (var i = 0; i < ranges.Count; i += perUrl)
        {
            var batch = ranges.Skip(i).Take(perUrl).ToList();
            var spec = "bytes=" + string.Join(',', batch.Select(range => range.ByteRangeSpec));
            fetches.Add(new XorbFetchPlan(signer.Sign(baseUrl, xorb, spec, options.DownloadUrlLifetime), batch));
        }

        return fetches;
    }
}
