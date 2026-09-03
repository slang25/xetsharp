using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

namespace XetSharp.Server.Storage.S3;

/// <summary>
/// Everything as objects in one bucket, laid out the way <see cref="FileSystemXetStore"/> lays
/// out a directory. Xorb bytes are handed to clients as presigned S3 URLs, one per byte range,
/// so a Lambda serving reconstructions never has a xorb's bytes pass through it.
/// </summary>
public sealed class S3XetStore(IAmazonS3 client, string bucket, string prefix = "") : IXetStore, IRepositoryStore
{
    /// <summary>How many times a commit that names no parent is rebuilt on a head that moved under it.</summary>
    private const int MaxCommitAttempts = 5;

    private const string RangeHeader = "Range";

    private readonly string _prefix = prefix.Length == 0 ? string.Empty : prefix.TrimEnd('/') + "/";

    public async ValueTask<StoredXorb?> GetXorbAsync(MerkleHash hash, CancellationToken cancellationToken = default)
    {
        var bytes = await GetAsync(IndexKey(hash), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : StoredXorbCodec.Decode(hash, bytes);
    }

    public async ValueTask<bool> PutXorbAsync(StoredXorb xorb, ReadOnlyMemory<byte> serialized, CancellationToken cancellationToken = default)
    {
        if (await ExistsAsync(IndexKey(xorb.Hash), cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Bytes and chunk entries first, index last: the index is what says a xorb exists, so a
        // failure before it leaves orphans a retry completes, rather than a xorb that looks whole
        // but is missing from the global-deduplication index for good.
        await PutAsync(XorbKey(xorb.Hash), serialized, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < xorb.Chunks.Length; i++)
        {
            var chunk = xorb.Chunks[i].Hash;
            if (GlobalDeduplication.IsEligible(chunk))
            {
                await PutAsync(ChunkKey(chunk), Encoding.ASCII.GetBytes($"{xorb.Hash} {i}"), cancellationToken).ConfigureAwait(false);
            }
        }

        await PutAsync(IndexKey(xorb.Hash), StoredXorbCodec.Encode(xorb), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask CopyXorbRangeAsync(MerkleHash hash, long start, long end, Stream destination, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetObjectAsync(
            new GetObjectRequest { BucketName = bucket, Key = XorbKey(hash), ByteRange = new ByteRange(start, end) },
            cancellationToken).ConfigureAwait(false);
        await response.ResponseStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Uri?> CreateDirectDownloadUrlAsync(MerkleHash hash, long start, long end, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = XorbKey(hash),
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            // The presigner assumes HTTPS whatever the client was configured with; a local
            // emulator on plain HTTP would otherwise get URLs nothing can fetch.
            Protocol = UsesPlainHttp(client.Config) ? Protocol.HTTP : Protocol.HTTPS,
        };

        // The Range header is part of what is signed, so the URL fetches these bytes and no
        // others: S3 refuses a request that omits the header or sends a different value. That is
        // the rule the spec sets for signed xorb URLs, and a xorb holds other files' chunks too.
        request.Headers[RangeHeader] = $"bytes={start}-{end}";
        return new Uri(await client.GetPreSignedURLAsync(request).ConfigureAwait(false));
    }

    public async ValueTask<StoredFile?> GetFileAsync(MerkleHash fileId, CancellationToken cancellationToken = default)
    {
        var bytes = await GetAsync(FileKey(fileId), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : StoredFileCodec.Decode(bytes);
    }

    public async ValueTask<StoredFile?> GetFileBySha256Async(RepositoryId repository, MerkleHash sha256, CancellationToken cancellationToken = default)
    {
        var bytes = await GetAsync(Sha256Key(repository, sha256), cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? null
            : await GetFileAsync(MerkleHash.Parse(Encoding.ASCII.GetString(bytes).Trim()), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> PutFileAsync(StoredFile file, RepositoryId repository, CancellationToken cancellationToken = default)
    {
        var existed = await ExistsAsync(FileKey(file.FileId), cancellationToken).ConfigureAwait(false);
        await PutAsync(FileKey(file.FileId), StoredFileCodec.Encode(file), cancellationToken).ConfigureAwait(false);
        if (file.Sha256 is { } sha256)
        {
            await PutAsync(Sha256Key(repository, sha256), Encoding.ASCII.GetBytes(file.FileId.ToString()), cancellationToken).ConfigureAwait(false);
        }

        return !existed;
    }

    public async ValueTask<ChunkLocation?> FindChunkAsync(MerkleHash chunkHash, CancellationToken cancellationToken = default)
    {
        var bytes = await GetAsync(ChunkKey(chunkHash), cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        var fields = Encoding.ASCII.GetString(bytes).Split(' ');
        return new ChunkLocation(MerkleHash.Parse(fields[0]), int.Parse(fields[1]));
    }

    public async ValueTask<RepositoryRevision?> GetRevisionAsync(RepositoryId repository, string revision, CancellationToken cancellationToken = default)
    {
        var (head, _) = await ReadBranchAsync(repository, revision, cancellationToken).ConfigureAwait(false);
        return await ReadCommitAsync(repository, head ?? revision, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> CommitAsync(RepositoryId repository, string branch, RepositoryCommit commit, CancellationToken cancellationToken = default)
    {
        // The branch pointer is advanced by a conditional put on the ETag it was read at, so two
        // commits from the same head cannot both land, whichever instances are serving them. A
        // commit that names its parent and loses the race is a conflict; one that does not is
        // rebuilt on the new head and tried again.
        for (var attempt = 1; ; attempt++)
        {
            var (head, etag) = await ReadBranchAsync(repository, branch, cancellationToken).ConfigureAwait(false);
            if (commit.ParentCommit is { } parent && head is not null && parent != head)
            {
                throw new BranchMovedException(branch, parent, head);
            }

            var parentRevision = head is null ? null : await ReadCommitAsync(repository, head, cancellationToken).ConfigureAwait(false);
            var commitId = InMemoryXetStore.NewCommitId();
            await PutAsync(
                CommitKey(repository, commitId),
                RepositoryRevisionCodec.Encode(new RepositoryRevision(commitId, commit.ApplyTo(parentRevision))),
                cancellationToken).ConfigureAwait(false);

            var advance = new PutObjectRequest { BucketName = bucket, Key = BranchKey(repository, branch), ContentBody = commitId };
            if (etag is null)
            {
                advance.IfNoneMatch = "*";
            }
            else
            {
                advance.IfMatch = etag;
            }

            try
            {
                await client.PutObjectAsync(advance, cancellationToken).ConfigureAwait(false);
                return commitId;
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            {
                if (commit.ParentCommit is { } stated)
                {
                    var (moved, _) = await ReadBranchAsync(repository, branch, cancellationToken).ConfigureAwait(false);
                    throw new BranchMovedException(branch, stated, moved);
                }

                if (attempt == MaxCommitAttempts)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>The commit a branch points at and the ETag of the pointer, or nulls when there is no such branch.</summary>
    private async Task<(string? CommitId, string? ETag)> ReadBranchAsync(RepositoryId repository, string branch, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetObjectAsync(bucket, BranchKey(repository, branch), cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(response.ResponseStream, Encoding.ASCII);
            return ((await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim(), response.ETag);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    private async Task<RepositoryRevision?> ReadCommitAsync(RepositoryId repository, string commitId, CancellationToken cancellationToken)
    {
        var bytes = await GetAsync(CommitKey(repository, commitId), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : RepositoryRevisionCodec.Decode(bytes);
    }

    private static bool UsesPlainHttp(Amazon.Runtime.IClientConfig config) =>
        config.UseHttp ||
        (Uri.TryCreate(config.ServiceURL, UriKind.Absolute, out var serviceUrl) && serviceUrl.Scheme == Uri.UriSchemeHttp);

    private string XorbKey(MerkleHash hash) => Spread("xorbs", hash);

    private string IndexKey(MerkleHash hash) => Spread("xorbs", hash) + ".index";

    private string FileKey(MerkleHash fileId) => Spread("files", fileId);

    /// <summary>Under the repository the file was uploaded to: the SHA-256 is the uploader's claim, so it is theirs alone.</summary>
    private string Sha256Key(RepositoryId repository, MerkleHash sha256)
    {
        var hex = sha256.ToString();
        return $"{RepositoryKey(repository)}/sha256/{hex[..2]}/{hex}";
    }

    private string ChunkKey(MerkleHash chunkHash) => Spread("chunks", chunkHash);

    private string Spread(string kind, MerkleHash hash)
    {
        var hex = hash.ToString();
        return $"{_prefix}{kind}/{hex[..2]}/{hex}";
    }

    private string BranchKey(RepositoryId repository, string branch) =>
        $"{RepositoryKey(repository)}/branches/{Uri.EscapeDataString(branch)}";

    private string CommitKey(RepositoryId repository, string commitId) =>
        $"{RepositoryKey(repository)}/commits/{Uri.EscapeDataString(commitId)}.json";

    private string RepositoryKey(RepositoryId repository) =>
        $"{_prefix}repos/{repository.ApiSegment}/{Uri.EscapeDataString(repository.Namespace)}/{Uri.EscapeDataString(repository.Name)}";

    private async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetObjectMetadataAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task PutAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        await client.PutObjectAsync(
            new PutObjectRequest { BucketName = bucket, Key = key, InputStream = stream, AutoCloseStream = false },
            cancellationToken).ConfigureAwait(false);
    }
}
