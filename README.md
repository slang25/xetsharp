# XetSharp

A modern, high-performance, idiomatic C# client for the [Xet protocol](https://huggingface.co/docs/xet/main/en/index) — Hugging Face's content-addressed storage system for large files.

> **Status:** early development. See [PLAN.md](PLAN.md) for the roadmap.

## What is Xet?

Xet replaces Git LFS on the Hugging Face Hub. Files are split into variable-sized chunks via content-defined chunking, deduplicated, grouped into ~64 MiB *xorbs*, and described by *shards*. Clients talk to a CAS (content-addressed storage) service to upload and download data, transferring only chunks the other side doesn't already have.

XetSharp implements the [Xet protocol specification](https://huggingface.co/docs/xet/main/en/index) (v1.1.0) for .NET.

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
