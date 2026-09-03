using System.Diagnostics;
using System.Security.Cryptography;
using XetSharp.Hub;
using XetSharp.Upload;

namespace XetSharp.Server.Tests;

/// <summary>
/// The official client against this server: <c>huggingface_hub</c> driving the Rust <c>hf_xet</c>
/// engine, which is the reference implementation of the protocol. Nothing here is a
/// self-consistency check — the other side of every transfer is code this repository did not
/// write. Opt-in through <c>XETSHARP_INTEROP_TESTS=1</c>; needs <c>uv</c>.
/// </summary>
public class PythonInteropTests
{
    private const string Repository = "acme/interop";

    [Test]
    [SkipWithoutInteropTests]
    public async Task Official_python_client_downloads_what_xetsharp_uploaded()
    {
        await using var host = await KestrelXetServer.StartAsync();
        using var client = host.CreateClient();
        var content = TestData.SplitMix64Bytes(1, 3_000_000);
        await client.UploadAndCommitAsync(XetRepository.Model(Repository), [XetUploadFile.FromBytes("weights.bin", content)], "Add weights");

        var cache = NewDirectory();
        try
        {
            var output = await RunPythonAsync(host.Url, DownloadScript, [Repository, "weights.bin", "model", cache]);

            await Assert.That(output).Contains("XET True");
            await Assert.That(output).Contains($"SHA256 {Convert.ToHexStringLower(SHA256.HashData(content))}");
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    [SkipWithoutInteropTests]
    public async Task Official_python_client_uploads_and_xetsharp_downloads()
    {
        await using var host = await KestrelXetServer.StartAsync();
        var content = TestData.SplitMix64Bytes(2, 3_000_000);
        var directory = NewDirectory();
        try
        {
            var source = Path.Combine(directory, "source.bin");
            await File.WriteAllBytesAsync(source, content);

            var output = await RunPythonAsync(host.Url, UploadScript, [source, "data/weights.bin", Repository, "model"]);
            await Assert.That(output).Contains("XET True");
            await Assert.That(output).Contains("COMMIT ");

            using var client = host.CreateClient();
            using var downloaded = new MemoryStream();
            var download = await client.DownloadAsync(XetRepository.Model(Repository), "data/weights.bin", downloaded);

            await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
            await Assert.That(download.Sha256).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(content)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The same bytes twice from the official client: the second upload should find them through
    /// the global-deduplication index and store nothing new, which exercises the HMAC-keyed shard
    /// response against the reference implementation's matching.
    /// </summary>
    [Test]
    [SkipWithoutInteropTests]
    public async Task Official_python_client_deduplicates_against_the_global_index()
    {
        await using var host = await KestrelXetServer.StartAsync();
        var content = TestData.WithEligibleChunk(3, 2_000_000);
        var directory = NewDirectory();
        try
        {
            var source = Path.Combine(directory, "source.bin");
            await File.WriteAllBytesAsync(source, content);

            await RunPythonAsync(host.Url, UploadScript, [source, "one.bin", Repository, "model"]);
            await RunPythonAsync(host.Url, UploadScript, [source, "two.bin", "acme/second", "model"]);

            // The second file's reconstruction must reach into the first upload's xorb: the client
            // asked the index about a sampled chunk, matched the keyed listing it got back, and
            // referenced what was already stored instead of packing it again.
            var first = await XorbsOfAsync(host.Server, Repository, "one.bin");
            var second = await XorbsOfAsync(host.Server, "acme/second", "two.bin");
            await Assert.That(second.Intersect(first)).IsNotEmpty();

            using var client = host.CreateClient();
            using var downloaded = new MemoryStream();
            await client.DownloadAsync(XetRepository.Model("acme/second"), "two.bin", downloaded);
            await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<HashSet<MerkleHash>> XorbsOfAsync(XetServer server, string repository, string path)
    {
        var (ns, name) = (repository.Split('/')[0], repository.Split('/')[1]);
        var revision = await server.Repositories.GetRevisionAsync(new Storage.RepositoryId("model", ns, name), "main");
        var file = await server.Store.GetFileAsync(revision!.Files[path].FileId);
        return file!.Terms.Select(term => term.Xorb).ToHashSet();
    }

    private const string DownloadScript = """
        import hashlib, sys
        import huggingface_hub.file_download as fd
        from huggingface_hub import hf_hub_download

        used = {"xet": False}
        original = fd.xet_get
        def spy(**kwargs):
            used["xet"] = True
            return original(**kwargs)
        fd.xet_get = spy

        path = hf_hub_download(repo_id=sys.argv[1], filename=sys.argv[2], repo_type=sys.argv[3], cache_dir=sys.argv[4])
        with open(path, "rb") as f:
            print("SHA256", hashlib.sha256(f.read()).hexdigest())
        print("XET", used["xet"])
        """;

    private const string UploadScript = """
        import os, sys
        import huggingface_hub._commit_api as commit_api
        from huggingface_hub import HfApi

        used = {"xet": False}
        original = commit_api._upload_xet_files
        def spy(**kwargs):
            used["xet"] = True
            return original(**kwargs)
        commit_api._upload_xet_files = spy

        api = HfApi(endpoint=os.environ["HF_ENDPOINT"], token="hf_interop")
        info = api.upload_file(path_or_fileobj=sys.argv[1], path_in_repo=sys.argv[2], repo_id=sys.argv[3], repo_type=sys.argv[4], commit_message="Add from python")
        print("COMMIT", info.oid)
        print("XET", used["xet"])
        """;

    private static async Task<string> RunPythonAsync(Uri endpoint, string script, string[] arguments)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"xetsharp-interop-{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var start = new ProcessStartInfo("uv")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "run", "--quiet", "--with", "huggingface_hub[hf_xet]", "python", scriptPath }.Concat(arguments))
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["HF_ENDPOINT"] = endpoint.ToString().TrimEnd('/');
            start.Environment["HF_HOME"] = Path.Combine(Path.GetTempPath(), "xetsharp-interop-hf-home");
            start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            start.Environment["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";
            start.Environment.Remove("HF_TOKEN");

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);

            var output = await stdout;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The Python client exited with {process.ExitCode}.\n{output}\n{await stderr}");
            }

            return output + await stderr;
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "xetsharp-interop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
