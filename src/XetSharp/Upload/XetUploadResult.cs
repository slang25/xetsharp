using XetSharp.Hub;

namespace XetSharp.Upload;

/// <summary>What one file turned into once it was chunked, packed and registered.</summary>
/// <param name="Path">The path given for it in the repository.</param>
/// <param name="FileId">
/// The file hash, which is the ID the reconstruction API answers to and the value the Hub reports
/// as <c>X-Xet-Hash</c>.
/// </param>
/// <param name="Size">The file's length in bytes.</param>
/// <param name="Sha256">
/// SHA-256 of the file's contents, lowercase hex. A Git-backed Hub repository needs this to write
/// the LFS pointer that makes the file visible.
/// </param>
/// <param name="ChunkCount">How many chunks the file was split into.</param>
/// <param name="DeduplicatedBytes">
/// How many of the file's bytes were already stored — in this upload's other xorbs or in the CAS
/// service — and so were not uploaded again.
/// </param>
public sealed record XetUploadedFile(
    string Path,
    MerkleHash FileId,
    long Size,
    string Sha256,
    int ChunkCount,
    long DeduplicatedBytes);

/// <summary>What an upload transferred and registered.</summary>
/// <param name="Files">One entry per uploaded file, in the order they were given.</param>
/// <param name="XorbCount">How many new xorbs the upload created.</param>
/// <param name="UploadedBytes">Total serialized length of those xorbs — the bytes that went over the wire.</param>
/// <param name="DeduplicatedBytes">File bytes that did not need uploading because the data already existed.</param>
/// <param name="GlobalDeduplicationQueries">How many chunks were looked up against the CAS service's global index.</param>
/// <param name="Commit">The Hub commit that published the files, when the upload was asked to make one.</param>
public sealed record XetUploadResult(
    IReadOnlyList<XetUploadedFile> Files,
    int XorbCount,
    long UploadedBytes,
    long DeduplicatedBytes,
    int GlobalDeduplicationQueries)
{
    public XetCommit? Commit { get; init; }
}
