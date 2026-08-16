using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using XetSharp.Cas;
using XetSharp.Download;
using XetSharp.Http;
using XetSharp.Hub;
using XetSharp.Upload;

namespace XetSharp;

/// <summary>
/// Downloads and uploads files stored on the Hugging Face Hub over the Xet protocol.
/// </summary>
/// <example>
/// <code>
/// using var client = new XetClient();
/// await client.DownloadToFileAsync(
///     XetRepository.Model("openai-community/gpt2"), "model.safetensors", "model.safetensors");
/// </code>
/// </example>
public sealed class XetClient : IDisposable
{
    private static readonly ProductInfoHeaderValue UserAgent = new(
        "XetSharp",
        typeof(XetClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.1.0");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly HubClient _hub;
    private readonly CasClient _cas;
    private readonly ReconstructionWriter _writer;
    private readonly XetDownloadOptions _downloadOptions;
    private readonly XetUploadOptions _uploadOptions;
    private readonly TimeProvider _timeProvider;

    public XetClient(XetClientOptions? options = null)
    {
        options ??= new XetClientOptions();
        _downloadOptions = options.Download;
        _uploadOptions = options.Upload;
        _uploadOptions.Validate();
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _ownsHttpClient = options.HttpClient is null;
        _httpClient = options.HttpClient ?? CreateHttpClient(options);

        var token = options.HubToken ?? (options.UseAmbientCredentials ? HuggingFaceCredentials.ResolveToken() : null);
        _hub = new HubClient(_httpClient, options.HubUrl, token);
        _cas = new CasClient(_httpClient, new XetTokenProvider(_hub, options.TimeProvider));
        _writer = new ReconstructionWriter(
            new XorbRangeFetcher(_httpClient, _downloadOptions.MaxConcurrentDownloads),
            _downloadOptions);
    }

    /// <summary>The Hub client this instance uses, for callers that need the raw endpoints.</summary>
    public HubClient Hub => _hub;

    /// <summary>The CAS client this instance uses, for callers that need the raw endpoints.</summary>
    public CasClient Cas => _cas;

    /// <summary>Looks up a file's Xet file ID, size and recorded SHA-256.</summary>
    public Task<XetFileInfo> GetFileInfoAsync(XetRepository repository, string path, CancellationToken cancellationToken = default) =>
        _hub.GetFileInfoAsync(repository, path, cancellationToken);

    /// <summary>
    /// Downloads a whole file into <paramref name="destination"/>, verifying on the way that the
    /// bytes reconstruct to the file ID they were requested by.
    /// </summary>
    public async Task<XetDownloadResult> DownloadAsync(
        XetRepository repository,
        string path,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        var file = await GetFileInfoAsync(repository, path, cancellationToken).ConfigureAwait(false);
        return await DownloadAsync(repository, file, destination, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a whole file to a path, writing to a neighbouring <c>.part</c> file first so a
    /// failed or unverifiable download never leaves a plausible-looking file behind.
    /// </summary>
    public async Task<XetDownloadResult> DownloadToFileAsync(
        XetRepository repository,
        string path,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var partialPath = destinationPath + ".part";
        try
        {
            XetDownloadResult result;
            var destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            await using (destination.ConfigureAwait(false))
            {
                result = await DownloadAsync(repository, path, destination, cancellationToken).ConfigureAwait(false);
            }

            File.Move(partialPath, destinationPath, overwrite: true);
            return result;
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    /// <summary>
    /// Downloads part of a file: <paramref name="length"/> bytes (or the rest of the file) starting
    /// at <paramref name="offset"/>. Whole-file verification does not apply to a partial download.
    /// </summary>
    public async Task<XetDownloadResult> DownloadRangeAsync(
        XetRepository repository,
        string path,
        Stream destination,
        long offset,
        long? length = null,
        CancellationToken cancellationToken = default)
    {
        var file = await GetFileInfoAsync(repository, path, cancellationToken).ConfigureAwait(false);
        return await DownloadAsync(repository, file, destination, offset, length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a file already resolved to a <see cref="XetFileInfo"/>, skipping the Hub lookup.
    /// </summary>
    public async Task<XetDownloadResult> DownloadAsync(
        XetRepository repository,
        XetFileInfo file,
        Stream destination,
        long offset = 0,
        long? length = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "A download length cannot be negative.");
        }

        // The inclusive end of the range is offset + length - 1, which would wrap around to a value
        // below the offset and be mistaken for an empty range.
        if (length is { } requested && requested > 0 && offset > long.MaxValue - (requested - 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length, $"A download of {requested} bytes from offset {offset} ends past the end of the 64-bit range.");
        }

        var knownSize = file.Size >= 0 ? file.Size : (long?)null;
        var isWholeFile = offset == 0 && (length is null || (knownSize is { } size && length >= size));

        ByteRange? range = null;
        long? maxBytes = null;
        if (!isWholeFile)
        {
            var end = length is { } wanted ? offset + wanted - 1 : knownSize - 1;
            if (end is null)
            {
                throw new ArgumentException(
                    "A length is required when the file's size is unknown.", nameof(length));
            }

            if (knownSize is { } fileSize)
            {
                if (offset >= fileSize)
                {
                    return new XetDownloadResult(0);
                }

                end = Math.Min(end.Value, fileSize - 1);
            }

            if (end.Value < offset)
            {
                return new XetDownloadResult(0);
            }

            range = new ByteRange(offset, end.Value);
            maxBytes = range.Value.Length;
        }

        var reconstruction = await _cas.GetReconstructionAsync(repository, file.FileId, range, cancellationToken).ConfigureAwait(false);

        return await _writer.WriteAsync(
            reconstruction,
            destination,
            maxBytes,
            isWholeFile && _downloadOptions.VerifyFileHash ? file.FileId : null,
            isWholeFile && _downloadOptions.VerifySha256 ? file.Sha256 : null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads files over the Xet protocol: chunk, deduplicate against what already exists, pack the
    /// rest into xorbs, and register the result. The bytes are stored and addressable afterwards, but
    /// a Git-backed repository does not show them until they are committed — see
    /// <see cref="UploadAndCommitAsync"/>.
    /// </summary>
    /// <remarks>Needs a Hub token with write access to the repository and revision.</remarks>
    public Task<XetUploadResult> UploadAsync(
        XetRepository repository,
        IReadOnlyList<XetUploadFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(files);
        repository.Validate();

        if (files.Count == 0)
        {
            throw new ArgumentException("There are no files to upload.", nameof(files));
        }

        var duplicate = files.GroupBy(file => file.Path, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"'{duplicate.Key}' is listed more than once.", nameof(files));
        }

        return new UploadSession(_cas, repository, _uploadOptions, _timeProvider).RunAsync(files, cancellationToken);
    }

    /// <summary>Uploads a single local file.</summary>
    public Task<XetUploadResult> UploadFileAsync(
        XetRepository repository,
        string localPath,
        string? pathInRepository = null,
        CancellationToken cancellationToken = default) =>
        UploadAsync(repository, [XetUploadFile.FromFile(localPath, pathInRepository)], cancellationToken);

    /// <summary>
    /// Uploads files and then commits LFS pointers to them, which is what a Git-backed repository
    /// needs before the files appear in it.
    /// </summary>
    public async Task<XetUploadResult> UploadAndCommitAsync(
        XetRepository repository,
        IReadOnlyList<XetUploadFile> files,
        string summary,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        // Checked here rather than left to the commit: a summary the Hub will refuse is worth
        // knowing about before the files are transferred, not after.
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        var result = await UploadAsync(repository, files, cancellationToken).ConfigureAwait(false);
        var commit = await CommitAsync(
            repository,
            new XetCommitRequest
            {
                Summary = summary,
                Description = description,
                Files = [.. result.Files.Select(file => new XetCommitFile(file.Path, file.Sha256, file.Size))],
            },
            cancellationToken).ConfigureAwait(false);

        return result with { Commit = commit };
    }

    /// <summary>
    /// Commits pointers to files whose contents are already stored. Uploading and committing are
    /// separate steps because storing the bytes is the Xet protocol's job and recording them in the
    /// repository's history is the Hub's.
    /// </summary>
    public Task<XetCommit> CommitAsync(XetRepository repository, XetCommitRequest request, CancellationToken cancellationToken = default) =>
        _hub.CommitAsync(repository, request, cancellationToken);

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateHttpClient(XetClientOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            // The resolve endpoint answers with a redirect whose headers carry the file ID; following
            // it would replace them with a CDN response that has none.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        var client = new HttpClient(new XetRetryHandler(handler, options.TimeProvider))
        {
            Timeout = options.RequestTimeout,
        };
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do: the original failure is what the caller needs to see.
        }
    }
}
