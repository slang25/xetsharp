# Xet Protocol: Chunking, Hashing, and Deduplication — Technical Digest for a C# Client

Sources (fetched 2026-08-16):

- Spec: https://huggingface.co/docs/xet/main/en/chunking
- Spec: https://huggingface.co/docs/xet/main/en/hashing
- Spec: https://huggingface.co/docs/xet/main/en/deduplication
- Reference impl (pinned commit referenced by spec): https://github.com/huggingface/xet-core @ `c3c726bed5cf54ded92a63fab892cfb7857c751a`
- Gearhash table: https://github.com/srijs/rust-gearhash @ `adad44e7141cfd29d898cf6e0858f50b995db286`

---

## 1. Content-Defined Chunking (Gearhash CDC)

### 1.1 Constants

| Constant | Value | Notes |
|---|---|---|
| `TARGET_CHUNK_SIZE` | 64 KiB (65536) | must be a power of 2, > 64, < 2^32 |
| `MIN_CHUNK_SIZE` | 8 KiB (8192) | = TARGET / `MINIMUM_CHUNK_DIVISOR` (8) |
| `MAX_CHUNK_SIZE` | 128 KiB (131072) | = TARGET * `MAXIMUM_CHUNK_MULTIPLIER` (2) |
| `MASK` | `0xFFFF_0000_0000_0000` | 16 one-bits → boundary probability 1/2^16 per byte |
| `HASH_WINDOW_SIZE` | 64 bytes | effective gear window (u64 shifted left 1/byte) |
| `TABLE[256]` | 256 x u64 | rust-gearhash `DEFAULT_TABLE` (see 1.6) |

Mask derivation in xet-core (`Chunker::new`): `mask = (target_chunk_size - 1) as u64; mask <<= mask.leading_zeros();` → for 65536: `0xFFFF << 48 = 0xFFFF000000000000`. Matches the spec's literal MASK.

### 1.2 Per-byte update rule

For each input byte `b`, with 64-bit **wrapping** arithmetic:

```text
h = (h << 1) + TABLE[b]     // u64 wrapping: h = unchecked((h << 1) + TABLE[b])
```

`h` initialized to 0; **reset to 0 after every emitted boundary** (only on boundary — this keeps streaming chunking stable).

### 1.3 Boundary rule

At each position after updating `h`, let `size = current_offset - start_offset + 1`:

- If `size < MIN_CHUNK_SIZE`: do NOT test the mask; continue.
- Else if `size >= MAX_CHUNK_SIZE`: force a boundary (MUST, even if `(h & MASK) != 0`).
- Else if `(h & MASK) == 0`: boundary at this position.

On boundary: emit chunk `[start_offset, current_offset + 1)`, set `start_offset = current_offset + 1`, reset `h = 0`.
At EOF: if `start_offset < len(data)`, emit final chunk `[start_offset, len(data))`.
Tiny files: if `len(data) < MIN_CHUNK_SIZE`, the whole file is one chunk.

### 1.4 Spec pseudocode (verbatim)

```text
Inputs: (See above for constant parameters)
  data: byte array

State:
  h = 0
  start_offset = 0 // start of the "current chunk"

if len(data) < MIN_CHUNK_SIZE:
  emit chunk [0, len(data))
  done

for i in range(0, len(data)):
  b = data[i]
  h = (h << 1) + TABLE[b]      // 64-bit wrapping
  size = i + 1 - start_offset

  if size < MIN_CHUNK_SIZE:
    continue

  if size >= MAX_CHUNK_SIZE or (h & MASK) == 0:
    emit chunk [start_offset, i + 1)
    start_offset = i + 1
    h = 0

if start_offset < len(data):
  emit chunk [start_offset, len(data))
```

### 1.5 Skip-ahead optimization (cut-point skipping)

Because `(h << 1)` discards a bit per byte, the hash at any offset depends only on the **last 64 bytes**. So the scanner may skip hashing the prefix of each chunk entirely:

