using Microsoft.Extensions.Logging;
using XetSharp.Hub;

namespace XetSharp.Diagnostics;

/// <summary>
/// Every log message this library writes, in one place, through the source-generated
/// <see cref="LoggerMessageAttribute"/> so nothing is formatted for a level that is switched off and
/// no reflection is involved (which keeps Native AOT intact).
/// </summary>
/// <remarks>
/// Levels are chosen for someone running a transfer, not for someone debugging this library:
/// Information marks the few events that describe what happened (a file downloaded, an upload
/// registered), Warning marks something that went wrong but was recovered from, and everything
/// per-request or per-chunk sits at Debug or Trace.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Debug, Message = "Minting a {Scope} token for {Repository}.")]
    public static partial void MintingToken(this ILogger logger, XetTokenScope scope, XetRepository repository);

    [LoggerMessage(
        EventId = 101,
        Level = LogLevel.Debug,
        Message = "Minted a {Scope} token for {Repository} against {CasUrl}, expiring at {Expiry:O}.")]
    public static partial void MintedToken(this ILogger logger, XetTokenScope scope, XetRepository repository, Uri casUrl, DateTimeOffset expiry);

    [LoggerMessage(
        EventId = 102,
        Level = LogLevel.Debug,
        Message = "The CAS service rejected the current {Scope} token for {Repository}; minting a replacement.")]
    public static partial void TokenRejected(this ILogger logger, XetTokenScope scope, XetRepository repository);

    [LoggerMessage(EventId = 200, Level = LogLevel.Debug, Message = "Requesting the {Version} reconstruction of {FileId}{Range}.")]
    public static partial void RequestingReconstruction(this ILogger logger, string version, MerkleHash fileId, string range);

    [LoggerMessage(
        EventId = 201,
        Level = LogLevel.Debug,
        Message = "{Origin} does not serve /v2/{Endpoint}; using /v1 for this deployment from now on.")]
    public static partial void FallingBackToV1(this ILogger logger, string origin, string endpoint);

    [LoggerMessage(
        EventId = 202,
        Level = LogLevel.Debug,
        Message = "Uploaded xorb {XorbHash} ({Bytes} bytes); the service {Outcome} it.")]
    public static partial void UploadedXorb(this ILogger logger, MerkleHash xorbHash, int bytes, string outcome);

    [LoggerMessage(EventId = 203, Level = LogLevel.Debug, Message = "Uploaded a {Bytes}-byte shard; the service {Outcome} it.")]
    public static partial void UploadedShard(this ILogger logger, int bytes, string outcome);

    [LoggerMessage(EventId = 204, Level = LogLevel.Trace, Message = "The global index {Outcome} chunk {ChunkHash}.")]
    public static partial void DeduplicationQueried(this ILogger logger, string outcome, MerkleHash chunkHash);

    [LoggerMessage(
        EventId = 300,
        Level = LogLevel.Debug,
        Message = "Reconstructing {Bytes} bytes from {TermCount} term(s) across {XorbCount} xorb(s).")]
    public static partial void ReconstructionPlanned(this ILogger logger, long bytes, int termCount, int xorbCount);

    [LoggerMessage(EventId = 301, Level = LogLevel.Trace, Message = "Fetched {Bytes} bytes of xorb data in {RangeCount} range(s) from {Host}.")]
    public static partial void FetchedXorbData(this ILogger logger, long bytes, int rangeCount, string host);

    [LoggerMessage(EventId = 302, Level = LogLevel.Information, Message = "Downloaded {Bytes} bytes of {FileId}.")]
    public static partial void Downloaded(this ILogger logger, long bytes, MerkleHash fileId);

    [LoggerMessage(
        EventId = 400,
        Level = LogLevel.Debug,
        Message = "Chunked '{Path}': {Bytes} bytes in {ChunkCount} chunk(s), {DeduplicatedBytes} of them already stored.")]
    public static partial void ChunkedFile(this ILogger logger, string path, long bytes, int chunkCount, long deduplicatedBytes);

    [LoggerMessage(EventId = 401, Level = LogLevel.Debug, Message = "Sealed xorb {XorbHash}: {ChunkCount} chunk(s), {Bytes} serialized bytes.")]
    public static partial void SealedXorb(this ILogger logger, MerkleHash xorbHash, int chunkCount, int bytes);

    [LoggerMessage(
        EventId = 402,
        Level = LogLevel.Information,
        Message = "Uploaded {FileCount} file(s) as {XorbCount} xorb(s) ({UploadedBytes} bytes sent, {DeduplicatedBytes} deduplicated).")]
    public static partial void Uploaded(this ILogger logger, int fileCount, int xorbCount, long uploadedBytes, long deduplicatedBytes);

    [LoggerMessage(EventId = 500, Level = LogLevel.Warning, Message = "Retrying {Method} {Uri} in {Delay} after {Reason} (attempt {Attempt} of {MaxAttempts}).")]
    public static partial void Retrying(this ILogger logger, HttpMethod method, Uri? uri, TimeSpan delay, string reason, int attempt, int maxAttempts);
}
