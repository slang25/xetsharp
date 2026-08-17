# XetSharp

A modern, high-performance, idiomatic C# client for the [Xet protocol](https://huggingface.co/docs/xet/main/en/index) — Hugging Face's content-addressed storage system for large files.

> **Status:** early development. See [PLAN.md](PLAN.md) for the roadmap.

## What is Xet?

Xet replaces Git LFS on the Hugging Face Hub. Files are split into variable-sized chunks via content-defined chunking, deduplicated, grouped into ~64 MiB *xorbs*, and described by *shards*. Clients talk to a CAS (content-addressed storage) service to upload and download data, transferring only chunks the other side doesn't already have.

XetSharp implements the [Xet protocol specification](https://huggingface.co/docs/xet/main/en/index) (v1.1.0) for .NET.

## Downloading a file

```csharp
using XetSharp;
using XetSharp.Hub;

using var client = new XetClient();

// Public repositories need no credentials; otherwise set HF_TOKEN or XetClientOptions.HubToken.
var result = await client.DownloadToFileAsync(
    XetRepository.Model("openai-community/gpt2"),
    "model.safetensors",
    "model.safetensors");

Console.WriteLine($"{result.BytesWritten} bytes, verified as {result.FileHash}");
```

A whole-file download is checked twice on the way past: the chunks that arrive are re-aggregated
into a file hash and compared with the file ID they were requested by, and the bytes are checked
against the SHA-256 the Hub records. A failure throws rather than returning a plausible file.

Part of a file, when you don't want all of it:

```csharp
using var destination = new MemoryStream();
await client.DownloadRangeAsync(repository, "model.safetensors", destination, offset: 1_000_000, length: 250_000);
```

## Uploading a file

```csharp
using var client = new XetClient(); // needs a Hub token with write access to the repository

var result = await client.UploadAndCommitAsync(
    XetRepository.Model("you/your-model"),
    [XetUploadFile.FromFile("model.safetensors")],
    summary: "Add the weights");

Console.WriteLine($"{result.Files[0].FileId} in {result.XorbCount} xorbs, " +
                  $"{result.DeduplicatedBytes} bytes already stored");
```

The file is chunked, deduplicated against what this upload has already seen and against the CAS
service's global index, packed into xorbs and registered with a shard — and then committed as an
LFS pointer, which is what makes a Git-backed repository show it. Storing the bytes and committing
them are separate steps, so `UploadAsync` and `CommitAsync` are available on their own.

Most of what an upload costs in CPU is compressing chunks, so up to four of them compress at once by
default — one per processor, never more than four — and the packed xorb is assembled in the order the
chunks were added, the same bytes a single-threaded pack would produce, about 2.3x sooner.
`XetUploadOptions.MaxCompressionParallelism` sets the number; 1 keeps the thread pool out of it
entirely.

Uploading several files at once packs their chunks together and publishes them in one commit:

```csharp
await client.UploadAndCommitAsync(
    repository,
    [XetUploadFile.FromFile("model.safetensors"), XetUploadFile.FromFile("tokenizer.json")],
    summary: "Add the model");
```

## Watching a transfer

Any download or upload takes an `IProgress<XetProgress>`. Progress is reported about once a
megabyte, and once more when the transfer finishes, so a fast link does not drown the consumer:

```csharp
var progress = new Progress<XetProgress>(p =>
    Console.WriteLine($"{p.BytesTransferred:N0} bytes{(p.Fraction is { } done ? $" ({done:P0})" : "")}"));

await client.DownloadToFileAsync(repository, "model.safetensors", "model.safetensors", progress);
```

`TotalBytes` is null when nothing can say how big the transfer is — uploading from a stream that
cannot be measured, for instance — and `Fraction` is null with it, rather than guessing.

For a log, hand the client a logger factory. The library depends on the logging *abstractions* only
and defaults to `NullLogger`, so nothing is formatted unless someone is listening:

```csharp
using var client = new XetClient(new XetClientOptions { LoggerFactory = loggerFactory });
```

Transfers are reported at Information, requests, tokens and xorbs at Debug, and retried requests at
Warning.

## Dependency injection

`XetSharp.Extensions.DependencyInjection` registers a client over `IHttpClientFactory`, wired to the
application's logging and `TimeProvider`:

```csharp
services.AddXetClient(options => options with { HubToken = configuration["HuggingFace:Token"] });
```

The call returns the `IHttpClientBuilder` for the named client behind it, so handlers, resilience or
a different primary handler can be added the usual way. It is a separate package on purpose: the
core library needs nothing from `Microsoft.Extensions.*` beyond `ILogger`.

## Layout

- `src/XetSharp` — the client library
- `src/XetSharp.Extensions.DependencyInjection` — `AddXetClient` for applications using DI
- `tests/XetSharp.Tests` — verification suite ([TUnit](https://tunit.dev)), including cross-checks against the [xet-core](https://github.com/huggingface/xet-core) reference implementation
- `benchmarks/XetSharp.Benchmarks` — [BenchmarkDotNet](https://benchmarkdotnet.org) suites for the per-byte work ([how to run](benchmarks/README.md))

## Building

Requires the .NET 10 SDK.

```sh
dotnet build
dotnet run --project tests/XetSharp.Tests
```

A handful of tests need reference artefacts too large to vendor (a 63 MB CSV and the 14 MB xorb
built from it). They skip unless you point at a local copy of the dataset:

```sh
hf download xet-team/xet-spec-reference-files --repo-type dataset --local-dir /tmp/xet-reference
XETSHARP_REFERENCE_DIR=/tmp/xet-reference dotnet run --project tests/XetSharp.Tests
```

A few more talk to the real Hub, downloading public files and byte-comparing them against the same
files fetched over plain HTTP. They need no credentials, but they do need a network, so they are
opt-in too:

```sh
XETSHARP_LIVE_TESTS=1 dotnet run --project tests/XetSharp.Tests
```

The live *upload* tests write to a real repository, so they need one you are happy to have scribbled
on and a Hub token with write access to it. They clean up after themselves:

```sh
XETSHARP_LIVE_UPLOAD_REPO=you/xetsharp-scratch dotnet run --project tests/XetSharp.Tests
```
