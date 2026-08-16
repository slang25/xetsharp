using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using XetSharp.Cas;
using XetSharp.Chunking;
using XetSharp.Hashing;
using XetSharp.Hub;
using XetSharp.Shards;

namespace XetSharp.Upload;

/// <summary>
/// Runs one upload: chunk each file, deduplicate what already exists, pack the rest into xorbs and
/// upload them, then register the lot with a single shard. A session is used once.
/// </summary>
/// <remarks>
/// The shard is sent only after every xorb it names is stored — the one ordering rule the protocol
/// enforces, and the reason xorb uploads are awaited before the shard rather than alongside it.
/// </remarks>
internal sealed class UploadSession
{
    private readonly CasClient _casClient;
    private readonly XetRepository _repository;
    private readonly XetUploadOptions _options;
    private readonly UploadDeduplicator _deduplicator;
    private readonly XorbBuilder _builder;
    private readonly SemaphoreSlim _uploadSlots;

    /// <summary>Xorb uploads not yet known to have succeeded. Completed ones are dropped as they go.</summary>
    private readonly List<Task> _uploads = [];

    /// <summary>The chunk listings the shard's CAS-info section will carry, one per new xorb.</summary>
    private readonly List<ShardCasInfo> _newXorbs = [];

    /// <summary>
    /// Terms pointing at the xorb currently being packed. Their xorb hash is unknowable until it is
    /// sealed, so they are filled in then.
    /// </summary>
    private readonly List<PendingTerm> _termsInCurrentXorb = [];

    private long _uploadedBytes;

    public UploadSession(CasClient casClient, XetRepository repository, XetUploadOptions options, TimeProvider timeProvider)
    {
        _casClient = casClient;
        _repository = repository;
        _options = options;
        _deduplicator = new UploadDeduplicator(casClient, repository, options, timeProvider);
        _builder = new XorbBuilder(options.MaxXorbBytes);
        _uploadSlots = new SemaphoreSlim(options.MaxConcurrentUploads);
    }

