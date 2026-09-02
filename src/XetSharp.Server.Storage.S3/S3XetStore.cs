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

        await PutAsync(XorbKey(xorb.Hash), serialized, cancellationToken).ConfigureAwait(false);
        await PutAsync(IndexKey(xorb.Hash), StoredXorbCodec.Encode(xorb), cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < xorb.Chunks.Length; i++)
        {
            var chunk = xorb.Chunks[i].Hash;
            if (GlobalDeduplication.IsEligible(chunk))
            {
                await PutAsync(ChunkKey(chunk), Encoding.ASCII.GetBytes($"{xorb.Hash} {i}"), cancellationToken).ConfigureAwait(false);
            }
        }

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
        var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = XorbKey(hash),
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            // The presigner assumes HTTPS whatever the client was configured with; a local
            // emulator on plain HTTP would otherwise get URLs nothing can fetch.
            Protocol = UsesPlainHttp(client.Config) ? Protocol.HTTP : Protocol.HTTPS,
        }).ConfigureAwait(false);
        return new Uri(url);
    }

    public async ValueTask<StoredFile?> GetFileAsync(MerkleHash fileId, CancellationToken cancellationToken = default)
    {
        var bytes = await GetAsync(FileKey(fileId), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : StoredFileCodec.Decode(bytes);
    }

    public async ValueTask<StoredFile?> GetFileBySha256Async(MerkleHash sha256, CancellationToken cancellationToken = default)
    {
        var bytes = await GetAsync(Sha256Key(sha256), cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? null
            : await GetFileAsync(MerkleHash.Parse(Encoding.ASCII.GetString(bytes).Trim()), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> PutFileAsync(StoredFile file, CancellationToken cancellationToken = default)
    {
        var existed = await ExistsAsync(FileKey(file.FileId), cancellationToken).ConfigureAwait(false);
        await PutAsync(FileKey(file.FileId), StoredFileCodec.Encode(file), cancellationToken).ConfigureAwait(false);
        if (file.Sha256 is { } sha256)
        {
            await PutAsync(Sha256Key(sha256), Encoding.ASCII.GetBytes(file.FileId.ToString()), cancellationToken).ConfigureAwait(false);
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
        var commitId = revision;
        if (await GetAsync(BranchKey(repository, revision), cancellationToken).ConfigureAwait(false) is { } head)
        {
            commitId = Encoding.ASCII.GetString(head).Trim();
        }

        var bytes = await GetAsync(CommitKey(repository, commitId), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : RepositoryRevisionCodec.Decode(bytes);
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
        await PutAsync(CommitKey(repository, commitId), RepositoryRevisionCodec.Encode(new RepositoryRevision(commitId, files)), cancellationToken).ConfigureAwait(false);
        await PutAsync(BranchKey(repository, branch), Encoding.ASCII.GetBytes(commitId), cancellationToken).ConfigureAwait(false);
        return commitId;
    }

    private static bool UsesPlainHttp(Amazon.Runtime.IClientConfig config) =>
        config.UseHttp ||
        (Uri.TryCreate(config.ServiceURL, UriKind.Absolute, out var serviceUrl) && serviceUrl.Scheme == Uri.UriSchemeHttp);

    private string XorbKey(MerkleHash hash) => Spread("xorbs", hash);

    private string IndexKey(MerkleHash hash) => Spread("xorbs", hash) + ".index";

    private string FileKey(MerkleHash fileId) => Spread("files", fileId);

    private string Sha256Key(MerkleHash sha256) => Spread("sha256", sha256);

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
