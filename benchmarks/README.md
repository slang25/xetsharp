# Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org) suites for the parts of the client that do work per
byte. Every suite runs offline against generated data — no Hub, no network. The one exception is the
[transfer sweep](#measuring-a-real-link), which is not a BenchmarkDotNet suite at all: it measures a
network, and there is nothing offline for it to measure.

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

## Measuring a real link

`sweep` is the odd one out: it downloads the same bytes from the real Hub several times over, once
per `MaxConcurrentDownloads` setting, and prints what each one achieved. Transfer concurrency is
there to hide two things a stand-in service does not have — the round trip before a range starts
arriving, and whatever ceiling one connection runs into — so tuning it against `FakeCas` would
measure `FakeCas`. Everything it reads is public: no token, and nothing is written.

```sh
# What the download would look like, without transferring anything.
dotnet run --project benchmarks/XetSharp.Benchmarks -c Release -- sweep --plan

# The sweep itself: 256 MiB of gpt2's weights per run, four settings, three interleaved rounds.
dotnet run --project benchmarks/XetSharp.Benchmarks -c Release -- sweep \
  --bytes 256 --concurrency 1,2,4,8 --buffer 1024 --rounds 3
```

`--plan` is worth running first, because it prints the thing that decides whether a sweep can say
anything at all: how many fetches the range breaks into and how big they are. A file stored as full
xorbs comes back as ~56 MB fetches, and 256 MiB of it is only five of them — so a setting of 8 has
nothing to be 8 of. It also prints how many of those fit in `MaxBufferedBytes`, which is what
actually bounds the requests in flight: at the shipping 128 MiB budget, two.

Rounds are interleaved rather than run setting by setting, so a link that slows down halfway through
spreads the damage instead of condemning one setting. Each client warms up untimed first, to keep
its token mint and connection setup out of the numbers. A single plain HTTP download of the same
bytes is timed alongside, as the link's own ceiling.

### What it said here

On a domestic link that tops out around 250 Mbit/s, 256 MiB of `openai-community/gpt2`'s
`model.safetensors`, three rounds per setting:

| MaxConcurrentDownloads | 1 | 2 | 4 | 8 |
| --- | --- | --- | --- | --- |
| median MB/s, 1024 MiB buffer | 30 | 30 | 31 | 29 |
| median MB/s, 128 MiB buffer (the default) | 23 | — | — | 30 |

One plain HTTP connection managed 23–27 MB/s over the same bytes, and no run of any setting left the
21–33 MB/s band. **This link is the ceiling, and one request in flight already reaches it** — which
is a real answer to "should the client ramp concurrency up and down", just not the interesting one.
A measurement that cannot separate 1 from 8 cannot justify building something to choose between
them, so the fixed defaults stay, and the tool is here for whoever next runs it somewhere with a
fatter pipe (a cloud VM in the same region as the CDN would settle it in a minute).

Treat one run as noise: the spread between rounds of the *same* setting reached 30% here, which is
larger than any difference between settings.

## Comparing with hf_xet

The reference client is Python over Rust, so there is no in-process comparison to make here. To
compare like for like, time `hf_xet` against the same file on the same machine — its chunker and
BLAKE3 are the parts worth comparing, and both are doing exactly the work `GearhashScanBenchmarks`
and `HashingBenchmarks` measure.