- Advance the scan pointer by up to `MIN_CHUNK_SIZE - 64 - 1` bytes at the start of a chunk before hashing/testing (xet-core: `if previous_len + 64 < minimum_chunk { skip = min(minimum_chunk - previous_len - 64 - 1, n_bytes) }`).
- This produces *identical* boundaries to a byte-by-byte implementation that simply refrains from taking boundaries before `MIN_CHUNK_SIZE` (the hash is fully "warmed up" by ≥ 64 bytes before the first admissible test).
- Additional xet-core guard: even after skipping, if a mask match fires at a position where `chunk_size_so_far < minimum_chunk` (possible when hasher state carried over fewer than 64 fresh bytes), the match is **ignored** (`continue`) — the boundary must satisfy `size >= minimum_chunk`.
- Cap scanning: only read up to `maximum_chunk - previous_len` bytes; if reached, force boundary at exactly `maximum_chunk`.

xet-core reference: `xet_data/src/deduplication/chunking.rs` (`Chunker::next_boundary`). The gearhash crate's `next_match(data, mask)` returns `i + 1` (count of bytes consumed) when `h & mask == 0` after processing byte `i`, i.e. the boundary is *after* the matching byte.

### 1.6 The 256-entry u64 gear table

The spec pins the table to rust-gearhash's `DEFAULT_TABLE`:

- Vendor from: `https://raw.githubusercontent.com/srijs/rust-gearhash/adad44e7141cfd29d898cf6e0858f50b995db286/src/table.rs`
- Rust decl: `pub static DEFAULT_TABLE: Table = [ ... ];` where `pub type Table = [u64; 256];`
- Exactly 256 entries ("random but static" integers).

First 8 entries (indices 0–7):

```text
0xb088d3a9e840f559, 0x5652c7f739ed20d6, 0x45b28969898972ab, 0x6b0a89d5b68ec777,
0x368f573e8b7a31b7, 0x1dc636dce936d94b, 0x207a4c4e5554d5b6, 0xa474b34628239acb,
```

Last 8 entries (indices 248–255):

```text
0x00004f63381b10c3, 0x07d5b7816fcc4e10, 0xe5a536726a6a8155, 0x57afb23447a07fdd,
0x18f346f7abc9d394, 0x636dc655d61ad33d, 0xcc8bab4939f7f3f6, 0x63c7a906c1dd187b,
```

(Note some entries legitimately have high zero bytes, e.g. index 17 = `0x00005082119ea468`, index 23 = `0x0000010695477bc5`.)

xet-core does not vendor its own table; it depends on the `gearhash` crate and uses `gearhash::Hasher::default()` (i.e. `DEFAULT_TABLE`).

### 1.7 Portability / determinism notes

- Endianness does not affect chunk boundaries (byte-wise updates, scalar u64 ops).
- SIMD implementations must produce identical boundaries; they are optimizations only.
- Deterministic: same input → same boundaries on all platforms.

### 1.8 Chunking test vectors

HF dataset repo `xet-team/xet-spec-reference-files`:

- Input: `Electric_Vehicle_Population_Data_20250917.csv`
- Expected chunks: `Electric_Vehicle_Population_Data_20250917.csv.chunks` — each line is `<64-hex-char chunk hash string> <chunk length in bytes>`. Use the lengths to validate boundary placement; use the hashes to validate chunk hashing. (796 chunks total.)

---

## 2. Hashing

All hashes are 32 bytes (256 bits). All hash constructions use **BLAKE3 keyed hashing** (`blake3::keyed_hash(key, data)` — the standard BLAKE3 keyed mode, 32-byte key, 32-byte output). Four distinct keys/uses:

| Use | Key |
|---|---|
| Chunk (leaf) hash | `DATA_KEY` |
| Internal Merkle node hash | `INTERNAL_NODE_KEY` |
| File hash finalization ("HMAC" step) | salt; 32 zero bytes when unsalted |
| Term/range verification hash | `VERIFICATION_KEY` |

### 2.1 Keys (exact bytes, decimal)

