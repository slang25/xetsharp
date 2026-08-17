namespace XetSharp.Download;

/// <summary>Knobs for the download pipeline. The defaults suit a single large file on a fast link.</summary>
public sealed record XetDownloadOptions
{
    public static XetDownloadOptions Default { get; } = new();

    /// <summary>
    /// How many xorb range requests may be in flight at once.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxBufferedBytes"/> is what binds first whenever the ranges are large: a file
    /// stored as full xorbs is fetched in pieces of roughly 56 MB, and the default budget holds two
    /// of those, not eight. The full eight is reached by files whose terms are short — a
    /// deduplicated file, or a range request that lands inside one — which are also the downloads
    /// where the round trip per request, rather than the bytes, is the thing worth hiding.
    /// </remarks>
    public int MaxConcurrentDownloads { get; init; } = 8;

    /// <summary>
    /// Roughly how much downloaded xorb data may sit in memory ahead of the writer. Data is held
    /// compressed and decompressed chunk by chunk as it is written, so this bounds the transfer
    /// size rather than the file size. Always at least one range: a single range is never split.
    /// </summary>
    /// <remarks>
    /// Raising this is what makes a larger <see cref="MaxConcurrentDownloads"/> take effect on a
    /// file of full-sized xorbs, at about 56 MB of memory per extra request in flight.
    /// </remarks>
    public long MaxBufferedBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>
    /// Whether to re-derive the file hash from the downloaded chunks and check it against the file
    /// ID. Only possible for whole-file downloads, where every chunk passes through.
    /// </summary>
    public bool VerifyFileHash { get; init; } = true;

    /// <summary>
    /// Whether to check the downloaded bytes against the SHA-256 the Hub records for the file, when
    /// the Hub supplied one and the whole file is being downloaded.
    /// </summary>
    public bool VerifySha256 { get; init; } = true;
}
