# Reference files

Golden files vendored from the [`xet-team/xet-spec-reference-files`](https://huggingface.co/datasets/xet-team/xet-spec-reference-files)
dataset (Apache-2.0, revision `c4aa3a3f15b1395fff5ce934784bf6c8f2d62de8`), renamed for brevity. They
describe one real file processed by the reference implementation:
`Electric_Vehicle_Population_Data_20250917.csv` — 63,527,244 bytes, 796 chunks, all in a single xorb.

| File here | Upstream name | Notes |
|---|---|---|
| `ev-population.csv.chunks` | `Electric_Vehicle_Population_Data_20250917.csv.chunks` | `<chunk hash> <length>` per line, in file order |
| `ev-population.csv.shard` | `…csv.shard` | Base shard: metadata-ext flag only, no verification entries, with footer |
| `ev-population.csv.shard.verification` | `…csv.shard.verification` | With verification entries and footer |
| `ev-population.csv.shard.verification-no-footer` | `…csv.shard.verification-no-footer` | The upload wire form (footer truncated) |
| `ev-population.csv.shard.dedupe` | `…csv.shard.dedupe` | Global-dedupe response form: empty file info, HMAC-keyed chunk hashes |
| `ev-population.xorb.first-10-chunks` | `eea25d…7632.xorb` (truncated) | First 170,626 bytes — chunk records 0–9 exactly |

Known constants asserted by the tests (from the upstream `.xet-file-hash`, `.xet-xorb-hash` and
`.xorb.range-hash` files) are inlined in the test source rather than vendored as one-line files.

The full xorb is 14,737,817 bytes, too large to vendor; only the prefix covering the first ten chunk
records is kept. Everything the whole-xorb artefacts prove about *hashing* is already provable from
`ev-population.csv.chunks`, which lists every chunk hash and length.

## Two things the reference bytes settle that the prose spec leaves ambiguous

- Compression scheme `1` ("LZ4") is the LZ4 **frame** format, not a bare LZ4 block: every chunk
  payload in the reference xorb starts with the frame magic `04 22 4d 18`.
- The reference xorb has **four trailing bytes** (`XETB`) after the last complete chunk record, which
  no version of the spec describes. Readers must stop at the end of the last complete record rather
  than assuming the data ends exactly on a record boundary.

To refresh these files:

```sh
hf download xet-team/xet-spec-reference-files --repo-type dataset --local-dir <dir>
```
