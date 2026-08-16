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

Uploading is not implemented yet — see [PLAN.md](PLAN.md).

## Layout

- `src/XetSharp` — the client library
- `tests/XetSharp.Tests` — verification suite ([TUnit](https://tunit.dev)), including cross-checks against the [xet-core](https://github.com/huggingface/xet-core) reference implementation

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
