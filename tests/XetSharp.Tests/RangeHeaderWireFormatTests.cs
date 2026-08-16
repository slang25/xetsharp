using System.Net;
using System.Net.Sockets;
using System.Text;
using XetSharp.Cas;
using XetSharp.Download;

namespace XetSharp.Tests;

/// <summary>
/// The CDN signature on a xorb URL covers the <c>Range</c> header as a string, so what goes over
/// the wire has to be the exact bytes the reconstruction implies — not something HttpClient
/// reformatted on the way out. That is only observable at the socket, so this test reads it there.
/// </summary>
public class RangeHeaderWireFormatTests
{
    [Test]
    public async Task Sends_a_multi_range_header_without_reformatting_it()
    {
        const string expected = "bytes=0-99,200-299";

        var requestLines = await CaptureRequestAsync(async (client, uri) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Range", expected);
            using var response = await client.SendAsync(request);
        });

        var range = requestLines.Single(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
        await Assert.That(range).IsEqualTo($"Range: {expected}");
    }

    /// <summary>The same, but through the fetcher, so the header the pipeline builds is the one checked.</summary>
    [Test]
    public async Task Fetcher_sends_the_range_header_the_reconstruction_implies()
    {
        var fetch = new XorbFetch(
            new Uri("http://placeholder.invalid"),
            [
                new XorbRangeDescriptor(new ChunkRange(0, 2), new ByteRange(0, 50_468)),
                new XorbRangeDescriptor(new ChunkRange(5, 8), new ByteRange(83_002, 128_564)),
            ]);

        var requestLines = await CaptureRequestAsync(async (client, uri) =>
        {
            var fetcher = new XorbRangeFetcher(client, 1);
            try
            {
                await fetcher.FetchAsync(fetch with { Url = uri }, CancellationToken.None);
            }
            catch (Exception exception) when (exception is InvalidDataException or HttpRequestException)
            {
                // The stub answers with an empty body; only the request matters here.
            }
        });

        var range = requestLines.Single(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
        await Assert.That(range).IsEqualTo("Range: bytes=0-50468,83002-128564");
    }

    /// <summary>Runs <paramref name="send"/> against a socket that records the request it receives.</summary>
    private static async Task<string[]> CaptureRequestAsync(Func<HttpClient, Uri, Task> send)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync();
            await using var stream = connection.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer);
            await stream.WriteAsync("HTTP/1.1 206 Partial Content\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
            return Encoding.ASCII.GetString(buffer, 0, read);
        });

        using var client = new HttpClient();
        await send(client, new Uri($"http://127.0.0.1:{port}/xorb"));

        var request = await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        return [.. request.Split("\r\n").TakeWhile(line => line.Length > 0)];
    }
}
