using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XetSharp;
using XetSharp.Http;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers a <see cref="XetClient"/> in a service collection, wired to
/// <c>IHttpClientFactory</c>, the application's logging and its <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// This lives in its own package so the core library needs nothing from
/// <c>Microsoft.Extensions.*</c> beyond the logging abstractions: a console tool that news up a
/// <see cref="XetClient"/> pays for none of this.
/// </remarks>
public static class XetClientServiceCollectionExtensions
{
    /// <summary>The name of the <c>HttpClient</c> the registered <see cref="XetClient"/> transfers over.</summary>
    public const string HttpClientName = "XetSharp";

    /// <summary>
    /// Registers <see cref="XetClient"/> as a singleton over a named <c>HttpClient</c> configured
    /// the way the protocol needs — redirects off, retry handler installed, XetSharp's user agent.
    /// </summary>
    /// <param name="services">The collection to register in.</param>
    /// <param name="configure">
    /// Adjusts the options the client is built with. The argument already carries the HttpClient,
    /// logger factory and time provider taken from the container, so a caller returns
    /// <c>options with { … }</c> rather than building a fresh record.
    /// </param>
    /// <returns>
    /// The builder for the underlying named <c>HttpClient</c>, so callers can add their own
    /// handlers, resilience or primary-handler configuration to it.
    /// </returns>
    /// <example>
    /// <code>
    /// services.AddXetClient(options => options with { HubToken = configuration["HuggingFace:Token"] });
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddXetClient(
        this IServiceCollection services,
        Func<XetClientOptions, XetClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services
            .AddHttpClient(HttpClientName, client =>
            {
                client.Timeout = DefaultOptions.RequestTimeout;
                client.DefaultRequestHeaders.UserAgent.Add(XetClient.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // The resolve endpoint answers with a redirect whose headers carry the file ID;
                // following it would replace them with a CDN response that has none.
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            })
            .AddHttpMessageHandler(provider => new XetRetryHandler(provider.GetService<TimeProvider>())
            {
                Logger = provider.GetService<ILoggerFactory>()?.CreateLogger<XetRetryHandler>()
                    ?? Logging.Abstractions.NullLogger<XetRetryHandler>.Instance,
            });

        services.TryAddSingleton(provider =>
        {
            var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            var options = new XetClientOptions
            {
                HttpClient = httpClient,
                LoggerFactory = provider.GetService<ILoggerFactory>(),
                TimeProvider = provider.GetService<TimeProvider>(),
            };

            var configured = configure?.Invoke(options) ?? options;

            // RequestTimeout is documented as ignored when an HttpClient is supplied, and here one
            // always is — so honour it against the client this registration created, which nothing
            // else shares.
            if (ReferenceEquals(configured.HttpClient, httpClient))
            {
                httpClient.Timeout = configured.RequestTimeout;
            }

            return new XetClient(configured);
        });

        return builder;
    }

    private static readonly XetClientOptions DefaultOptions = new();
}
