using Microsoft.Extensions.Logging;
using XetSharp.Server.Storage;

namespace XetSharp.Server.Diagnostics;

/// <summary>
/// Every message the server logs, through the source-generated <see cref="LoggerMessageAttribute"/>
/// so nothing is formatted for a level that is off and nothing needs reflection.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Stored xorb {XorbHash}: {Bytes} bytes, {ChunkCount} chunks.")]
    public static partial void XorbStored(this ILogger logger, MerkleHash xorbHash, long bytes, int chunkCount);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Xorb {XorbHash} was already stored.")]
    public static partial void XorbAlreadyStored(this ILogger logger, MerkleHash xorbHash);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Registered a shard naming {FileCount} file(s), {NewFileCount} of them new.")]
    public static partial void ShardRegistered(this ILogger logger, int fileCount, int newFileCount);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug, Message = "Reconstruction of {FileId}{Range}: {TermCount} term(s) over {XorbCount} xorb(s).")]
    public static partial void ReconstructionServed(this ILogger logger, MerkleHash fileId, string range, int termCount, int xorbCount);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Debug, Message = "Global-deduplication query for chunk {ChunkHash}: {Outcome}.")]
    public static partial void DeduplicationQueried(this ILogger logger, MerkleHash chunkHash, string outcome);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Debug, Message = "Served bytes {RangeSpec} of xorb {XorbHash}.")]
    public static partial void XorbRangeServed(this ILogger logger, MerkleHash xorbHash, string rangeSpec);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Debug, Message = "Minted a {Scope} token for {Repository} at {Revision}.")]
    public static partial void TokenMinted(this ILogger logger, string scope, RepositoryId repository, string revision);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "Committed {Commit} to {Repository} {Branch}: {Added} added, {Deleted} deleted.")]
    public static partial void Committed(this ILogger logger, string commit, RepositoryId repository, string branch, int added, int deleted);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Debug, Message = "Resolved {Path} in {Repository} at {Revision} to {FileId}.")]
    public static partial void Resolved(this ILogger logger, string path, RepositoryId repository, string revision, MerkleHash fileId);

    [LoggerMessage(EventId = 1200, Level = LogLevel.Warning, Message = "Refused {Method} {Path} with {StatusCode}: {Reason}")]
    public static partial void RequestRefused(this ILogger logger, string method, string path, int statusCode, string reason);
}
