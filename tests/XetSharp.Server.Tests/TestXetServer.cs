using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XetSharp.Hub;
using XetSharp.Server.Storage;

namespace XetSharp.Server.Tests;

/// <summary>
/// A server hosted in-process behind <see cref="TestServer"/>, with the real XetSharp client
/// pointed at it: the client's Hub, CAS and data requests all land on the same handler, so a
/// whole upload or download runs through the real pipeline on both sides with no network.
/// </summary>
internal sealed class TestXetServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestXetServer(WebApplication app, XetServer server, IXetStore store)
    {
        _app = app;
        Server = server;
        Store = store;
        HttpClient = app.GetTestClient();
    }

    public XetServer Server { get; }

    public IXetStore Store { get; }

    public HttpClient HttpClient { get; }

    public Uri BaseUrl => HttpClient.BaseAddress!;

    public static Task<TestXetServer> StartAsync(XetServerOptions? options = null)
    {
        var store = new InMemoryXetStore();
        return StartAsync(store, store, options);
    }

    public static async Task<TestXetServer> StartAsync(IXetStore store, IRepositoryStore repositories, XetServerOptions? options = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        var server = new XetServer(store, repositories, options, app.Services.GetRequiredService<ILoggerFactory>());
        app.MapXetServer(server);
        await app.StartAsync();
        return new TestXetServer(app, server, store);
    }

    /// <summary>A client of this server. Anonymous unless a token is given.</summary>
    public XetClient CreateClient(string? hubToken = null, XetClientOptions? options = null) =>
        new((options ?? new XetClientOptions()) with
        {
            HubUrl = BaseUrl,
            HttpClient = HttpClient,
            HubToken = hubToken,
            UseAmbientCredentials = false,
        });

    public async ValueTask DisposeAsync()
    {
        HttpClient.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
