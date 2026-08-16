using System.Text.Json.Serialization;

namespace XetSharp.Hub;

/// <summary>
/// A file to reference from a commit. Its contents must already be stored — over the Xet protocol,
/// that means its shard has been uploaded — because the commit only records a pointer to them.
/// </summary>
/// <param name="Path">Where the file goes in the repository.</param>
/// <param name="Sha256">SHA-256 of the file's contents, lowercase hex.</param>
/// <param name="Size">The file's length in bytes.</param>
public sealed record XetCommitFile(string Path, string Sha256, long Size);

/// <summary>What to commit, and what to say about it.</summary>
public sealed record XetCommitRequest
{
    /// <summary>The commit message's first line.</summary>
    public required string Summary { get; init; }

    /// <summary>The rest of the commit message.</summary>
    public string? Description { get; init; }

    /// <summary>The files to add or replace, as LFS pointers.</summary>
    public IReadOnlyList<XetCommitFile> Files { get; init; } = [];

    /// <summary>Paths to delete in the same commit.</summary>
    public IReadOnlyList<string> DeletedFiles { get; init; } = [];

    /// <summary>
    /// The commit this one must apply on top of. When set, the Hub rejects the commit if the branch
    /// has moved on — the way to avoid clobbering a concurrent change.
    /// </summary>
    public string? ParentCommit { get; init; }

    /// <summary>Whether to open a pull request instead of committing to the branch directly.</summary>
    public bool CreatePullRequest { get; init; }
}

/// <summary>What the Hub made of a commit.</summary>
/// <param name="CommitOid">The new commit's object ID, when the Hub reported one.</param>
/// <param name="CommitUrl">A link to the commit.</param>
/// <param name="PullRequestUrl">A link to the pull request, when one was asked for.</param>
public sealed record XetCommit(string? CommitOid, Uri? CommitUrl, Uri? PullRequestUrl);

/// <summary>The Hub's commit response.</summary>
internal sealed class CommitResponseJson
{
    [JsonPropertyName("commitOid")]
    public string? CommitOid { get; set; }

    [JsonPropertyName("commitUrl")]
    public string? CommitUrl { get; set; }

    [JsonPropertyName("pullRequestUrl")]
    public string? PullRequestUrl { get; set; }
}
