using System.Collections.Immutable;
using XetSharp.Hashing;
using XetSharp.Server.Storage;
using XetSharp.Shards;

namespace XetSharp.Server.Cas;

/// <summary>What a shard upload did: how many files it named, and whether any were new.</summary>
public sealed record ShardRegistration(int FileCount, int NewFileCount)
{
    /// <summary>The <c>result</c> the shard endpoints report: 1 when something was registered, 0 when it all already was.</summary>
    public int Result => NewFileCount > 0 ? 1 : 0;
}

/// <summary>
/// Validates an uploaded shard against what the server already holds, then registers the files it
/// describes. Every claim a shard makes is checked against the xorbs' own indexes: that each xorb
/// exists, that each chunk listing matches the xorb byte for byte, that each term's verification
/// hash proves the uploader had the data, and that each file hash is the one its chunks produce.
/// </summary>
internal sealed class ShardRegistrar(IXetStore store)
{
    /// <param name="repository">
    /// The repository the uploader's token is for, which the files' SHA-256 claims are recorded
    /// against: the one claim in a shard the server cannot check, so it reaches no further.
    /// </param>
    /// <param name="progress">
    /// Told (verified, total) chunk counts as validation goes, which is what the streaming upload
    /// endpoint reports to its client.
    /// </param>
    /// <exception cref="XetServerException">The shard is malformed, names a xorb that is not stored, or lies about one.</exception>
    public async Task<ShardRegistration> RegisterAsync(
        ReadOnlyMemory<byte> serialized,
        RepositoryId repository,
        Func<long, long, ValueTask>? progress,
        CancellationToken cancellationToken)
    {
        if (serialized.Length > MdbShard.MaxUploadSize)
        {
            throw XetServerException.BadRequest($"The shard is {serialized.Length} bytes; the limit is {MdbShard.MaxUploadSize}.");
        }

        MdbShard shard;
        try
        {
            shard = MdbShard.Parse(serialized.Span);
        }
        catch (InvalidDataException exception)
        {
            throw XetServerException.BadRequest($"The shard is malformed: {exception.Message}");
        }

        var xorbs = await LoadReferencedXorbsAsync(shard, cancellationToken).ConfigureAwait(false);

        var total = shard.Xorbs.Sum(xorb => (long)xorb.Chunks.Count) +
                    shard.Files.Sum(file => file.Terms.Sum(term => (long)term.ChunkCount));
        var verified = 0L;
        await ReportAsync(progress, verified, total).ConfigureAwait(false);

        foreach (var listing in shard.Xorbs)
        {
            VerifyListing(listing, xorbs[listing.XorbHash]);
            verified += listing.Chunks.Count;
            await ReportAsync(progress, verified, total).ConfigureAwait(false);
        }

        var files = new List<StoredFile>(shard.Files.Count);
        foreach (var file in shard.Files)
        {
            files.Add(VerifyFile(file, xorbs));
            verified += file.Terms.Sum(term => (long)term.ChunkCount);
            await ReportAsync(progress, verified, total).ConfigureAwait(false);
        }

        var newFiles = 0;
        foreach (var file in files)
        {
            if (await store.PutFileAsync(file, repository, cancellationToken).ConfigureAwait(false))
            {
                newFiles++;
            }
        }

        return new ShardRegistration(files.Count, newFiles);
    }

    private static ValueTask ReportAsync(Func<long, long, ValueTask>? progress, long verified, long total) =>
        progress is null ? ValueTask.CompletedTask : progress(verified, total);

    private async Task<Dictionary<MerkleHash, StoredXorb>> LoadReferencedXorbsAsync(MdbShard shard, CancellationToken cancellationToken)
    {
        var referenced = shard.Xorbs.Select(xorb => xorb.XorbHash)
            .Concat(shard.Files.SelectMany(file => file.Terms.Select(term => term.XorbHash)))
            .Distinct();

        var xorbs = new Dictionary<MerkleHash, StoredXorb>();
        foreach (var hash in referenced)
        {
            xorbs[hash] = await store.GetXorbAsync(hash, cancellationToken).ConfigureAwait(false)
                ?? throw XetServerException.BadRequest($"The shard references xorb {hash}, which has not been uploaded.");
        }

        return xorbs;
    }

