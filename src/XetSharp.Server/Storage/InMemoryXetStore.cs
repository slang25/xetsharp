using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace XetSharp.Server.Storage;

/// <summary>
/// Everything in dictionaries. The store a test or a quick local run wants: nothing to configure,
/// nothing left behind, and every byte gone when the process is.
/// </summary>
public sealed class InMemoryXetStore : IXetStore, IRepositoryStore
{
    private readonly ConcurrentDictionary<MerkleHash, (StoredXorb Index, byte[] Bytes)> _xorbs = new();
    private readonly ConcurrentDictionary<MerkleHash, StoredFile> _files = new();
    private readonly ConcurrentDictionary<(RepositoryId Repository, MerkleHash Sha256), MerkleHash> _filesBySha256 = new();
    private readonly ConcurrentDictionary<MerkleHash, ChunkLocation> _chunks = new();
    private readonly ConcurrentDictionary<RepositoryId, Repository> _repositories = new();

    /// <summary>How many xorbs are stored.</summary>
    public int XorbCount => _xorbs.Count;

    /// <summary>How many files are registered.</summary>
    public int FileCount => _files.Count;

    public ValueTask<StoredXorb?> GetXorbAsync(MerkleHash hash, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_xorbs.TryGetValue(hash, out var entry) ? entry.Index : null);

    public ValueTask<bool> PutXorbAsync(StoredXorb xorb, ReadOnlyMemory<byte> serialized, CancellationToken cancellationToken = default)
    {
        if (!_xorbs.TryAdd(xorb.Hash, (xorb, serialized.ToArray())))
        {
            return ValueTask.FromResult(false);
        }

        for (var i = 0; i < xorb.Chunks.Length; i++)
        {
            if (GlobalDeduplication.IsEligible(xorb.Chunks[i].Hash))
            {
                _chunks.TryAdd(xorb.Chunks[i].Hash, new ChunkLocation(xorb.Hash, i));
            }
        }

        return ValueTask.FromResult(true);
    }

    public async ValueTask CopyXorbRangeAsync(MerkleHash hash, long start, long end, Stream destination, CancellationToken cancellationToken = default)
    {
        if (!_xorbs.TryGetValue(hash, out var entry))
        {
            throw new KeyNotFoundException($"Xorb {hash} is not stored.");
        }

        await destination.WriteAsync(entry.Bytes.AsMemory((int)start, (int)(end - start + 1)), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Uri?> CreateDirectDownloadUrlAsync(MerkleHash hash, long start, long end, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Uri?>(null);

    public ValueTask<StoredFile?> GetFileAsync(MerkleHash fileId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<StoredFile?>(_files.TryGetValue(fileId, out var file) ? file : null);

    public ValueTask<StoredFile?> GetFileBySha256Async(RepositoryId repository, MerkleHash sha256, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<StoredFile?>(_filesBySha256.TryGetValue((repository, sha256), out var fileId) && _files.TryGetValue(fileId, out var file) ? file : null);

    public ValueTask<bool> PutFileAsync(StoredFile file, RepositoryId repository, CancellationToken cancellationToken = default)
    {
        var inserted = _files.TryAdd(file.FileId, file);
        if (file.Sha256 is { } sha256)
        {
            _filesBySha256[(repository, sha256)] = file.FileId;
        }

        return ValueTask.FromResult(inserted);
    }

    public ValueTask<ChunkLocation?> FindChunkAsync(MerkleHash chunkHash, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ChunkLocation?>(_chunks.TryGetValue(chunkHash, out var location) ? location : null);

    public ValueTask<RepositoryRevision?> GetRevisionAsync(RepositoryId repository, string revision, CancellationToken cancellationToken = default)
    {
        if (!_repositories.TryGetValue(repository, out var repo))
        {
            return ValueTask.FromResult<RepositoryRevision?>(null);
        }

        lock (repo)
        {
            var commitId = repo.Branches.GetValueOrDefault(revision) ?? revision;
            return ValueTask.FromResult(repo.Commits.GetValueOrDefault(commitId));
        }
    }

    public ValueTask<string> CommitAsync(RepositoryId repository, string branch, RepositoryCommit commit, CancellationToken cancellationToken = default)
    {
        var repo = _repositories.GetOrAdd(repository, _ => new Repository());
        lock (repo)
        {
            var head = repo.Branches.GetValueOrDefault(branch);
            if (commit.ParentCommit is { } parent && head is not null && parent != head)
            {
                throw new BranchMovedException(branch, parent, head);
            }

            var commitId = NewCommitId();
            repo.Commits[commitId] = new RepositoryRevision(commitId, commit.ApplyTo(head is null ? null : repo.Commits.GetValueOrDefault(head)));
            repo.Branches[branch] = commitId;
            return ValueTask.FromResult(commitId);
        }
    }

    /// <summary>A 40-hex-character ID, shaped like the Git commit hash the Hub would report.</summary>
    internal static string NewCommitId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    private sealed class Repository
    {
        public Dictionary<string, string> Branches { get; } = [];

        public Dictionary<string, RepositoryRevision> Commits { get; } = [];
    }
}
