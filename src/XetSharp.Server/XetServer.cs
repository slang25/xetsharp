using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XetSharp.Server.Auth;
using XetSharp.Server.Cas;
using XetSharp.Server.Storage;

namespace XetSharp.Server;

/// <summary>
/// A Xet server: the CAS API plus the slice of the Hugging Face Hub API a client needs to reach
/// it, over whichever <see cref="IXetStore"/> it is given. Map it into any ASP.NET Core
/// application with <see cref="XetServerEndpointRouteBuilderExtensions.MapXetServer"/>.
/// </summary>
public sealed class XetServer
{
    public XetServer(IXetStore store, IRepositoryStore repositories, XetServerOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(repositories);
        Store = store;
        Repositories = repositories;
        Options = options ?? new XetServerOptions();
        Logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("XetSharp.Server");

        var key = Options.SigningKey ?? RandomNumberGenerator.GetBytes(32);
        Tokens = new XetTokenService(key, Options.TimeProvider);
        Signer = new XorbUrlSigner(key, Options.TimeProvider);
        DeduplicationKey = new MerkleHash(HMACSHA256.HashData(key, "xetsharp-server/deduplication-key"u8));
        Registrar = new ShardRegistrar(store);
        Reconstructions = new ReconstructionBuilder(store, Signer, Options);
    }

    /// <summary>A server over a single in-memory store, for tests and quick local runs.</summary>
    public static XetServer InMemory(XetServerOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        var store = new InMemoryXetStore();
        return new XetServer(store, store, options, loggerFactory);
    }

    public IXetStore Store { get; }

    public IRepositoryStore Repositories { get; }

    public XetServerOptions Options { get; }

    internal ILogger Logger { get; }

    internal XetTokenService Tokens { get; }

    internal XorbUrlSigner Signer { get; }

    internal ShardRegistrar Registrar { get; }

    internal ReconstructionBuilder Reconstructions { get; }

    /// <summary>The key global-deduplication responses HMAC their chunk hashes with.</summary>
    internal MerkleHash DeduplicationKey { get; }

    /// <summary>
    /// The URL this server is reachable at, for the CAS URL in a token and the URLs in a
    /// reconstruction: configured, or else what the request came in on.
    /// </summary>
    internal Uri BaseUrl(HttpRequest request)
    {
        if (Options.PublicBaseUrl is { } configured)
        {
            return configured;
        }

        var builder = new StringBuilder()
            .Append(request.Scheme)
            .Append("://")
            .Append(request.Host.ToUriComponent())
            .Append(request.PathBase.ToUriComponent())
            .Append('/');
        return new Uri(builder.ToString());
    }
}
