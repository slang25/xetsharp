using Amazon.Lambda.AspNetCoreServer.Hosting;
using Amazon.Lambda.Serialization.SystemTextJson;
using XetSharp.Server;
using XetSharp.Server.Host;
using XetSharp.Server.Storage;

var settings = HostSettings.Parse(args);
if (settings is null)
{
    Console.Error.WriteLine(HostSettings.Usage);
    return 2;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.AddSimpleConsole(console => console.SingleLine = true);
// The server's own log says what was stored and served; per-request framework chatter is noise next to it.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
if (settings.Urls is { } urls)
{
    builder.WebHost.UseUrls(urls);
}

// Inert outside Lambda; inside it, this is what turns API Gateway or function-URL events into requests.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi, new SourceGeneratorLambdaJsonSerializer<LambdaJsonContext>());

var app = builder.Build();
var (store, repositories, description) = settings.CreateStores();
var server = new XetServer(store, repositories, settings.Options, app.Services.GetRequiredService<ILoggerFactory>());
app.MapXetServer(server);

app.Logger.LogInformation("xet-server storing in {Store}.", description);
await app.RunAsync();
return 0;
