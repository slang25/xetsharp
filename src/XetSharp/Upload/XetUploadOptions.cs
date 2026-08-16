namespace XetSharp.Upload;

/// <summary>How an upload should behave. Every value has a working default.</summary>
public sealed record XetUploadOptions
{
    public static XetUploadOptions Default { get; } = new();

    /// <summary>
    /// One chunk hash in this many is eligible for a global-deduplication query by default. It is
    /// the sample the reference implementation takes, and matching it is what makes two clients ask
    /// about the same chunks — so a hit is likely to be a long run rather than a lone chunk.
    /// </summary>
    public const ulong DefaultGlobalDeduplicationSampleRate = 1024;

    /// <summary>
    /// How many xorbs may be uploading at once. Each in-flight upload holds its serialized xorb — up
    /// to 64 MiB, usually much less — in memory until the service acknowledges it.
    /// </summary>
    public int MaxConcurrentUploads { get; init; } = 4;

    /// <summary>
    /// Whether to ask the CAS service about chunks it might already hold. Chunks this upload has
    /// seen are always deduplicated; this covers the ones only the service can know about, at the
    /// cost of a round trip per eligible chunk.
    /// </summary>
    public bool UseGlobalDeduplication { get; init; } = true;

    /// <summary>How much of a file to read at a time on the way into the chunker.</summary>
    public int ReadBufferSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// The sampling rate for global-deduplication queries. Internal because
    /// <see cref="DefaultGlobalDeduplicationSampleRate"/> is the value that keeps this client asking
    /// the same questions as the reference implementation; tests lower it so a query is certain
    /// rather than a one-in-a-thousand event.
    /// </summary>
    internal ulong GlobalDeduplicationSampleRate { get; init; } = DefaultGlobalDeduplicationSampleRate;

    /// <summary>
    /// The most raw chunk data to pack into one xorb. Internal for the same reason: the protocol's
    /// figure is the only one worth using in anger, and tests need multi-xorb uploads without
    /// generating 64 MiB of data for each one.
    /// </summary>
    internal int MaxXorbBytes { get; init; } = XorbBuilder.MaxUncompressedBytes;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentUploads, 1, nameof(MaxConcurrentUploads));
        ArgumentOutOfRangeException.ThrowIfLessThan(ReadBufferSize, 4096, nameof(ReadBufferSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(GlobalDeduplicationSampleRate, 1ul, nameof(GlobalDeduplicationSampleRate));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxXorbBytes, Xorbs.XorbChunkHeader.MaxUncompressedSize, nameof(MaxXorbBytes));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxXorbBytes, XorbBuilder.MaxUncompressedBytes, nameof(MaxXorbBytes));
    }
}
