using System.Text;
using Amazon.S3;
using XetSharp.Server.Storage;
using XetSharp.Server.Storage.S3;

namespace XetSharp.Server.Host;

/// <summary>
/// What the single binary is told on the command line or through the environment. Every setting
/// has both forms so a laptop can use flags and a Lambda can use environment variables.
/// </summary>
internal sealed record HostSettings(string Store, string? Urls, XetServerOptions Options)
{
    public const string Usage = """
        xet-server — a Xet protocol server (CAS API plus a Hugging Face Hub facade).

        Options (each may also be set by the environment variable in brackets):
          --store memory                 Keep everything in memory (the default).   [XET_STORE]
          --store <directory>            Keep everything under a directory.
          --store s3://<bucket>[/prefix] Keep everything in an S3 bucket.
          --urls <urls>                  Where to listen, e.g. http://127.0.0.1:8080. [ASPNETCORE_URLS]
          --public-url <url>             The URL clients reach the server at, when it
                                         differs from the one it listens on.        [XET_PUBLIC_URL]
          --signing-key <base64>         Key for tokens and signed URLs; random when
                                         unset, which suits one process only.       [XET_SIGNING_KEY]
          --hub-token <token>            A Hub token to accept; repeatable.          [XET_HUB_TOKENS, comma-separated]
          --no-anonymous-writes          Require a Hub token to upload or commit.    [XET_ALLOW_ANONYMOUS_WRITES=false]
          --no-anonymous-reads           Require a Hub token to read.                [XET_ALLOW_ANONYMOUS_READS=false]
          --help                         Print this.
        """;

    public static HostSettings? Parse(string[] args)
    {
        var store = Environment.GetEnvironmentVariable("XET_STORE") ?? "memory";
        string? urls = null;
        var publicUrl = Environment.GetEnvironmentVariable("XET_PUBLIC_URL");
        var signingKey = Environment.GetEnvironmentVariable("XET_SIGNING_KEY");
        var hubTokens = (Environment.GetEnvironmentVariable("XET_HUB_TOKENS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var anonymousWrites = Environment.GetEnvironmentVariable("XET_ALLOW_ANONYMOUS_WRITES") is not "false";
        var anonymousReads = Environment.GetEnvironmentVariable("XET_ALLOW_ANONYMOUS_READS") is not "false";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--store" when i + 1 < args.Length:
                    store = args[++i];
                    break;
                case "--urls" when i + 1 < args.Length:
                    urls = args[++i];
                    break;
                case "--public-url" when i + 1 < args.Length:
                    publicUrl = args[++i];
                    break;
                case "--signing-key" when i + 1 < args.Length:
                    signingKey = args[++i];
                    break;
                case "--hub-token" when i + 1 < args.Length:
                    hubTokens.Add(args[++i]);
                    break;
                case "--no-anonymous-writes":
                    anonymousWrites = false;
                    break;
                case "--no-anonymous-reads":
                    anonymousReads = false;
                    break;
                case "--help" or "-h":
                    return null;
                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}'.");
                    return null;
            }
        }

        var options = new XetServerOptions
        {
            PublicBaseUrl = publicUrl is null ? null : new Uri(publicUrl.TrimEnd('/') + "/"),
            SigningKey = signingKey is null ? null : Convert.FromBase64String(signingKey),
            HubTokens = hubTokens,
            AllowAnonymousWrites = anonymousWrites,
            AllowAnonymousReads = anonymousReads,
        };

        return new HostSettings(store, urls, options);
    }

    public (IXetStore Store, IRepositoryStore Repositories, string Description) CreateStores()
    {
        if (Store == "memory")
        {
            var memory = new InMemoryXetStore();
            return (memory, memory, "memory");
        }

        if (Store.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            var location = Store["s3://".Length..].TrimEnd('/');
            var slash = location.IndexOf('/');
            var bucket = slash < 0 ? location : location[..slash];
            var prefix = slash < 0 ? string.Empty : location[(slash + 1)..];
            var s3 = new S3XetStore(new AmazonS3Client(), bucket, prefix);
            return (s3, s3, $"S3 bucket {bucket}" + (prefix.Length > 0 ? $" under {prefix}/" : string.Empty));
        }

        var directory = new FileSystemXetStore(Store);
        return (directory, directory, $"directory {directory.Root}");
    }
}
