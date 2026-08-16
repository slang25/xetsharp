using System.Net;
using Microsoft.Extensions.Logging;
using XetSharp.Http;
using XetSharp.Hub;
using XetSharp.Upload;

namespace XetSharp.Tests;

/// <summary>
/// What the client tells an <see cref="ILogger"/>, and what it costs when nobody is listening.
/// </summary>
public class LoggingTests
{
    private static readonly XetRepository Repository = XetRepository.Model("acme/scratch");

    [Test]
    public async Task Reports_an_upload_and_a_download_at_information_level()
    {
        var log = new RecordingLoggerFactory();
        var (client, _) = NewClient(log);
        var content = TestData.SplitMix64Bytes(21, 300_000);

        var uploaded = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);
        using var destination = new MemoryStream();
        await client.DownloadAsync(Repository, uploaded.Files.Single().FileId.ToString(), destination);

        var headlines = log.Entries.Where(entry => entry.Level >= LogLevel.Information).Select(entry => entry.Message).ToList();
        await Assert.That(headlines.Any(message => message.Contains("Uploaded 1 file(s) as 1 xorb(s)"))).IsTrue();
        await Assert.That(headlines.Any(message => message.Contains($"Downloaded {content.Length} bytes"))).IsTrue();
    }

    /// <summary>
    /// The per-request detail sits below Information, so a caller logging at Information gets a line
    /// per transfer rather than a line per xorb.
    /// </summary>
    [Test]
    public async Task Keeps_the_per_request_detail_at_debug()
    {
        var log = new RecordingLoggerFactory();
        var (client, _) = NewClient(log);

        await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(22, 300_000))]);

        var debug = log.Entries.Where(entry => entry.Level == LogLevel.Debug).Select(entry => entry.Message).ToList();
        await Assert.That(debug.Any(message => message.Contains("Minting a Write token"))).IsTrue();
        await Assert.That(debug.Any(message => message.Contains("Sealed xorb"))).IsTrue();
        await Assert.That(debug.Any(message => message.Contains("Chunked 'data.bin'"))).IsTrue();
        await Assert.That(log.Entries.Any(entry => entry.Level >= LogLevel.Warning)).IsFalse();
    }

    [Test]
    public async Task Warns_about_a_retry()
    {
        var log = new RecordingLoggerFactory();
        var attempts = 0;
        var handler = new FakeHttpHandler(_ => ++attempts < 2 ? FakeHttpHandler.Status(HttpStatusCode.ServiceUnavailable) : FakeHttpHandler.Json("{}"));
        using var client = new HttpClient(new XetRetryHandler(handler, new TestTimeProvider())
        {
            Logger = log.CreateLogger("retry"),
        });

        await client.GetAsync("https://example.invalid/");

        var warning = log.Entries.Single(entry => entry.Level == LogLevel.Warning);
        await Assert.That(warning.Message).Contains("Retrying GET https://example.invalid/");
        await Assert.That(warning.Message).Contains("503");
    }

    /// <summary>
    /// CAS signs xorb URLs by putting the credential in the query, so the retry warning — the one
    /// log line that carries a URL, and the one that fires on exactly the flaky connections a
    /// support engineer will be reading logs about — has to stop at the path.
    /// </summary>
    [Test]
    public async Task Keeps_a_signed_urls_query_out_of_the_retry_warning()
    {
        var log = new RecordingLoggerFactory();
        var attempts = 0;
        var handler = new FakeHttpHandler(_ => ++attempts < 2 ? FakeHttpHandler.Status(HttpStatusCode.ServiceUnavailable) : FakeHttpHandler.Json("{}"));
        using var client = new HttpClient(new XetRetryHandler(handler, new TestTimeProvider())
        {
            Logger = log.CreateLogger("retry"),
        });

        await client.GetAsync("https://cas.invalid/xorbs/default/abc123?X-Xet-Signed-Range=0-1024&Signature=s3cr3t");

        var warning = log.Entries.Single(entry => entry.Level == LogLevel.Warning);
        await Assert.That(warning.Message).Contains("https://cas.invalid/xorbs/default/abc123");
        await Assert.That(warning.Message).DoesNotContain("s3cr3t");
        await Assert.That(warning.Message).DoesNotContain("Signature");
        await Assert.That(warning.Message).DoesNotContain("X-Xet-Signed-Range");
    }

    /// <summary>A client given no logger factory logs nowhere, and does not go looking for one.</summary>
    [Test]
    public async Task Logs_nothing_by_default()
    {
        var (client, cas) = NewClient(loggerFactory: null);

        await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(23, 100_000))]);

        await Assert.That(cas.Shards.Count).IsEqualTo(1);
    }

    private static (XetClient Client, FakeCas Cas) NewClient(ILoggerFactory? loggerFactory)
    {
        var cas = new FakeCas();
        var client = new XetClient(new XetClientOptions
        {
            HubUrl = new Uri("https://hub.invalid"),
            UseAmbientCredentials = false,
            HttpClient = new HttpClient(new FakeHttpHandler(cas.Handler)),
            LoggerFactory = loggerFactory,
        });

        return (client, cas);
    }

    private sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message);

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly Lock _gate = new();

        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private void Add(LogEntry entry)
        {
            lock (_gate)
            {
                Entries.Add(entry);
            }
        }

        private sealed class RecordingLogger(RecordingLoggerFactory factory, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                factory.Add(new LogEntry(category, logLevel, eventId, formatter(state, exception)));
        }
    }
}
