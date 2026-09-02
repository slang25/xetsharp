using Amazon.Runtime;
using Amazon.S3;
using Testcontainers.Floci;
using XetSharp.Hub;
using XetSharp.Server.Storage.S3;
using XetSharp.Upload;

namespace XetSharp.Server.Tests;

/// <summary>
/// The S3 store against a real S3 API. By default that is <a href="https://github.com/floci-io/floci">Floci</a>,
/// a local AWS emulator started in a container for the test; set <c>XETSHARP_S3_ENDPOINT</c> to
/// point at something already running instead. Opt-in with <c>XETSHARP_S3_TESTS=1</c>, because
/// it needs Docker. The client talks to the server on a real port and fetches xorb data from the
/// presigned URLs the reconstruction hands out, which never touch the server.
/// </summary>
public class S3StoreTests
{
    private const string FlociImage = "floci/floci:2.0.1";

    private static readonly XetRepository Repository = XetRepository.Model("acme/s3");

    [Test]
    [SkipWithoutS3Tests]
    public async Task Round_trips_through_s3_with_presigned_xorb_urls()
    {
        await using var s3 = await S3Endpoint.StartAsync();
        var bucket = Environment.GetEnvironmentVariable("XETSHARP_S3_BUCKET") ?? "xetsharp-tests";
        try
        {
            await s3.Client.PutBucketAsync(bucket);
        }
        catch (AmazonS3Exception exception) when (exception.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }

        var store = new S3XetStore(s3.Client, bucket, $"run-{Guid.NewGuid():N}");
        await using var host = await KestrelXetServer.StartAsync(store, store);
        using var xet = host.CreateClient();
        var content = TestData.WithEligibleChunk(50, 1_500_000);

        var upload = await xet.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)], "Add");
        var registered = await store.GetFileAsync(upload.Files.Single().FileId);
        await Assert.That(registered).IsNotNull();
        await Assert.That(await store.GetXorbAsync(registered!.Terms[0].Xorb)).IsNotNull();

        // The reconstruction sends the client straight to S3.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var cas = new XetSharp.Cas.CasClient(http, new XetTokenProvider(new HubClient(http, new Uri(host.Url + "/"))));
        var reconstruction = await cas.GetReconstructionAsync(Repository, upload.Files.Single().FileId);
        var urls = reconstruction.Xorbs.Values.SelectMany(fetches => fetches).Select(fetch => fetch.Url.ToString()).ToList();
        await Assert.That(urls.All(url => url.StartsWith(s3.Url.TrimEnd('/')))).IsTrue().Because(string.Join("\n", urls));

        using var downloaded = new MemoryStream();
        await xet.DownloadAsync(Repository, "data.bin", downloaded);
        await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);

        using var range = new MemoryStream();
        await xet.DownloadRangeAsync(Repository, "data.bin", range, 700_000, 100_000);
        await Assert.That(range.ToArray()).IsEquivalentTo(content.AsSpan(700_000, 100_000).ToArray(), CollectionOrdering.Matching);

        // The global index lives in the bucket too.
        var again = await xet.UploadAsync(XetRepository.Model("acme/s3-again"), [XetUploadFile.FromBytes("data.bin", content)]);
        await Assert.That(again.DeduplicatedBytes).IsGreaterThan(0);
    }

    /// <summary>An S3 endpoint and a client for it: Floci in a container, or whatever <c>XETSHARP_S3_ENDPOINT</c> names.</summary>
    private sealed class S3Endpoint(string url, IAmazonS3 client, FlociContainer? container) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public IAmazonS3 Client { get; } = client;

        public static async Task<S3Endpoint> StartAsync()
        {
            FlociContainer? container = null;
            var url = Environment.GetEnvironmentVariable("XETSHARP_S3_ENDPOINT");
            if (string.IsNullOrEmpty(url))
            {
                container = new FlociBuilder(FlociImage).Build();
                await container.StartAsync();
                url = container.GetConnectionString();
            }

            var client = new AmazonS3Client(
                new BasicAWSCredentials(
                    Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ?? FlociBuilder.AccessKey,
                    Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ?? FlociBuilder.SecretKey),
                new AmazonS3Config { ServiceURL = url, ForcePathStyle = true, AuthenticationRegion = FlociBuilder.Region });
            return new S3Endpoint(url, client, container);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }
    }
}

public sealed class SkipWithoutS3TestsAttribute()
    : SkipAttribute("XETSHARP_S3_TESTS is not set; the S3 store tests start Floci in Docker and are opt-in.")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
    {
        var enabled = Environment.GetEnvironmentVariable("XETSHARP_S3_TESTS") is { Length: > 0 } value && value is not ("0" or "false" or "False");
        return Task.FromResult(!enabled && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XETSHARP_S3_ENDPOINT")));
    }
}
