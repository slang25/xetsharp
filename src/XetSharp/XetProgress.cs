namespace XetSharp;

/// <summary>
/// How far a transfer has got. Reported to the <see cref="IProgress{T}"/> a download or upload was
/// given, roughly once per megabyte and once more when the transfer finishes.
/// </summary>
/// <param name="BytesTransferred">
/// Bytes written to the destination so far (downloading), or read from the source and placed
/// (uploading). Deduplicated bytes count as transferred: they are part of the file's progress even
/// though they never travel.
/// </param>
/// <param name="TotalBytes">
/// The transfer's total, when it is known in advance. Null for a stream whose length cannot be
/// asked for.
/// </param>
public readonly record struct XetProgress(long BytesTransferred, long? TotalBytes)
{
    /// <summary>How much of the transfer is done, from 0 to 1, or null when the total is unknown.</summary>
    public double? Fraction => TotalBytes is { } total && total > 0
        ? Math.Clamp((double)BytesTransferred / total, 0, 1)
        : null;
}

/// <summary>
/// Feeds an <see cref="IProgress{T}"/> without letting a fast transfer flood it: a report every
/// <see cref="ReportInterval"/> bytes, plus one at the start and one at the end. Byte-driven rather
/// than clock-driven, so a test sees the same reports every run.
/// </summary>
internal sealed class ProgressReporter
{
    private const long ReportInterval = 1024 * 1024;

    private readonly IProgress<XetProgress>? _progress;
    private readonly long? _totalBytes;
    private long _transferred;
    private long _reported;

    public ProgressReporter(IProgress<XetProgress>? progress, long? totalBytes)
    {
        _progress = progress;
        _totalBytes = totalBytes;
        _progress?.Report(new XetProgress(0, totalBytes));
    }

    public void Advance(long bytes)
    {
        _transferred += bytes;
        if (_progress is not null && _transferred - _reported >= ReportInterval)
        {
            Report();
        }
    }

    /// <summary>Reports the final figure, so a consumer's last update matches what happened.</summary>
    public void Complete()
    {
        if (_progress is not null && _transferred != _reported)
        {
            Report();
        }
    }

    private void Report()
    {
        _reported = _transferred;
        _progress!.Report(new XetProgress(_transferred, _totalBytes));
    }
}
