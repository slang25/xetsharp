using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using XetSharp.Cas;
using XetSharp.Download;
using XetSharp.Hub;

namespace XetSharp.Benchmarks;

/// <summary>
/// The one measurement here that needs a network: how download throughput responds to
/// <see cref="XetDownloadOptions.MaxConcurrentDownloads"/> against the real Hub, CAS service and CDN.
/// </summary>
/// <remarks>
/// A stand-in service cannot answer this. The two things transfer concurrency exists to hide — the
/// round trip before a range starts arriving, and whatever ceiling one connection runs into — are
/// exactly the two things a loopback stand-in does not have, so tuning against one measures the
/// stand-in. Everything read here is public, so no credentials are needed; nothing is written.
/// </remarks>
internal static class LiveTransferSweep
{
    private const string Usage = """
        usage: dotnet run --project benchmarks/XetSharp.Benchmarks -c Release -- sweep [options]

          --repo <id>            repository to read from (default openai-community/gpt2)
          --type model|dataset   repository type (default model)
          --file <path>          file within the repository (default model.safetensors)
          --bytes <MiB>          how much of the file each run downloads (default 256)
          --concurrency <list>   the MaxConcurrentDownloads values to compare (default 1,2,4,8)
          --buffer <MiB>         MaxBufferedBytes (default 128, the shipping default)
          --rounds <n>           timed rounds per setting, interleaved (default 3)
          --plan                 print the reconstruction's shape and stop, transferring nothing
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        SweepOptions options;
        try
        {
            options = SweepOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var repository = options.IsDataset ? XetRepository.Dataset(options.Repository) : XetRepository.Model(options.Repository);

        using var probe = new XetClient();
        var file = await probe.GetFileInfoAsync(repository, options.Path).ConfigureAwait(false);

        // Staying short of the whole file keeps this a range download, which skips the file-hash and
        // SHA-256 checks — they are real work, but they are not what is being compared here.
        var length = file.Size > 0 ? Math.Min(options.Bytes, file.Size - 1) : options.Bytes;
        var reconstruction = await probe.Cas
            .GetReconstructionAsync(repository, file.FileId, new ByteRange(0, length - 1))
            .ConfigureAwait(false);

        PrintPlan(repository, options, file, length, reconstruction);
        if (options.PlanOnly)
        {
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"Baseline: one plain HTTP connection to the Hub's ordinary download route.");
        var baseline = await TimePlainHttpAsync(repository, options.Path, length).ConfigureAwait(false);
        Console.WriteLine($"  {baseline:N0} MB/s");
        Console.WriteLine();

        var clients = new Dictionary<int, XetClient>();
        var results = new Dictionary<int, List<double>>();
        try
        {
            foreach (var concurrency in options.Concurrency)
            {
                clients[concurrency] = new XetClient(new XetClientOptions
                {
                    Download = XetDownloadOptions.Default with
                    {
                        MaxConcurrentDownloads = concurrency,
                        MaxBufferedBytes = options.BufferBytes,
                    },
                });

                results[concurrency] = [];

                // Untimed: mints this client's token, opens its connections and JITs the path, none
                // of which the setting under test is responsible for.
                await clients[concurrency]
                    .DownloadAsync(repository, file, Stream.Null, 0, 8L * 1024 * 1024)
                    .ConfigureAwait(false);
            }

            // Interleaved rather than all rounds of one setting then all of the next, so a link that
            // slows down halfway through spreads the damage rather than condemning one setting.
            for (var round = 1; round <= options.Rounds; round++)
            {
                foreach (var concurrency in options.Concurrency)
                {
                    var throughput = await TimeAsync(clients[concurrency], repository, file, length).ConfigureAwait(false);
                    results[concurrency].Add(throughput);
                    Console.WriteLine($"  round {round}, concurrency {concurrency,2}: {throughput,7:N0} MB/s");
                }
            }
        }
        finally
        {
            foreach (var client in clients.Values)
            {
                client.Dispose();
            }
        }

        PrintResults(options, results, baseline);
        return 0;
    }

    private static void PrintPlan(
        XetRepository repository,
        SweepOptions options,
        XetFileInfo file,
        long length,
        FileReconstruction reconstruction)
    {
        var fetches = reconstruction.Xorbs.Values.SelectMany(list => list).ToArray();
        var transferred = fetches.Sum(fetch => fetch.ByteCount);

        Console.WriteLine($"{repository.Id}/{options.Path}: {file.Size:N0} bytes, file ID {file.FileId}");
        Console.WriteLine($"Downloading the first {length:N0} bytes per run.");
        Console.WriteLine($"  terms:   {reconstruction.Terms.Count:N0}");
        Console.WriteLine($"  xorbs:   {reconstruction.Xorbs.Count:N0}");
        Console.WriteLine($"  fetches: {fetches.Length:N0}, {transferred:N0} bytes over the wire");

        if (fetches.Length > 0)
        {
            var sizes = fetches.Select(fetch => fetch.ByteCount).Order().ToArray();
            Console.WriteLine($"  fetch size: min {sizes[0]:N0}, median {sizes[sizes.Length / 2]:N0}, max {sizes[^1]:N0} bytes");

            // A fetch only starts once the budget has room for it, so this is the ceiling on what
            // MaxConcurrentDownloads can actually reach for this file at this buffer size.
            var budgeted = Math.Max(1, (int)(options.BufferBytes / Math.Max(1, sizes[sizes.Length / 2])));
            Console.WriteLine(
                $"  at a {options.BufferBytes / (1024 * 1024)} MiB buffer, about {budgeted} median-sized " +
                $"fetches fit in flight; there are {fetches.Length:N0} to issue in total.");
        }
    }

    private static void PrintResults(SweepOptions options, Dictionary<int, List<double>> results, double baseline)
    {
        Console.WriteLine();
        Console.WriteLine($"| MaxConcurrentDownloads | best MB/s | median MB/s | vs. 1 |");
        Console.WriteLine($"| --- | --- | --- | --- |");

        var single = results.TryGetValue(1, out var first) && first.Count > 0 ? Median(first) : (double?)null;
        foreach (var concurrency in options.Concurrency)
        {
            var runs = results[concurrency];
            var median = Median(runs);
            var relative = single is { } baselineMedian and > 0
                ? (median / baselineMedian).ToString("0.00x", CultureInfo.InvariantCulture)
                : "-";
            Console.WriteLine($"| {concurrency} | {runs.Max():N0} | {median:N0} | {relative} |");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"One plain HTTP connection managed {baseline:N0} MB/s over the same bytes, " +
            $"buffer {options.BufferBytes / (1024 * 1024)} MiB, {options.Rounds} rounds.");
    }

    private static async Task<double> TimeAsync(XetClient client, XetRepository repository, XetFileInfo file, long length)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await client.DownloadAsync(repository, file, Stream.Null, 0, length).ConfigureAwait(false);
        stopwatch.Stop();

        if (result.BytesWritten != length)
        {
            throw new InvalidOperationException($"Expected {length} bytes, got {result.BytesWritten}.");
        }

        return result.BytesWritten / 1e6 / stopwatch.Elapsed.TotalSeconds;
    }

    /// <summary>The same bytes over the Hub's non-Xet route, on one connection: what the link can do.</summary>
    private static async Task<double> TimePlainHttpAsync(XetRepository repository, string path, long length)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var url = $"https://huggingface.co/{repository.ResolvePrefix}{repository.Id}/resolve/{repository.Revision}/{path}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, length - 1);

        var stopwatch = Stopwatch.StartNew();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            await body.CopyToAsync(Stream.Null).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return length / 1e6 / stopwatch.Elapsed.TotalSeconds;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private sealed record SweepOptions
    {
        public string Repository { get; init; } = "openai-community/gpt2";

        public bool IsDataset { get; init; }

        public string Path { get; init; } = "model.safetensors";

        public long Bytes { get; init; } = 256L * 1024 * 1024;

        public long BufferBytes { get; init; } = XetDownloadOptions.Default.MaxBufferedBytes;

        public int[] Concurrency { get; init; } = [1, 2, 4, 8];

        public int Rounds { get; init; } = 3;

        public bool PlanOnly { get; init; }

        public static SweepOptions Parse(string[] args)
        {
            var options = new SweepOptions();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--plan":
                        options = options with { PlanOnly = true };
                        continue;
                    case "--repo":
                        options = options with { Repository = Next(args, ref i) };
                        continue;
                    case "--type":
                        var type = Next(args, ref i);
                        options = type switch
                        {
                            "dataset" => options with { IsDataset = true },
                            "model" => options with { IsDataset = false },
                            _ => throw new ArgumentException($"Unknown repository type '{type}'; expected 'model' or 'dataset'."),
                        };
                        continue;
                    case "--file":
                        options = options with { Path = Next(args, ref i) };
                        continue;
                    case "--bytes":
                        options = options with { Bytes = ParseMebibytes(Next(args, ref i)) };
                        continue;
                    case "--buffer":
                        options = options with { BufferBytes = ParseMebibytes(Next(args, ref i)) };
                        continue;
                    case "--rounds":
                        options = options with { Rounds = ParseCount(Next(args, ref i)) };
                        continue;
                    case "--concurrency":
                        options = options with { Concurrency = [.. Next(args, ref i).Split(',').Select(ParseCount)] };
                        continue;
                    default:
                        throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }

            if (options.Concurrency.Length == 0)
            {
                throw new ArgumentException("--concurrency needs at least one value.");
            }

            return options;
        }

        private static string Next(string[] args, ref int index) =>
            ++index < args.Length ? args[index] : throw new ArgumentException($"'{args[index - 1]}' needs a value.");

        private static long ParseMebibytes(string value) =>
            long.TryParse(value, CultureInfo.InvariantCulture, out var mebibytes) && mebibytes > 0
                ? mebibytes * 1024 * 1024
                : throw new ArgumentException($"'{value}' is not a positive number of MiB.");

        private static int ParseCount(string value) =>
            int.TryParse(value, CultureInfo.InvariantCulture, out var count) && count > 0
                ? count
                : throw new ArgumentException($"'{value}' is not a positive count.");
    }
}
