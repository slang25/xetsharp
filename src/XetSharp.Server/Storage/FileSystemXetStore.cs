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

    public string Root => _root;

    public async ValueTask<StoredXorb?> GetXorbAsync(MerkleHash hash, CancellationToken cancellationToken = default)
    {
        var path = IndexPath(hash);
        if (!File.Exists(path))
        {
            return null;
        }

        return StoredXorbCodec.Decode(hash, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<bool> PutXorbAsync(StoredXorb xorb, ReadOnlyMemory<byte> serialized, CancellationToken cancellationToken = default)
    {
        var indexPath = IndexPath(xorb.Hash);
        if (File.Exists(indexPath))
        {
            return false;
        }

        // Bytes first, index last: the index is what says a xorb exists, so a crash between the
        // two leaves an orphaned blob rather than an index pointing at nothing.
        await WriteAtomicallyAsync(XorbPath(xorb.Hash), serialized, cancellationToken).ConfigureAwait(false);
        var inserted = await WriteAtomicallyAsync(indexPath, StoredXorbCodec.Encode(xorb), cancellationToken, overwrite: false).ConfigureAwait(false);

        for (var i = 0; i < xorb.Chunks.Length; i++)
        {
            var chunk = xorb.Chunks[i].Hash;
            if (GlobalDeduplication.IsEligible(chunk))
            {
                await WriteAtomicallyAsync(ChunkPath(chunk), Encoding.ASCII.GetBytes($"{xorb.Hash} {i}"), cancellationToken, overwrite: false).ConfigureAwait(false);
            }
        }

        return inserted;
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
            ? StoredFileCodec.Decode(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false))
            : null;
    }

    public async ValueTask<StoredFile?> GetFileBySha256Async(MerkleHash sha256, CancellationToken cancellationToken = default)
    {
        var path = Sha256Path(sha256);
        if (!File.Exists(path))
        {
            return null;
        }

        var fileId = MerkleHash.Parse((await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim());
        return await GetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> PutFileAsync(StoredFile file, CancellationToken cancellationToken = default)
    {
        var inserted = await WriteAtomicallyAsync(FilePath(file.FileId), StoredFileCodec.Encode(file), cancellationToken, overwrite: false).ConfigureAwait(false);
        if (file.Sha256 is { } sha256)
        {
            await WriteAtomicallyAsync(Sha256Path(sha256), Encoding.ASCII.GetBytes(file.FileId.ToString()), cancellationToken).ConfigureAwait(false);
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

        var fields = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Split(' ');
        return new ChunkLocation(MerkleHash.Parse(fields[0]), int.Parse(fields[1]));
    }

    public async ValueTask<RepositoryRevision?> GetRevisionAsync(RepositoryId repository, string revision, CancellationToken cancellationToken = default)
    {
        var commitId = revision;
        var branchPath = BranchPath(repository, revision);
        if (File.Exists(branchPath))
        {
            commitId = (await File.ReadAllTextAsync(branchPath, cancellationToken).ConfigureAwait(false)).Trim();
        }

        var commitPath = CommitPath(repository, commitId);
        return File.Exists(commitPath)
            ? RepositoryRevisionCodec.Decode(await File.ReadAllBytesAsync(commitPath, cancellationToken).ConfigureAwait(false))
            : null;
    }

    public async ValueTask<string> CommitAsync(RepositoryId repository, string branch, RepositoryCommit commit, CancellationToken cancellationToken = default)
    {
        var parent = await GetRevisionAsync(repository, branch, cancellationToken).ConfigureAwait(false);
        var files = parent is null ? [] : new Dictionary<string, RepositoryFile>(parent.Files);
        foreach (var path in commit.Deleted)
        {
            files.Remove(path);
        }

        foreach (var file in commit.Added)
        {
            files[file.Path] = file;
        }

        var commitId = InMemoryXetStore.NewCommitId();
        await WriteAtomicallyAsync(CommitPath(repository, commitId), RepositoryRevisionCodec.Encode(new RepositoryRevision(commitId, files)), cancellationToken)
            .ConfigureAwait(false);
        await WriteAtomicallyAsync(BranchPath(repository, branch), Encoding.ASCII.GetBytes(commitId), cancellationToken).ConfigureAwait(false);
        return commitId;
    }

    private string XorbPath(MerkleHash hash) => Spread("xorbs", hash);

    private string IndexPath(MerkleHash hash) => Spread("xorbs", hash) + ".index";

    private string FilePath(MerkleHash fileId) => Spread("files", fileId);

    private string Sha256Path(MerkleHash sha256) => Spread("sha256", sha256);

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
                File.Move(temporary, path, overwrite: true);
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
}
