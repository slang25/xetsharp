# Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org) suites for the parts of the client that do work per
byte. Everything here runs offline against generated data — no Hub, no network.

```sh
dotnet run --project benchmarks/XetSharp.Benchmarks -c Release -- --filter '*'
dotnet run --project benchmarks/XetSharp.Benchmarks -c Release -- --filter '*Chunking*' --job short
```

Every suite reports an `MB/s` column alongside the usual mean: the payload one invocation processes
divided by how long it took. For the shard suite that payload is the data the shard *describes*
rather than the shard itself, since what matters there is the fixed cost an upload pays per byte
transferred.

| Suite | What it measures |
| --- | --- |
| `GearhashScanBenchmarks` | The chunking boundary scan alone: one byte at a time against the four-at-a-time form the chunker ships. |
| `ChunkingBenchmarks` | Chunking a 32 MiB buffer, allocation and all — random data, float32 weights, and fed in 4 MiB blocks as an upload reads a file. |
| `HashingBenchmarks` | BLAKE3 chunk hashing, and the Merkle aggregations that turn chunk hashes into a file ID. |
| `XorbBenchmarks` | Serializing a xorb (every chunk is compressed twice, so the smaller form can be stored) and reading one back. |
| `PackingBenchmarks` | Packing a xorb at each degree of compression parallelism — compression alone, with the upload loop's hashing, and with its chunking too. The last is what an uploaded byte costs in CPU, and what `MaxCompressionParallelism`'s default was chosen from. |
| `ShardBenchmarks` | Writing and parsing a shard describing 16 xorbs and 32 files. |

## Measuring on a Mac

BenchmarkDotNet gives each benchmark case its own process. On Apple Silicon the scheduler is free to
put that process on an efficiency core — and a process that lands there reads about four times
slower than one that lands on a performance core, which looks exactly like a regression. Runs
launched from a background or low-priority shell are especially prone to it.

Two things help:

- Run from an ordinary interactive terminal, not from a script running in the background.
- Pass `--inProcess` when comparing two implementations against each other. Every case then runs in
  the host process, so whatever core the run gets, both sides get it.

Treat a single run with a large `Error` column as noise rather than as a result.

`PackingBenchmarks` needs more care than the rest, because it is the only suite that uses more than
one core: it competes with whatever else is running for the same cores, so on a busy machine
`--job short` produces a scaling curve that is not even monotonic. Give it a quiet machine and
enough iterations to average the interference out:

```sh
dotnet run --project benchmarks/XetSharp.Benchmarks -c Release -- \
  --filter '*Packing*' --inProcess --warmupCount 3 --iterationCount 15 --invocationCount 1 --unrollFactor 1
```

## Comparing with hf_xet

The reference client is Python over Rust, so there is no in-process comparison to make here. To
compare like for like, time `hf_xet` against the same file on the same machine — its chunker and
BLAKE3 are the parts worth comparing, and both are doing exactly the work `GearhashScanBenchmarks`
and `HashingBenchmarks` measure.