    /// <summary>
    /// A CAS-info block must describe the xorb the server indexed from the bytes: same chunks,
    /// same lengths, offsets that are the running total of the raw lengths.
    /// </summary>
    private static void VerifyListing(ShardCasInfo listing, StoredXorb xorb)
    {
        if (listing.Chunks.Count != xorb.Chunks.Length)
        {
            throw XetServerException.BadRequest(
                $"The shard lists {listing.Chunks.Count} chunks for xorb {xorb.Hash}, which holds {xorb.Chunks.Length}.");
        }

        var offset = 0L;
        for (var i = 0; i < listing.Chunks.Count; i++)
        {
            var claimed = listing.Chunks[i];
            var actual = xorb.Chunks[i];
            if (claimed.Hash != actual.Hash || claimed.UnpackedLength != actual.UnpackedLength || claimed.ByteRangeStart != offset)
            {
                throw XetServerException.BadRequest($"The shard's listing of xorb {xorb.Hash} disagrees with the xorb at chunk {i}.");
            }

            offset += actual.UnpackedLength;
        }

        if (listing.TotalUncompressedBytes != offset)
        {
            throw XetServerException.BadRequest(
                $"The shard says xorb {xorb.Hash} unpacks to {listing.TotalUncompressedBytes} bytes; its chunks total {offset}.");
        }

        // The published reference shards carry zero here, so zero has to mean "not stated".
        if (listing.SerializedLength != 0 && listing.SerializedLength != xorb.SerializedLength)
        {
            throw XetServerException.BadRequest(
                $"The shard says xorb {xorb.Hash} is {listing.SerializedLength} bytes serialized; it is {xorb.SerializedLength}.");
        }
    }

    private static StoredFile VerifyFile(ShardFileInfo file, Dictionary<MerkleHash, StoredXorb> xorbs)
    {
        if (!file.HasVerification && file.Terms.Count > 0)
        {
            throw XetServerException.BadRequest($"File {file.FileHash} carries no verification entries; an uploaded shard must.");
        }

        var terms = ImmutableArray.CreateBuilder<StoredTerm>(file.Terms.Count);
        var nodes = new List<(MerkleHash Hash, ulong Length)>();
        var size = 0L;
        foreach (var term in file.Terms)
        {
            var xorb = xorbs[term.XorbHash];
            if (term.ChunkIndexStart >= term.ChunkIndexEnd || term.ChunkIndexEnd > xorb.Chunks.Length)
            {
                throw XetServerException.BadRequest(
                    $"File {file.FileHash} has a term over chunks [{term.ChunkIndexStart}, {term.ChunkIndexEnd}) of xorb {xorb.Hash}, which holds {xorb.Chunks.Length}.");
            }

            var start = (int)term.ChunkIndexStart;
            var end = (int)term.ChunkIndexEnd;
            var unpacked = xorb.UnpackedLengthOf(start, end);
            if (term.UnpackedLength != unpacked)
            {
                throw XetServerException.BadRequest(
                    $"File {file.FileHash} says a term unpacks to {term.UnpackedLength} bytes; its chunks total {unpacked}.");
            }

            var chunkHashes = new MerkleHash[end - start];
            for (var i = start; i < end; i++)
            {
                chunkHashes[i - start] = xorb.Chunks[i].Hash;
                nodes.Add((xorb.Chunks[i].Hash, (ulong)xorb.Chunks[i].UnpackedLength));
            }

            if (term.VerificationHash is { } claimed && claimed != XetHashes.VerificationHash(chunkHashes))
            {
                throw XetServerException.BadRequest($"File {file.FileHash} has a term whose verification hash does not match its chunks.");
            }

            terms.Add(new StoredTerm(xorb.Hash, start, end, unpacked));
            size += unpacked;
        }

        var actualHash = XetHashes.FileHash(nodes.ToArray());
        if (actualHash != file.FileHash)
        {
            throw XetServerException.BadRequest($"File {file.FileHash} is not the hash of its terms' chunks, which is {actualHash}.");
        }

        return new StoredFile(file.FileHash, size, file.Sha256, terms.MoveToImmutable());
    }
}
