namespace XetSharp.Server.Storage;

/// <summary>
/// The Hub half of what a server stores: which files a repository holds at each revision. Nothing
/// in the Xet protocol needs this — the CAS API deals in file IDs — but a client cannot reach the
/// CAS API without a Hub to resolve paths and mint tokens, so a usable server has to play one.
/// </summary>
public interface IRepositoryStore
{
    /// <summary>
    /// A repository's contents at <paramref name="revision"/>, which may be a branch name or a
    /// commit ID, or null when the repository or revision does not exist.
    /// </summary>
    ValueTask<RepositoryRevision?> GetRevisionAsync(RepositoryId repository, string revision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a commit to a branch, creating the repository and the branch when they do not yet
    /// exist, and returns the new commit ID. When the commit names a parent and the branch exists
    /// at some other commit, nothing changes and <see cref="BranchMovedException"/> is thrown. The
    /// check and the advance are one atomic step, so two commits from the same parent cannot both
    /// land.
    /// </summary>
    ValueTask<string> CommitAsync(RepositoryId repository, string branch, RepositoryCommit commit, CancellationToken cancellationToken = default);
}

/// <summary>A commit named a parent that is no longer the branch's head: another commit landed first.</summary>
public sealed class BranchMovedException(string branch, string parent, string? head)
    : Exception($"The branch '{branch}' has moved on from {parent}{(head is null ? string.Empty : $" to {head}")}.")
{
    public string Branch { get; } = branch;

    /// <summary>The parent the commit named.</summary>
    public string Parent { get; } = parent;

    /// <summary>Where the branch was found to be instead, when known.</summary>
    public string? Head { get; } = head;
}

/// <summary>A repository on the Hub facade: <c>models/openai-community/gpt2</c> and the like.</summary>
public readonly record struct RepositoryId(string Type, string Namespace, string Name)
{
    /// <summary>The <c>namespace/name</c> pair the Hub calls a repo ID.</summary>
    public string Id => $"{Namespace}/{Name}";

    /// <summary>The plural path segment used under <c>/api/</c>.</summary>
    public string ApiSegment => Type + "s";

    public override string ToString() => $"{ApiSegment}/{Id}";
}

/// <summary>A file in a repository revision, pointing at the Xet file that holds its bytes.</summary>
public sealed record RepositoryFile(string Path, MerkleHash FileId, long Size, string Sha256);

/// <summary>A repository's contents at one commit.</summary>
public sealed record RepositoryRevision(string CommitId, IReadOnlyDictionary<string, RepositoryFile> Files);

/// <summary>What a Hub commit changes: files added or replaced, and files removed.</summary>
public sealed record RepositoryCommit(
    string Summary,
    string? Description,
    IReadOnlyList<RepositoryFile> Added,
    IReadOnlyList<string> Deleted,
    string? ParentCommit)
{
    /// <summary>The files a revision holds once this commit is applied to <paramref name="parent"/>.</summary>
    public Dictionary<string, RepositoryFile> ApplyTo(RepositoryRevision? parent)
    {
        var files = parent is null ? [] : new Dictionary<string, RepositoryFile>(parent.Files);
        foreach (var path in Deleted)
        {
            files.Remove(path);
        }

        foreach (var file in Added)
        {
            files[file.Path] = file;
        }

        return files;
    }
}
