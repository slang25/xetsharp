using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XetSharp.Server.Storage;

namespace XetSharp.Server.Tests;

/// <summary>
/// The server on a real port, for a client in another process. Bound to a free port on loopback,
/// so parallel tests never collide.
/// </summary>
internal sealed class KestrelXetServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private KestrelXetServer(WebApplication app, XetServer server, Uri url)
    {
        _app = app;
        Server = server;
        Url = url;
    }

    public XetServer Server { get; }

    /// <summary>The bound address, without a trailing slash — the form <c>HF_ENDPOINT</c> wants.</summary>
    public Uri Url { get; }

    public static Task<KestrelXetServer> StartAsync(XetServerOptions? options = null)
    {
        var store = new InMemoryXetStore();
        return StartAsync(store, store, options);
    }

    public static async Task<KestrelXetServer> StartAsync(IXetStore store, IRepositoryStore repositories, XetServerOptions? options = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        var server = new XetServer(store, repositories, options, app.Services.GetRequiredService<ILoggerFactory>());
        app.MapXetServer(server);
        await app.StartAsync();
        return new KestrelXetServer(app, server, new Uri(app.Urls.Single().TrimEnd('/')));
    }

    public XetClient CreateClient() => new(new XetClientOptions { HubUrl = new Uri(Url + "/"), UseAmbientCredentials = false });

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