    public async Task<XetUploadResult> RunAsync(IReadOnlyList<XetUploadFile> files, CancellationToken cancellationToken)
    {
        var builds = new List<FileBuild>(files.Count);
        try
        {
            foreach (var file in files)
            {
                builds.Add(await ChunkAsync(file, cancellationToken).ConfigureAwait(false));
            }

            await SealCurrentXorbAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(_uploads).ConfigureAwait(false);

            var shard = BuildShard(builds).ToByteArray();
            if (shard.Length > MdbShard.MaxUploadSize)
            {
                throw new XetException(
                    $"These {files.Count} file(s) describe a {shard.Length}-byte shard, over the {MdbShard.MaxUploadSize}-byte limit " +
                    "the CAS service accepts. Upload them in smaller batches.");
            }

            await _casClient.UploadShardAsync(_repository, shard, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DrainAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _builder.Dispose();
            _uploadSlots.Dispose();
        }

        return new XetUploadResult(
            [.. builds.Select(build => new XetUploadedFile(
                build.Source.Path,
                build.FileId,
                build.Size,
                build.Sha256,
                build.Chunks.Count,
                build.DeduplicatedBytes))],
            _newXorbs.Count,
            _uploadedBytes,
            builds.Sum(build => build.DeduplicatedBytes),
            _deduplicator.QueryCount);
    }

    /// <summary>
    /// Reads a file once, splitting it into chunks and placing each one, while hashing the whole
    /// thing for the SHA-256 a Git-backed repository's LFS pointer needs.
    /// </summary>
    private async Task<FileBuild> ChunkAsync(XetUploadFile file, CancellationToken cancellationToken)
    {
        var build = new FileBuild(file);
        var (content, leaveOpen) = await file.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunker = new Chunker();
            var chunks = new List<byte[]>();
            var buffer = ArrayPool<byte>.Shared.Rent(_options.ReadBufferSize);
            try
            {
                while (true)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(0, _options.ReadBufferSize), cancellationToken)
                        .ConfigureAwait(false);
                    var isFinal = read == 0;

                    sha256.AppendData(buffer.AsSpan(0, read));
                    build.Size += read;

                    chunks.Clear();
                    chunker.NextBlock(buffer.AsSpan(0, read), isFinal, chunks);
                    foreach (var chunk in chunks)
                    {
                        await PlaceAsync(build, chunk, cancellationToken).ConfigureAwait(false);
                    }

                    if (isFinal)
                    {
                        break;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            build.Sha256 = Convert.ToHexStringLower(sha256.GetCurrentHash());
        }
        finally
        {
            if (!leaveOpen)
            {
                await content.DisposeAsync().ConfigureAwait(false);
            }
        }

        // An empty file has no chunks, so no reconstruction and no verification entries — and a
        // shard whose files disagree about carrying those is invalid. The Hub stores files this
        // small as ordinary Git blobs anyway.
        if (build.Chunks.Count == 0)
        {
            throw new XetException($"'{file.Path}' is empty; there is nothing for the Xet protocol to store.");
        }

        build.FileId = XetHashes.FileHash(CollectionsMarshal.AsSpan(build.Chunks));
        return build;
    }

    /// <summary>
    /// Decides where one chunk lives — somewhere it already exists, or the xorb being packed — and
    /// folds it into the file's reconstruction.
    /// </summary>
    private async ValueTask PlaceAsync(FileBuild build, byte[] chunk, CancellationToken cancellationToken)
    {
        var chunkHash = XetHashes.ChunkHash(chunk);
        build.Chunks.Add((chunkHash, (ulong)chunk.Length));

        var placement = await _deduplicator.FindAsync(chunkHash, cancellationToken).ConfigureAwait(false);
        if (placement is { } existing)
        {
            build.DeduplicatedBytes += chunk.Length;
            Extend(build, existing, chunk.Length);
            return;
        }

        if (!_builder.CanAdd(chunk.Length))
        {
            await SealCurrentXorbAsync(cancellationToken).ConfigureAwait(false);
        }

        var index = _builder.Add(chunk, chunkHash);
        _deduplicator.RecordPending(chunkHash, index);
        Extend(build, new ChunkPlacement(null, index), chunk.Length);
    }

    /// <summary>
    /// Adds a chunk to the file's last term when it continues it — same xorb, next index — and
    /// starts a new term otherwise. Long terms are the goal: a reconstruction is fetched a term at
    /// a time, so fragmenting one costs every later download.
    /// </summary>
    private void Extend(FileBuild build, ChunkPlacement placement, int length)
    {
        if (build.Terms.Count > 0 && build.Terms[^1] is { } open && open.Xorb == placement.Xorb && open.End == placement.Index)
        {
            open.End++;
            open.UnpackedLength += length;
            return;
        }

        var term = new PendingTerm
        {
            Xorb = placement.Xorb,
            Start = placement.Index,
            End = placement.Index + 1,
            UnpackedLength = length,
            FirstChunk = build.Chunks.Count - 1,
        };

        build.Terms.Add(term);
        if (placement.Xorb is null)
        {
            _termsInCurrentXorb.Add(term);
        }
    }

    /// <summary>
    /// Seals the xorb being packed, hands its bytes to an upload, and settles everything that was
    /// waiting to learn its hash. Waits for a free upload slot first, so the number of serialized
    /// xorbs held in memory stays bounded by <see cref="XetUploadOptions.MaxConcurrentUploads"/>.
    /// </summary>
    private async Task SealCurrentXorbAsync(CancellationToken cancellationToken)
    {
        if (_builder.IsEmpty)
        {
            return;
        }

        var packed = _builder.Build();
        foreach (var term in _termsInCurrentXorb)
        {
            term.Xorb = packed.Hash;
        }

        _termsInCurrentXorb.Clear();
        _deduplicator.Seal(packed.Hash);
        _newXorbs.Add(packed.Info);
        _uploadedBytes += packed.Serialized.Length;

        await _uploadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        _uploads.RemoveAll(upload => upload.IsCompletedSuccessfully);
        _uploads.Add(UploadXorbAsync(packed, cancellationToken));
    }

    private async Task UploadXorbAsync(PackedXorb packed, CancellationToken cancellationToken)
    {
        try
        {
            await _casClient.UploadXorbAsync(_repository, packed.Hash, packed.Serialized, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _uploadSlots.Release();
        }
    }

    /// <summary>
    /// Awaits the uploads still running after something else has already gone wrong, so that one of
    /// them failing too does not surface later as an unobserved exception. The failure on its way
    /// out is the one worth reporting.
    /// </summary>
    private async Task DrainAsync()
    {
        try
        {
            await Task.WhenAll(_uploads).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberately discarded; see above.
        }
    }

    private MdbShard BuildShard(List<FileBuild> builds) => new()
    {
        Files = [.. builds.Select(ToFileInfo)],
        Xorbs = _newXorbs,
    };

    /// <summary>
    /// Turns a chunked file into its shard entry: the file hash that becomes its ID, one term per
    /// contiguous run, a verification hash per term proving we hold the data, and the SHA-256 the
    /// Hub's LFS pointer references.
    /// </summary>
    private static ShardFileInfo ToFileInfo(FileBuild build)
    {
        var terms = new ShardFileTerm[build.Terms.Count];
        for (var i = 0; i < terms.Length; i++)
        {
            var term = build.Terms[i];
            var chunkHashes = new MerkleHash[term.End - term.Start];
            for (var j = 0; j < chunkHashes.Length; j++)
            {
                chunkHashes[j] = build.Chunks[term.FirstChunk + j].Hash;
            }

            terms[i] = new ShardFileTerm(
                term.Xorb!.Value,
                checked((uint)term.UnpackedLength),
                checked((uint)term.Start),
                checked((uint)term.End),
                XetHashes.VerificationHash(chunkHashes));
        }

        return new ShardFileInfo(build.FileId, terms)
        {
            // The reference implementation parses the digest's hex form into the hash type it uses
            // everywhere, which byte-swaps each 8-byte group relative to the raw digest.
            Sha256 = MerkleHash.FromHexOrder(Convert.FromHexString(build.Sha256)),
        };
    }

    /// <summary>One term while it is still growing, and before its xorb necessarily has a hash.</summary>
    private sealed class PendingTerm
    {
        public MerkleHash? Xorb { get; set; }

        public required int Start { get; init; }

        public required int End { get; set; }

        public required long UnpackedLength { get; set; }

        /// <summary>Index of the term's first chunk in its file's chunk list.</summary>
        public required int FirstChunk { get; init; }
    }

    /// <summary>One file as it is being taken apart.</summary>
    private sealed class FileBuild(XetUploadFile source)
    {
        public XetUploadFile Source { get; } = source;

        public List<(MerkleHash Hash, ulong Length)> Chunks { get; } = [];

        public List<PendingTerm> Terms { get; } = [];

        public long Size { get; set; }

        public string Sha256 { get; set; } = string.Empty;

        public long DeduplicatedBytes { get; set; }

        public MerkleHash FileId { get; set; }
    }
}
