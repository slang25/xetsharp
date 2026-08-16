using XetSharp.Cas;

namespace XetSharp.Download;

/// <summary>A run of chunks a term takes from one fetched range.</summary>
internal sealed record TermSegment(int FetchOrdinal, XorbRangeDescriptor Range, ChunkRange Chunks);

/// <summary>A term, split into the fetched ranges it draws from.</summary>
internal sealed record PlannedTerm(ReconstructionTerm Term, IReadOnlyList<TermSegment> Segments);

/// <summary>
/// The download schedule for one reconstruction: which fetches to issue, in what order, and how
/// long each one's data has to stay in memory.
/// </summary>
/// <remarks>
/// Fetches are ordered by the term that first needs them, so downloading ahead of the writer never
/// downloads something that will not be wanted soon. How far ahead it runs is bounded by
/// <see cref="XetDownloadOptions.MaxBufferedBytes"/>, with the range the writer is actually waiting
/// on always allowed through — otherwise a single range larger than the budget would deadlock.
/// </remarks>
internal sealed class FetchPlan : IDisposable
{
    private readonly PlannedFetch[] _fetches;
    private readonly Dictionary<int, Task<XorbRangeData[]>> _started = [];
    private readonly XorbRangeFetcher _fetcher;
    private readonly XetDownloadOptions _options;
    private readonly CancellationTokenSource _cancellation;
    private long _bufferedBytes;
    private int _nextToStart;

    private FetchPlan(
        PlannedFetch[] fetches,
        IReadOnlyList<PlannedTerm> terms,
        XorbRangeFetcher fetcher,
        XetDownloadOptions options,
        CancellationToken cancellationToken)
    {
        _fetches = fetches;
        Terms = terms;
        _fetcher = fetcher;
        _options = options;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public IReadOnlyList<PlannedTerm> Terms { get; }

    public static FetchPlan Create(
        FileReconstruction reconstruction,
        XorbRangeFetcher fetcher,
        XetDownloadOptions options,
        CancellationToken cancellationToken)
    {
        var ordinals = new Dictionary<XorbFetch, int>(ReferenceEqualityComparer.Instance);
        var fetches = new List<PlannedFetch>();
        var terms = new List<PlannedTerm>(reconstruction.Terms.Count);

        for (var termIndex = 0; termIndex < reconstruction.Terms.Count; termIndex++)
        {
            var term = reconstruction.Terms[termIndex];
            var segments = new List<TermSegment>();
            var segmentStart = term.Chunks.Start;
            TermSegment? current = null;

            for (var chunk = term.Chunks.Start; chunk < term.Chunks.End; chunk++)
            {
                if (!reconstruction.TryFindRange(term.Xorb, chunk, out var fetch, out var range))
                {
                    throw new InvalidDataException($"The reconstruction does not cover chunk {chunk} of xorb {term.Xorb}.");
                }

                if (!ordinals.TryGetValue(fetch, out var ordinal))
                {
                    ordinal = fetches.Count;
                    ordinals[fetch] = ordinal;
                    fetches.Add(new PlannedFetch(fetch));
                }

                fetches[ordinal].LastTermIndex = termIndex;

                // Consecutive chunks from the same fetched range become one segment.
                if (current is null || current.FetchOrdinal != ordinal || current.Range != range)
                {
                    if (current is not null)
                    {
                        segments.Add(current with { Chunks = new ChunkRange(segmentStart, chunk) });
                        segmentStart = chunk;
                    }

                    current = new TermSegment(ordinal, range, default);
                }
            }

            if (current is not null)
            {
                segments.Add(current with { Chunks = new ChunkRange(segmentStart, term.Chunks.End) });
            }

            terms.Add(new PlannedTerm(term, segments));
        }

        return new FetchPlan([.. fetches], terms, fetcher, options, cancellationToken);
    }

    /// <summary>Waits for the data a segment needs, starting downloads as far ahead as the budget allows.</summary>
    public async ValueTask<XorbRangeData> GetRangeAsync(TermSegment segment)
    {
        StartThrough(segment.FetchOrdinal);
        var ranges = await _started[segment.FetchOrdinal].ConfigureAwait(false);
        foreach (var range in ranges)
        {
            if (range.Descriptor == segment.Range)
            {
                return range;
            }
        }

        throw new InvalidDataException($"The fetched xorb data does not include the range {segment.Range.Bytes} it was asked for.");
    }

    /// <summary>Drops the data of every fetch whose last term has now been written.</summary>
    public void ReleaseCompleted(int termIndex)
    {
        foreach (var (ordinal, _) in _started.ToArray())
        {
            if (_fetches[ordinal].LastTermIndex <= termIndex)
            {
                _started.Remove(ordinal);
                _bufferedBytes -= _fetches[ordinal].Fetch.ByteCount;
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        foreach (var task in _started.Values)
        {
            // The writer may be unwinding from its own failure; make sure nothing here is left as an
            // unobserved exception.
            _ = task.ContinueWith(static completed => _ = completed.Exception, TaskScheduler.Default);
        }

        _started.Clear();
        _cancellation.Dispose();
    }

    private void StartThrough(int requiredOrdinal)
    {
        while (_nextToStart < _fetches.Length)
        {
            var planned = _fetches[_nextToStart];
            if (_nextToStart > requiredOrdinal)
            {
                if (_nextToStart - requiredOrdinal > _options.MaxConcurrentDownloads ||
                    (_bufferedBytes > 0 && _bufferedBytes + planned.Fetch.ByteCount > _options.MaxBufferedBytes))
                {
                    return;
                }
            }

            _started[_nextToStart] = _fetcher.FetchAsync(planned.Fetch, _cancellation.Token);
            _bufferedBytes += planned.Fetch.ByteCount;
            _nextToStart++;
        }
    }

    private sealed class PlannedFetch(XorbFetch fetch)
    {
        public XorbFetch Fetch { get; } = fetch;

        /// <summary>The last term that draws on this fetch, after which its data can be dropped.</summary>
        public int LastTermIndex { get; set; } = -1;
    }
}