```text
DATA_KEY = [102, 151, 245, 119, 91, 149, 80, 222, 49, 53, 203, 172, 165, 151, 24, 28,
            157, 228, 33, 16, 155, 235, 43, 88, 180, 208, 176, 75, 147, 173, 242, 41]

INTERNAL_NODE_KEY = [1, 126, 197, 199, 165, 71, 41, 150, 253, 148, 102, 102, 180, 138, 2, 230,
                     93, 221, 83, 111, 55, 199, 109, 210, 248, 99, 82, 230, 74, 83, 113, 63]

VERIFICATION_KEY = [127, 24, 87, 214, 206, 86, 237, 102, 18, 127, 249, 19, 231, 165, 195, 243,
                    164, 205, 38, 213, 181, 219, 73, 230, 65, 36, 152, 127, 40, 251, 148, 195]
```

References: `xet_core_structures/src/merklehash/data_hash.rs` (DATA_KEY, INTERNAL_NODE_HASH key), `xet_core_structures/src/metadata_shard/chunk_verification.rs` (VERIFICATION_KEY), all at commit `c3c726b`.

### 2.2 MerkleHash representation and string form (endianness — critical)

`MerkleHash`/`DataHash` is a transparent `[u64; 4]` overlaying the raw 32 hash bytes (pure typecast, no byte reordering). The **hex string form** prints each of the 4 u64s as `{:016x}` of its little-endian value:

```rust
pub fn hex(&self) -> String {
    format!("{:016x}{:016x}{:016x}{:016x}",
        self.0[0].to_le(), self.0[1].to_le(), self.0[2].to_le(), self.0[3].to_le())
}
```

On a little-endian machine this means: **for each consecutive 8-byte group of the raw digest, the hex string shows those 8 bytes byte-reversed** (u64 read little-endian, printed most-significant-nibble first). To convert a spec/reference hex string back to raw bytes: split into four 16-hex-char groups, parse each as a u64, write each u64 little-endian. The spec states: "To get the raw value of these hashes you must invert the endianness of each byte octet in the hash string."

C# sketch:

```csharp
// raw digest (32 bytes) -> canonical hex string
string ToHex(ReadOnlySpan<byte> raw) {
    var sb = new StringBuilder(64);
    for (int i = 0; i < 4; i++)
        sb.Append(BinaryPrimitives.ReadUInt64LittleEndian(raw.Slice(8*i, 8)).ToString("x16"));
    return sb.ToString();
}
```

Also defined: `base64()` = URL-safe, no-padding Base64 of the raw 32 bytes. `Hash %` (used for tree cuts and dedup eligibility): `hash % n == u64_le(bytes[24..32]) % n` (i.e. `self[3].to_le() % rhs`).

### 2.3 Chunk hashes (leaves)

```text
chunk_hash = blake3_keyed(DATA_KEY, chunk_bytes)      // 32 bytes
```

### 2.4 Internal node hash

Given an ordered group of child nodes, each a `(hash, size)` pair:

1. Build an ASCII buffer with one line per child: `"{hash_hex} : {size}\n"` —
   - `hash_hex` = the 64-lowercase-hex-char string form from 2.2,
   - separator is exactly space, colon, space (`" : "`),
   - `size` in decimal (no padding), then `\n`. (Max entry length 64+3+20+1 = 88 bytes.)
2. `node_hash = blake3_keyed(INTERNAL_NODE_KEY, buffer_bytes)`
3. The node's `size` = sum of child sizes (propagated up the tree).

Spec example — children:

```text
1f6a2b8e9d3c4075a2e8c5fd4f0b763e6f3c1d7a9b2e6487de3f91ab7c6d5401,10000
7c94fe2a38bdcf9b4d2a6f7e1e08ac35bc24a7903d6f5a0e7d1c2b93e5f748de,20000
cfd18a92e0743bb09e56dbf76ea2c34d99b5a0cf271f8d429b6cd148203df061,25000
e38d7c09a21b4cf8d0f92b3a85e6df19f7c20435e0b1c78a9d635f7b8c2e4da1,64000
```

Buffer (note trailing newline):

