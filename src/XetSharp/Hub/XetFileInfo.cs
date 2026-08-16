namespace XetSharp.Hub;

/// <summary>
/// What the Hub tells us about a Xet-backed file when we resolve its path: the file ID the CAS API
/// is addressed by, plus the metadata worth checking a download against.
/// </summary>
/// <param name="FileId">The Xet file ID — the file's Merkle hash, straight from <c>X-Xet-Hash</c>.</param>
/// <param name="Size">The file's length in bytes, from <c>X-Linked-Size</c>.</param>
/// <param name="Sha256">
/// The SHA-256 the repository's LFS pointer records, from <c>X-Linked-Etag</c>, or null if absent.
/// </param>
/// <param name="CommitSha">The commit the revision resolved to, from <c>X-Repo-Commit</c>.</param>
public sealed record XetFileInfo(MerkleHash FileId, long Size, string? Sha256, string? CommitSha);
