using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XetSharp.Buffers;
using XetSharp.Cas;
using XetSharp.Diagnostics;
using XetSharp.Hashing;

namespace XetSharp.Download;

/// <summary>What a completed download turned out to contain.</summary>
/// <param name="BytesWritten">Bytes handed to the destination stream.</param>
/// <param name="FileHash">
/// The file hash re-derived from the downloaded chunks, when the whole file was downloaded and
/// verification was enabled; null otherwise.
/// </param>
/// <param name="Sha256">The SHA-256 of the written bytes, under the same conditions.</param>
public sealed record XetDownloadResult(long BytesWritten, MerkleHash? FileHash = null, string? Sha256 = null);

/// <summary>
/// Turns a reconstruction into file bytes: fetches the xorb ranges the terms need — several at a
/// time, a bounded distance ahead of the writer — and writes each term's chunks out in order.
/// </summary>
internal sealed class ReconstructionWriter(XorbRangeFetcher fetcher, XetDownloadOptions options, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <param name="reconstruction">The response describing the file, or the requested slice of it.</param>
    /// <param name="destination">Where the file bytes go. Written strictly in order.</param>
    /// <param name="maxBytes">
    /// A cap on the output, for range requests whose end falls mid-chunk. Null writes everything the
    /// reconstruction covers.
    /// </param>
    /// <param name="expectedFileId">
    /// The file ID to check the downloaded chunks against. Only pass this when downloading a whole
    /// file, since the check aggregates every chunk of it.
    /// </param>
    /// <param name="expectedSha256">The Hub's SHA-256 for the file, under the same whole-file condition.</param>
    /// <param name="progress">Told how many bytes have reached the destination, if anyone is listening.</param>
    public async Task<XetDownloadResult> WriteAsync(
        FileReconstruction reconstruction,
        Stream destination,
        long? maxBytes = null,
        MerkleHash? expectedFileId = null,
        string? expectedSha256 = null,
        IProgress<XetProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = FetchPlan.Create(reconstruction, fetcher, options, cancellationToken);
        var chunkHashes = expectedFileId is not null ? new List<(MerkleHash Hash, ulong Length)>() : null;
        using var sha256 = expectedSha256 is not null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        using var chunkBuffer = new PooledBufferWriter(Xorbs.XorbChunkHeader.MaxUncompressedSize);

        var limit = maxBytes ?? long.MaxValue;
        var skipRemaining = reconstruction.OffsetIntoFirstRange;
        var written = 0L;
        var reporter = new ProgressReporter(progress, Math.Min(limit, reconstruction.ContentLength));
        _logger.ReconstructionPlanned(Math.Min(limit, reconstruction.ContentLength), reconstruction.Terms.Count, reconstruction.Xorbs.Count);

        try
        {
            for (var termIndex = 0; termIndex < plan.Terms.Count && written < limit; termIndex++)
            {
                var term = plan.Terms[termIndex];
                var unpacked = 0L;

                foreach (var segment in term.Segments)
                {
                    var data = await plan.GetRangeAsync(segment).ConfigureAwait(false);
                    for (var chunkIndex = segment.Chunks.Start; chunkIndex < segment.Chunks.End; chunkIndex++)
                    {
                        chunkBuffer.Reset();
                        unpacked += data.DecompressChunk(chunkIndex, chunkBuffer);
                        var chunk = chunkBuffer.WrittenMemory;
                        chunkHashes?.Add((XetHashes.ChunkHash(chunk.Span), (ulong)chunk.Length));

                        // The skip only ever applies at the very start, and may swallow whole chunks.
                        if (skipRemaining > 0)
                        {
                            var skipped = (int)Math.Min(skipRemaining, chunk.Length);
                            skipRemaining -= skipped;
                            chunk = chunk[skipped..];
                        }

                        if (written + chunk.Length > limit)
                        {
                            chunk = chunk[..(int)(limit - written)];
                        }

                        if (chunk.Length == 0)
                        {
                            continue;
                        }

                        sha256?.AppendData(chunk.Span);
                        await destination.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                        written += chunk.Length;
                        reporter.Advance(chunk.Length);
                    }
                }

                // The protocol requires this check: a term that decompresses to the wrong length
                // means the xorb data was truncated or corrupted in transit.
                if (unpacked != term.Term.UnpackedLength)
                {
                    throw new XetVerificationException(
                        $"Term {termIndex} of xorb {term.Term.Xorb} decompressed to {unpacked} bytes, " +
                        $"but the reconstruction declares {term.Term.UnpackedLength}.");
                }

                plan.ReleaseCompleted(termIndex);
            }
        }
        finally
        {
            plan.Dispose();
        }

        reporter.Complete();

        var fileHash = Verify(chunkHashes, expectedFileId, sha256, expectedSha256);
        return new XetDownloadResult(written, fileHash, sha256 is null ? null : Convert.ToHexStringLower(sha256.GetCurrentHash()));
    }

    private static MerkleHash? Verify(
        List<(MerkleHash Hash, ulong Length)>? chunkHashes,
        MerkleHash? expectedFileId,
        IncrementalHash? sha256,
        string? expectedSha256)
    {
        MerkleHash? fileHash = null;
        if (chunkHashes is not null && expectedFileId is { } expected)
        {
            fileHash = XetHashes.FileHash(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chunkHashes));
            if (fileHash != expected)
            {
                // The salt is the one input to a file hash that does not come from the file itself,
                // and nothing in the download path reveals it, so a salted repository would land
                // here with data that is in fact intact.
                throw new XetVerificationException(
                    $"The downloaded data hashes to {fileHash}, not to the file ID {expected} it was requested by. " +
                    "File-hash verification assumes the default (zero) repository salt.");
            }
        }

        if (sha256 is not null && expectedSha256 is not null)
        {
            var actual = Convert.ToHexStringLower(sha256.GetCurrentHash());
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new XetVerificationException($"The downloaded data has SHA-256 {actual}, but the Hub records {expectedSha256}.");
            }
        }

        return fileHash;
    }
}
