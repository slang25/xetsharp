using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using XetSharp.Cas;
using XetSharp.Download;
using XetSharp.Http;
using XetSharp.Hub;

namespace XetSharp;

/// <summary>
/// Downloads files stored on the Hugging Face Hub over the Xet protocol.
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

    public XetClient(XetClientOptions? options = null)
    {
        options ??= new XetClientOptions();
        _downloadOptions = options.Download;
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
