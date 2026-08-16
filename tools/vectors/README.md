# Test-vector generator

A small Rust harness that generates reference hash vectors for the XetSharp verification suite
using the protocol's BLAKE3 keys directly (no xet-core dependency). The chunk-boundary and
Merkle-aggregation vectors in the test suite are instead ported verbatim from xet-core's own
test suite (`xet_data/src/deduplication/chunking.rs` and
`xet_core_structures/src/merklehash/aggregated_hashes.rs`), which publishes them explicitly
for cross-implementation verification.

```sh
cargo run --release
```

Output values are already committed into `tests/XetSharp.Tests/ChunkHashTests.cs`; rerun only
if you need to extend the vector set.
