using System.Text.Json.Serialization;
using Amazon.Lambda.APIGatewayEvents;

namespace XetSharp.Server.Host;

/// <summary>
/// The Lambda event types the host reads and writes, declared for source generation so the
/// Lambda hosting layer needs no reflection under Native AOT.
/// </summary>
[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyRequest))]
[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyResponse))]
internal sealed partial class LambdaJsonContext : JsonSerializerContext;