```text
"1f6a2b8e9d3c4075a2e8c5fd4f0b763e6f3c1d7a9b2e6487de3f91ab7c6d5401 : 10000
7c94fe2a38bdcf9b4d2a6f7e1e08ac35bc24a7903d6f5a0e7d1c2b93e5f748de : 20000
cfd18a92e0743bb09e56dbf76ea2c34d99b5a0cf271f8d429b6cd148203df061 : 25000
e38d7c09a21b4cf8d0f92b3a85e6df19f7c20435e0b1c78a9d635f7b8c2e4da1 : 64000
"
```

### 2.5 Merkle tree construction (aggregated node hash) — NOT a fixed-arity tree

Reference: `xet_core_structures/src/merklehash/aggregated_hashes.rs` @ `c3c726b`. The tree uses **content-defined grouping** with a mean branching factor:

```text
AGGREGATED_HASHES_MEAN_TREE_BRANCHING_FACTOR (BF) = 4
MIN_GROUP_SIZE = 2
MAX_GROUP_SIZE = 2*BF + 1 = 9
natural cut condition on a node: hash % 4 == 0     // i.e. u64_le(raw[24..32]) % 4 == 0
```

Group-cut algorithm (`next_merge_cut`) over a slice of `(hash, size)` nodes:

```text
if len(nodes) <= 2: return len(nodes)          // whole remainder is one group
end = min(2*BF + 1, len(nodes))                // = min(9, len)
for i in 2..end:                               // i starts at 2 -> groups have >= 3 nodes
    if nodes[i].hash % 4 == 0: return i + 1    // cut AFTER the node satisfying the condition
return end                                     // no natural cut -> group of `end` nodes
```

Tree collapse (`aggregated_node_hash`):

```text
if nodes is empty: return 32 zero bytes
hv = nodes                               // list of (hash, size)
while len(hv) > 1:
    out = []
    read = 0
    while read != len(hv):
        cut = read + next_merge_cut(hv[read..])
        out.append(merged_hash_of_sequence(hv[read..cut]))   // (internal_node_hash, sum_of_sizes)
        read = cut
    hv = out
return hv[0].hash
```

`merged_hash_of_sequence(group)` = internal node hash per 2.4 over the group, paired with the summed size.

Consequences to get right:

- **Single node in the whole list → the aggregated hash IS that node's hash** (no internal-node wrapping; the while loop never runs). Verified by test vector: a single chunk `(cfc5d07f..., 100)` yields xorb hash `cfc5d07f...` itself.
- Empty list → all-zero hash.
- Groups at each level have between 2 and 9 children; a group of exactly 2 happens only for a trailing remainder (`len <= 2`).

### 2.6 Xorb hash

```text
xorb_hash = aggregated_node_hash([(chunk_hash_i, chunk_len_i), ...])   // leaves = chunk hashes in order
```

(empty xorb → zero hash; the spec calls this "the root node hash of the MerkleTree").

### 2.7 File hash

```text
root = aggregated_node_hash([(chunk_hash_i, chunk_len_i) for all chunks of the file, in order])
file_hash = blake3_keyed(key = salt, data = root_raw_32_bytes)
// unsalted (the normal case): salt = 32 zero bytes
```

In xet-core this is `root.hmac(salt)` where `hmac(key) = blake3::keyed_hash(&key, self.as_bytes())`. So the "file hash" = xorb-style Merkle root, then one extra keyed-blake3 with an all-zeros key (or a repo salt via `file_hash_with_salt`).

### 2.8 Term verification hashes (range hashes, for shard upload)

Every term in every file-info in an uploaded shard MUST have a matching `FileVerificationEntry` hash:

1. Take the chunk hashes of the term's range `[chunk_index_start, chunk_index_end)` (end-exclusive) within the term's xorb.
2. Concatenate their **raw 32-byte** representations in order (NOT hex, NO separators, NO lengths).
3. `verification_hash = blake3_keyed(VERIFICATION_KEY, concatenated_bytes)`.

Reference impl (`chunk_verification.rs`, verbatim):

