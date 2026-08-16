using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XetSharp.Hub;
using XetSharp.Upload;

namespace XetSharp.Tests;

/// <summary>
/// The DI extension: a registered client transfers, shares the container's HttpClient plumbing, and
/// picks up whatever logging and time provider the application registered.
/// </summary>
public class DependencyInjectionTests
{
    private static readonly XetRepository Repository = XetRepository.Model("acme/scratch");

    [Test]
    public async Task Registers_a_client_that_uploads_and_downloads()
    {
        var cas = new FakeCas();
        var provider = NewProvider(cas);
        var client = provider.GetRequiredService<XetClient>();
        var content = TestData.SplitMix64Bytes(31, 200_000);

        var uploaded = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);
        using var destination = new MemoryStream();
        await client.DownloadAsync(Repository, uploaded.Files.Single().FileId.ToString(), destination);

        await Assert.That(destination.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Registers_the_client_once()
    {
        var provider = NewProvider(new FakeCas());

        await Assert.That(provider.GetRequiredService<XetClient>()).IsSameReferenceAs(provider.GetRequiredService<XetClient>());
    }

    /// <summary>
    /// The point of the package: the client's HTTP goes through the factory's named client, so
    /// handlers a caller adds to it are in the path.
    /// </summary>
    [Test]
    public async Task Sends_through_the_named_http_client()
    {
        var cas = new FakeCas();
        var counted = 0;
        var services = new ServiceCollection();
        services.AddXetClient(options => options with { HubUrl = new Uri("https://hub.invalid"), UseAmbientCredentials = false })
            .AddHttpMessageHandler(() => new CountingHandler(() => Interlocked.Increment(ref counted)))
            .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpHandler(cas.Handler));

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<XetClient>()
            .UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(32, 100_000))]);

        await Assert.That(counted).IsGreaterThan(0);
    }

    [Test]
    public async Task Logs_through_the_containers_logger_factory()
    {
        var cas = new FakeCas();
        var log = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(log).SetMinimumLevel(LogLevel.Debug));
        services.AddXetClient(options => options with { HubUrl = new Uri("https://hub.invalid"), UseAmbientCredentials = false })
            .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpHandler(cas.Handler));

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<XetClient>()
            .UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(33, 100_000))]);

        await Assert.That(log.Messages.Any(message => message.Contains("Uploaded 1 file(s)"))).IsTrue();
    }

    /// <summary>
    /// A repository lookup only works because redirects are off: following the resolve redirect
    /// would drop the Xet headers the file ID comes from.
    /// </summary>
    [Test]
    public async Task Configures_the_handler_the_protocol_needs()
    {
        var cas = new FakeCas();
        var provider = NewProvider(cas);
        var client = provider.GetRequiredService<XetClient>();
        var uploaded = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", TestData.SplitMix64Bytes(34, 100_000))]);

        var info = await client.GetFileInfoAsync(Repository, uploaded.Files.Single().FileId.ToString());

        await Assert.That(info.FileId).IsEqualTo(uploaded.Files.Single().FileId);
    }

    private static ServiceProvider NewProvider(FakeCas cas)
    {
        var services = new ServiceCollection();
        services.AddXetClient(options => options with { HubUrl = new Uri("https://hub.invalid"), UseAmbientCredentials = false })
            .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpHandler(cas.Handler));

        return services.BuildServiceProvider();
    }

    private sealed class CountingHandler(Action onSend) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly Lock _gate = new();

        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new ListLogger(this);

        public void Dispose()
        {
        }

        private sealed class ListLogger(ListLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (provider._gate)
                {
                    provider.Messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
