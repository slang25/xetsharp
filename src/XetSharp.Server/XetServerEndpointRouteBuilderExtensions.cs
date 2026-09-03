using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using XetSharp.Server.Cas;
using XetSharp.Server.Hub;

namespace XetSharp.Server;

/// <summary>Maps a <see cref="XetServer"/>'s endpoints into an ASP.NET Core application.</summary>
public static class XetServerEndpointRouteBuilderExtensions
{
    /// <summary>Maps both halves: the CAS API and the Hub facade.</summary>
    public static IEndpointRouteBuilder MapXetServer(this IEndpointRouteBuilder endpoints, XetServer server)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(server);
        endpoints.MapXetCas(server);
        endpoints.MapXetHub(server);
        return endpoints;
    }

    /// <summary>
    /// Maps the CAS API alone — reconstructions, global deduplication, xorb and shard upload, and
    /// the signed xorb data route — for a deployment whose Hub is elsewhere.
    /// </summary>
    public static IEndpointRouteBuilder MapXetCas(this IEndpointRouteBuilder endpoints, XetServer server)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(server);
        new CasEndpoints(server).Map(endpoints);
        return endpoints;
    }

    /// <summary>Maps the Hub facade alone: tokens, resolve, preupload, commit and plain download.</summary>
    public static IEndpointRouteBuilder MapXetHub(this IEndpointRouteBuilder endpoints, XetServer server)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(server);
        new HubEndpoints(server).Map(endpoints);
        return endpoints;
    }
}