```rust
pub fn range_hash_from_chunks(chunks: &[MerkleHash]) -> MerkleHash {
    let combined: Vec<u8> = chunks.iter().flat_map(|hash| hash.as_bytes().to_vec()).collect();
    let range_hash = blake3::keyed_hash(&VERIFICATION_KEY, combined.as_slice());
    MerkleHash::from(range_hash.as_bytes())
}
```

Spec Python (verbatim):

```python
def verification_hash_function(term):
    buffer = bytes()
    # note chunk ranges are end exclusive
    for chunk_hash in term.xorb.chunk_hashes[term.chunk_index_start : term.chunk_index_end]:
        buffer.extend(bytes(chunk_hash))
    return blake3(buffer, key=VERIFICATION_KEY)
```

### 2.9 Official reference-file test vectors (dataset `xet-team/xet-spec-reference-files`)

- Chunk hashes: three `.chunk` files whose first 64 filename chars are the string-form chunk hash of the file's contents:
  - `b10aa1dc71c61661de92280c41a188aabc47981739b785724a099945d8dc5ce4.chunk`
  - `26255591fa803b6baf25d88c315b8a6f5153d5bcfdf18ec5ef526264e0ccc907.chunk`
  - `099cb228194fe640e36a6c7d274ee5ed3a714ccd557a0951d9b6b43a7292b5d1.chunk`
- File hash of `Electric_Vehicle_Population_Data_20250917.csv` = `118a53328412787fee04011dcf82fdc4acf3a4a1eddec341c910d30a306aaf97`
- Xorb hash (all 796 chunks in one xorb) = `eea25d6ee393ccae385820daed127b96ef0ea034dfb7cf6da3a950ce334b7632` (serialized xorb + its `.chunks` list are in the repo)
- Range/verification hash over that single all-796-chunk range = `d81c11b1fc9bc2a25587108c675bbfe65ca2e5d350b0cd92c58329fcc8444178`

### 2.10 xorb_hash / file_hash unit-test vectors (from xet-core `aggregated_hashes.rs` tests)

