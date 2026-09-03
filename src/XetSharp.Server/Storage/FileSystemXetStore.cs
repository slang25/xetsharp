using System.Collections.Concurrent;
using System.Text;

namespace XetSharp.Server.Storage;

/// <summary>
/// Everything under one directory, one file per object, so a local server keeps what it was
/// given across restarts and a person can look at it with <c>ls</c>. Xorbs and files live under
/// the first two hex characters of their hash, the way Git spreads objects across directories.
/// </summary>
public sealed class FileSystemXetStore(string root) : IXetStore, IRepositoryStore
{
    private readonly string _root = Path.GetFullPath(root);
    private readonly ConcurrentDictionary<RepositoryId, SemaphoreSlim> _commitGates = new();

    public string Root => _root;

    public async ValueTask<StoredXorb?> GetXorbAsync(MerkleHash hash, CancellationToken cancellationToken = default)
    {
        var path = IndexPath(hash);
        if (!File.Exists(path))
        {
            return null;
        }

        return StoredXorbCodec.Decode(hash, await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<bool> PutXorbAsync(StoredXorb xorb, ReadOnlyMemory<byte> serialized, CancellationToken cancellationToken = default)
    {
        var indexPath = IndexPath(xorb.Hash);
        if (File.Exists(indexPath))
        {
            return false;
        }

        // Bytes and chunk entries first, index last: the index is what says a xorb exists, so a
        // crash before it leaves orphans a retry completes, rather than a xorb that looks whole
        // but is missing from the global-deduplication index for good.
        await WriteAtomicallyAsync(XorbPath(xorb.Hash), serialized, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < xorb.Chunks.Length; i++)
        {
            var chunk = xorb.Chunks[i].Hash;
            if (GlobalDeduplication.IsEligible(chunk))
            {
                await WriteAtomicallyAsync(ChunkPath(chunk), Encoding.ASCII.GetBytes($"{xorb.Hash} {i}"), cancellationToken, overwrite: false).ConfigureAwait(false);
            }
        }

        return await WriteAtomicallyAsync(indexPath, StoredXorbCodec.Encode(xorb), cancellationToken, overwrite: false).ConfigureAwait(false);
    }

    public async ValueTask CopyXorbRangeAsync(MerkleHash hash, long start, long end, Stream destination, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(XorbPath(hash), FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            stream.Position = start;
            var remaining = end - start + 1;
            var buffer = new byte[(int)Math.Min(remaining, 256 * 1024)];
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Xorb {hash} is shorter than its index says.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
    }

    public ValueTask<Uri?> CreateDirectDownloadUrlAsync(MerkleHash hash, long start, long end, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Uri?>(null);

    public async ValueTask<StoredFile?> GetFileAsync(MerkleHash fileId, CancellationToken cancellationToken = default)
    {
        var path = FilePath(fileId);
        return File.Exists(path)
            ? StoredFileCodec.Decode(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false))
            : null;
    }

    public async ValueTask<StoredFile?> GetFileBySha256Async(RepositoryId repository, MerkleHash sha256, CancellationToken cancellationToken = default)
    {
        var path = Sha256Path(repository, sha256);
        if (!File.Exists(path))
        {
            return null;
        }

        var fileId = MerkleHash.Parse(await ReadTextSharedAsync(path, cancellationToken).ConfigureAwait(false));
        return await GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> PutFileAsync(StoredFile file, RepositoryId repository, CancellationToken cancellationToken = default)
    {
        var inserted = await WriteAtomicallyAsync(FilePath(file.FileId), StoredFileCodec.Encode(file), cancellationToken, overwrite: false).ConfigureAwait(false);
        if (file.Sha256 is { } sha256)
        {
            await WriteAtomicallyAsync(Sha256Path(repository, sha256), Encoding.ASCII.GetBytes(file.FileId.ToString()), cancellationToken).ConfigureAwait(false);
        }

        return inserted;
    }

    public async ValueTask<ChunkLocation?> FindChunkAsync(MerkleHash chunkHash, CancellationToken cancellationToken = default)
    {
        var path = ChunkPath(chunkHash);
        if (!File.Exists(path))
        {
            return null;
        }

        var fields = (await ReadTextSharedAsync(path, cancellationToken).ConfigureAwait(false)).Split(' ');
        return new ChunkLocation(MerkleHash.Parse(fields[0]), int.Parse(fields[1]));
    }

    public async ValueTask<RepositoryRevision?> GetRevisionAsync(RepositoryId repository, string revision, CancellationToken cancellationToken = default)
    {
        var commitId = await ReadBranchAsync(repository, revision, cancellationToken).ConfigureAwait(false) ?? revision;
        return await ReadCommitAsync(repository, commitId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> CommitAsync(RepositoryId repository, string branch, RepositoryCommit commit, CancellationToken cancellationToken = default)
    {
        // One commit at a time per repository, so the parent check and the branch advance cannot
        // interleave with another commit's. The gate is this process's, which is enough: a
        // directory store is one server's, not shared between instances.
        var gate = _commitGates.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var head = await ReadBranchAsync(repository, branch, cancellationToken).ConfigureAwait(false);
            if (commit.ParentCommit is { } parent && head is not null && parent != head)
            {
                throw new BranchMovedException(branch, parent, head);
            }

            var parentRevision = head is null ? null : await ReadCommitAsync(repository, head, cancellationToken).ConfigureAwait(false);
            var commitId = InMemoryXetStore.NewCommitId();
            await WriteAtomicallyAsync(
                CommitPath(repository, commitId),
                RepositoryRevisionCodec.Encode(new RepositoryRevision(commitId, commit.ApplyTo(parentRevision))),
                cancellationToken).ConfigureAwait(false);
            await WriteAtomicallyAsync(BranchPath(repository, branch), Encoding.ASCII.GetBytes(commitId), cancellationToken).ConfigureAwait(false);
            return commitId;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The commit a branch points at, or null when there is no such branch.</summary>
    private async Task<string?> ReadBranchAsync(RepositoryId repository, string branch, CancellationToken cancellationToken)
    {
        var branchPath = BranchPath(repository, branch);
        return File.Exists(branchPath)
            ? await ReadTextSharedAsync(branchPath, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task<RepositoryRevision?> ReadCommitAsync(RepositoryId repository, string commitId, CancellationToken cancellationToken)
    {
        var commitPath = CommitPath(repository, commitId);
        return File.Exists(commitPath)
            ? RepositoryRevisionCodec.Decode(await ReadSharedAsync(commitPath, cancellationToken).ConfigureAwait(false))
            : null;
    }

    private string XorbPath(MerkleHash hash) => Spread("xorbs", hash);

    private string IndexPath(MerkleHash hash) => Spread("xorbs", hash) + ".index";

    private string FilePath(MerkleHash fileId) => Spread("files", fileId);

    /// <summary>Under the repository the file was uploaded to: the SHA-256 is the uploader's claim, so it is theirs alone.</summary>
    private string Sha256Path(RepositoryId repository, MerkleHash sha256)
    {
        var hex = sha256.ToString();
        return Path.Combine(RepositoryPath(repository), "sha256", hex[..2], hex);
    }

    private string ChunkPath(MerkleHash chunkHash) => Spread("chunks", chunkHash);

    private string Spread(string kind, MerkleHash hash)
    {
        var hex = hash.ToString();
        return Path.Combine(_root, kind, hex[..2], hex);
    }

    private string BranchPath(RepositoryId repository, string branch) =>
        Path.Combine(RepositoryPath(repository), "branches", Uri.EscapeDataString(branch));

    private string CommitPath(RepositoryId repository, string commitId) =>
        Path.Combine(RepositoryPath(repository), "commits", Uri.EscapeDataString(commitId) + ".json");

    private string RepositoryPath(RepositoryId repository) =>
        Path.Combine(_root, "repos", repository.ApiSegment, Uri.EscapeDataString(repository.Namespace), Uri.EscapeDataString(repository.Name));

    /// <summary>
    /// Writes to a sibling temporary file and renames it into place, so a reader never sees half a
    /// file. Returns false when <paramref name="overwrite"/> is off and the file already existed.
    /// </summary>
    private static async Task<bool> WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            if (overwrite)
            {
                await ReplaceAsync(temporary, path, cancellationToken).ConfigureAwait(false);
                return true;
            }

            try
            {
                File.Move(temporary, path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// Renames over an existing file. Windows refuses to replace a file another handle has open
    /// unless that handle shares deletion — which every read here does, see
    /// <see cref="ReadSharedAsync"/> — and can still say no for a moment while a handle is on its
    /// way out, so a refusal is retried briefly before it is an error.
    /// </summary>
    private static async Task ReplaceAsync(string temporary, string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < ReplaceAttempts)
            {
                await Task.Delay(ReplaceRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private const int ReplaceAttempts = 10;

    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Reads a whole file, sharing it for writing and deletion, so that a pointer being replaced
    /// under a reader — a branch advancing, a SHA-256 recorded again — lets the rename through on
    /// Windows, which otherwise refuses to replace a file anyone holds open. The reader keeps the
    /// bytes it opened, as it does everywhere else.
    /// </summary>
    private static async Task<byte[]> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
    }

    private static async Task<string> ReadTextSharedAsync(string path, CancellationToken cancellationToken) =>
        Encoding.ASCII.GetString(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
}