Format: leaves `[(hash_hex, size)...]` → `xorb_hash`; then `(salt, file_hash_with_salt)` pairs (salt `000...0` = the standard file hash). All strings are the endianness-swapped hex form of 2.2; feed them through `from_hex` before use. Selected vectors (full set of 18 in the source file, incl. a 100-leaf case: https://github.com/huggingface/xet-core/blob/c3c726bed5cf54ded92a63fab892cfb7857c751a/xet_core_structures/src/merklehash/aggregated_hashes.rs):

```text
[] -> xorb 0000...0000
  salt 0000...0000 -> file 0000...0000

[("0000000000000000000000000000000000000000000000000000000000000000", 0)]
  -> xorb 0000000000000000000000000000000000000000000000000000000000000000
  salt 0000...0 -> file 638a6bc391964a85939d48f008e8bdbae6a7975e7ca2d87a3ce2492f4e4d8a4c
  salt f4aa7219b7bc6145df344b930c1cf63680037e13236f4b3b8f439aba1520d443
       -> file 3e44e4a200e05a69afc979eb2e0507817e4f0ddff128a97e563c0e05fed3bd25

[("cfc5d07f6f03c29bbf424132963fe08d19a37d5757aaf520bf08119f05cd56d6", 100)]
  -> xorb cfc5d07f6f03c29bbf424132963fe08d19a37d5757aaf520bf08119f05cd56d6   // single leaf = identity!
  salt 0000...0 -> file 8e16257caa3fe079d484d872a8975264b2ff683b0d6db9028cc7c0f968a45661

[("cfc5d07f...56d6",100), ("c3e67584b5c4fc2a89837ec39e40f2c8a6bb0b2987ac94cd4b31e5fbdd210a72",200),
 ("0d2beb91b9196929a5ddec9f6e306924ddf4a24268e3e59fd8464738d525af37",300)]
  -> xorb 71ec1275fca074724e2dd666921b3277c7cee603e4d025bcab2d4050015be2bc
  salt 0000...0 -> file 54e55dccc6653c612bdb5576c5d3cb34bb31bc4e100248abccf4c908b3ef7715

[("cfc5d07f...56d6",100) x4]
  -> xorb 89f2ada89ff8c96763c6b25010e6dd76a4c05b1466207633ea559acf2093211b
  salt 0000...0 -> file 2cdba690d0e09563596e0cda626d43eb4c96ef1e994fe72d9b2f5a83cfcd36dd

[("cfc5d07f...56d6",100),("c3e67584...0a72",200)] x2   (4 leaves alternating)
  -> xorb 90f8313ef12df385d237a067aded02562c35ded12116e32eba401dbc86c38031
  salt 0000...0 -> file 284ea045e5a579e99c21ec597c20de1fc0c09e7168162aac00db8f61b3d82dbb

6 leaves (100,200,100,200,300,400 pattern above + adf8773496a9b7319b2e50dc98093f344053b17d8ad37100b9c07d9805988784)
  -> xorb 52c826f99507aa05d0b45e9837fa1709e0485425cfbcb1e80db3905cf98b3ee9
  salt 0000...0 -> file 91d21684db364c8883ab8209fa5eb2e781cf07f37e1fa43e731c30839afe422f

8 distinct leaves (sizes 0,100,...,700; hashes 0000...0, cfc5d07f..., c3e67584..., 0d2beb91...,
 adf87734..., 4ac202caf347fc1e9c874b1ef6a1c5e619141eb775a6f43f0f0124ccd0060d9e,
 b3b28636f65c149ea52eb1f94669466f70f033b54cea792824c696ba6ef3c389,
 0e2c1a002aae913d2c0fc8ddfa4e9e14b7b311b3b0d458726d5d9f6a6318013c)
  -> xorb f62abe77e3fb9c954fe52b0028027ddc90c064c45951a4fd2211d87e5c0011db
  salt 0000...0 -> file d1b068be5bbdb38992269e8efe61f601881e39f7a7585fd76883cc6ea5c23b44

same 8 leaves repeated 8 times (64 leaves)
  -> xorb 6554007c9b5d0a5e7918f79596a1b68815c1407535585435f5735db761f21b88
  salt 0000...0 -> file a8640ab81d48854e00078e12b1ea8be5d90be0ffb5f73a15b7009981d093ddd8

[("cfc5d07f...56d6",100)] x32
  -> xorb 0a0123c1617921883b7e13902095fcb86676e77c49120c33b233003b0af0e0a6
  salt 0000...0 -> file 53af4711fd1d5e5bdc7f931b6be932314d8d673cb16ad2482f6f5222eaf9e63d
```

(These make excellent C# unit tests: they exercise empty, single-leaf identity, multi-level collapse, and the salt/HMAC finalization.)

---

## 3. Deduplication

### 3.1 Units and limits

- **Chunk**: CDC block; target 64 KB, range 8–128 KB; identified by its chunk hash (Blake3-keyed MerkleHash).
- **Xorb**: aggregation of chunks. **Max size 64 MB** (`MAX_XORB_BYTES = 64*1024*1024`), **max 8,192 chunks** (`MAX_XORB_CHUNKS = 8*1024`). If adding a chunk would exceed either limit, finalize and upload the current xorb, start a new one.
- **Shard**: max size 64 MB; lists xorbs that can be deduped against (for dedup purposes ignore the shard's file-info section). Serves as the positive response format for global dedup queries.
- **CAS**: content-addressed, immutable storage keyed by cryptographic hash.

### 3.2 Three-level dedup strategy

1. **Local session dedup** — in-memory hash table of chunk hashes seen in the current upload session. Fastest, zero network.
2. **Cached metadata dedup** — local cache of shard files from previously uploaded/downloaded content; persistent across sessions.
3. **Global dedup API** — cross-user/cross-repo dedup service, HMAC-protected.

### 3.3 Global dedup eligibility and query rules

- **Eligibility** (to limit system load), a chunk may be queried iff:
  1. it is the **first chunk of a file** (always eligible), OR
  2. `u64_le(chunk_hash_raw_bytes[24..32]) % 1024 == 0` — "the last 8 bytes of the hash interpreted as a little-endian 64-bit integer % 1024 == 0". (This is exactly `MerkleHash % 1024 == 0` via the `Rem` impl `self[3].to_le() % rhs`.)
- **Spacing recommendation**: the API returns info about nearby chunks on a match; consider issuing at most one eligible-chunk request per ~4 MB of data.
- Queries SHOULD run asynchronously in the background (don't block upload).

### 3.4 Global dedup response handling (HMAC scheme)

On a match the server returns a **shard** containing:

- **CAS info section**: metadata about many xorbs and their chunk listings.
- **HMAC key** in the shard metadata header.
- **All chunk hashes in the returned shard are HMAC-transformed** with that key. The transformation is the same `hmac` primitive as 2.7: `hmac_hash = blake3_keyed(key = shard_hmac_key, data = raw_chunk_hash_32_bytes)`.

Client matching procedure (MUST):

1. Compute `blake3_keyed(hmac_key, own_chunk_hash_bytes)`.
2. Search for that value in the shard's chunk listings.
3. Repeat for subsequent chunks (encrypt-then-search each).
4. Track the original (non-HMAC'd) chunk hash locally while recording which xorb (and position within it) holds the chunk.
5. Download and cache the shard metadata for future dedup.

Security property: raw chunk hashes are never sent server→client; a client must already possess the data (and thus the raw hash) to discover a match, and a match reveals only that one chunk's location, not other raw hashes.

### 3.5 File reconstruction info produced by dedup

For each deduplicated segment (term), record:

- hash of the xorb containing the chunks,
- flags for the CAS block,
- total bytes in the segment (unpacked length),
- start and end chunk indices within the xorb (**start inclusive, end exclusive**).

Reconstruction = locate xorbs → extract chunk ranges → concatenate in order.

### 3.6 Fragmentation prevention (SHOULD)

Aggressive dedup scatters a file's chunks across many xorbs, hurting read performance. Implementations SHOULD keep long continuous runs of chunks in the same xorb, even at the cost of skipping dedup for a few chunks. Suggested heuristics from the spec: only take a dedup reference for runs of **at least 8 chunks**, or target average contiguous runs of **≥ 1 MB**.

---

## 4. C# implementation checklist

1. **Gear table**: vendor the 256 u64 constants from the pinned rust-gearhash `table.rs` URL above into a `static readonly ulong[256]`.
2. **Chunker**: `h = unchecked((h << 1) + TABLE[b])`; test `(h & 0xFFFF000000000000UL) == 0`; min 8 KiB (no tests below it, and ignore matches that would land below it), max 128 KiB forced; reset `h = 0` on emit; skip-ahead `min_chunk - 64 - 1` allowed; streaming-safe.
3. **BLAKE3 keyed mode** (not BLAKE3-HMAC, not derive_key) — e.g. via `Blake3.Hasher.NewKeyed(key)`.
4. **Hash type**: store raw 32 bytes; implement `hex()`/`from_hex` with the per-8-byte u64-LE swap; implement `mod` as `ReadUInt64LittleEndian(bytes[24..32]) % n`.
5. **Merkle aggregation**: BF=4 content-defined grouping (`next_merge_cut`), group text lines `"{hex} : {size}\n"`, INTERNAL_NODE_KEY; single-leaf identity; empty→zero.
6. **File hash**: root then `blake3_keyed(zero_key_32, root_bytes)`.
7. **Verification/range hash**: raw-byte concat + VERIFICATION_KEY.
8. **Global dedup**: eligibility `hash % 1024 == 0` or first chunk; ~4 MB spacing; HMAC candidate matching = `blake3_keyed(shard_key, raw_hash)`.
9. **Xorb packing**: ≤ 64 MB and ≤ 8192 chunks; dedup runs ≥ 8 chunks / ≥ 1 MB to limit fragmentation.
10. **Validate** against: the `.chunks` file (boundaries + chunk hashes), the `.xet-file-hash` / `.xet-xorb-hash` / `.range-hash` reference values (section 2.9), and the 18 unit-test vectors (section 2.10).
