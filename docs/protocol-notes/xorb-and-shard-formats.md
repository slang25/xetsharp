# Xet Protocol — Binary Formats & Reconstruction Digest

Research digest for implementing a C# Xet client. Sources (fetched 2026-08-16):

- https://huggingface.co/docs/xet/main/en/xorb
- https://huggingface.co/docs/xet/main/en/shard
- https://huggingface.co/docs/xet/main/en/file-reconstruction
- https://huggingface.co/docs/xet/main/en/api (supporting: exact JSON schemas, endpoints)
- https://huggingface.co/docs/xet/main/en/download-protocol (supporting: QueryReconstructionResponse details)
- `xet-core` source `xet_core_structures/src/metadata_shard/shard_format.rs` (exact magic tag bytes)

Global conventions:

- **All multi-byte integers in all binary formats are little-endian.**
- **All chunk ranges everywhere are half-open `[start, end)`** (start-inclusive, end-exclusive).
- `Hash` = 32-byte value (`[u8; 32]`).
- HTTP `Range` byte ranges are **end-inclusive** (standard HTTP semantics) — note the contrast with chunk ranges.

---

## 1. Xorb Format

A **xorb** ("Xet Orb") is a serialized sequence of chunks: repeated `[chunk header][compressed chunk data]` records, back to back, with **no container header, no footer, no index**. Chunk N+1 starts immediately after chunk N's data.

```txt
┌─────────┬─────────────────────────┬─────────┬─────────────────────────┬──────
│  Chunk  │                         │  Chunk  │                         │
│  Header │  Compressed Chunk Data  │  Header │  Compressed Chunk Data  │ ...
└─────────┴─────────────────────────┴─────────┴─────────────────────────┴──────
│         Chunk 0                   │         Chunk 1                   │ ...
```

### 1.1 Size limits

- **Hard limit: 64 MiB total serialized xorb size.** The CAS server rejects uploads exceeding this.
- No explicit limit on chunk count; since target chunk size is 64 KiB, expect **~1024 chunks per xorb**.
- Recommended packing: accumulate chunks until total *uncompressed* length is near 64 MiB, then serialize. (Xorbs point to roughly 64 MiB of data.)
- Max raw (uncompressed) chunk size: **128 KiB** (needs 18 bits; fits in the 3-byte size field).
- RECOMMENDED: pack chunks from multiple files into one xorb when size allows.

> **"Near 64 MiB uncompressed" is not the same as "under 64 MiB serialized".** A chunk that fails to
> compress is stored as-is, so the serialized xorb is the raw bytes *plus 8 bytes of header per
> chunk*. Packing to exactly 64 MiB of raw data therefore overshoots the limit on incompressible
> input. XetSharp caps the raw total at 64 MiB minus one header per chunk at its 8192-chunk cap,
> which makes an over-limit xorb impossible to build rather than something to discover after
> compressing the chunk that broke it. (The reference xorb is 63.5 MB of CSV in 14.7 MB serialized,
> so the difference only bites on data that does not compress.)

### 1.2 Chunk header — 8 bytes

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 1 | `version` | Protocol version, currently `0` |
| 1 | 3 | `compressed_size` | 3-byte little-endian unsigned int; size of the data after compression |
| 4 | 1 | `compression_type` | Enum, see below |
| 5 | 3 | `uncompressed_size` | 3-byte little-endian unsigned int; raw chunk size before compression |

```txt
┌─────────┬───────────────────┬──────────────┬───────────────────┐
│ Version │  Compressed Size  │ Compression  │ Uncompressed Size │
│ 1 byte  │ 3 bytes (LE)      │ Type, 1 byte │ 3 bytes (LE)      │
└─────────┴───────────────────┴──────────────┴───────────────────┘
0         1                   4              5                   8
```

Immediately after the header: exactly `compressed_size` bytes of chunk data.

### 1.3 Compression schemes (`compression_type` enum)

| Value | Name | Description |
|-------|------|-------------|
| `0` | `None` | Data stored as-is (`compressed_size == uncompressed_size`) |
| `1` | `LZ4` | LZ4 **frame** format — see the note below |
| `2` | `ByteGrouping4LZ4` (BG4) | Byte grouping with 4-byte groups, then LZ4 (also framed) |

If compression makes the chunk *larger*, the chunk SHOULD be stored uncompressed (scheme 0); uncompressed size max is still 128 KiB.

> **Verified against the reference xorb, 2026-08-16.** The spec says only "standard LZ4 compression",
> which reads like the bare LZ4 block format. It is not: every chunk payload in
> `eea25d6e….xorb` begins with the LZ4 frame magic `04 22 4d 18`, with `FLG = 0x60` (independent
> blocks, no checksums) and `BD = 0x50` (256 KiB max block). Decoding as a block yields garbage.
> In .NET that means `K4os.Compression.LZ4.Streams.LZ4Frame`, not `LZ4Codec.Decode`.

#### BG4 transform (scheme 2)

1. **Group**: create 4 buffers. Walk input 4 bytes at a time `(B1, B2, B3, B4)`, appending each byte to buffer 1..4 respectively. Concatenate buffers in order 1,2,3,4.
   - `[A1,A2,A3,A4, B1,B2,B3,B4, C1,C2,C3,C4, ...]` → `[A1,B1,C1,..., A2,B2,C2,..., A3,B3,C3,..., A4,B4,C4,...]`
   - If length isn't a multiple of 4, distribute the trailing 1–3 bytes one each to the *first* 1–3 groups (continuing the pattern).
2. **Compress** the grouped bytes with standard LZ4.

Decompression (download path): LZ4-decompress, then invert the grouping (split into 4 groups — first `ceil` groups get the extra byte when `len % 4 != 0` — and re-interleave).

### 1.4 Upload notes (serialization)

- Client picks compression per chunk; **one xorb MAY mix schemes across chunks**.
- Strategies: brute-force all schemes and keep smallest; or predict BG4 benefit (xet-core uses max KL divergence over per-byte pop-count distributions of sampled groups — see `xet_core_structures/src/xorb_object/byte_grouping/bg4_prediction.rs`). If chosen scheme shows no benefit, store uncompressed.
- Pseudocode from the spec:

```python
VERSION = 0
for chunk in xorb.chunks:
    compressed, scheme = pick_compression_scheme_and_compress(chunk)
    write(Header(VERSION, len(compressed), scheme, len(chunk)))
    write(compressed)
```

### 1.5 Chunk addressing

- Chunks indexed 0-based within their xorb; addressed by index, usually in ranges `[start, end)`.
- Reference sample xorb: dataset `xet-team/xet-spec-reference-files`, file `eea25d6ee393ccae385820daed127b96ef0ea034dfb7cf6da3a950ce334b7632.xorb` (hash = filename). 14,737,817 bytes, 796 chunks, all scheme 1.

> **Undocumented trailing bytes.** That reference xorb's last complete chunk record ends at offset
> 14,737,813; the remaining four bytes are the ASCII `XETB`. Nothing in the spec describes a xorb
> footer. A reader must therefore stop at the end of the last *complete* record rather than assume
> the data ends exactly on a record boundary — and must not treat a short remainder as corruption.

---

## 2. MDB Shard Format

Binary format carrying file reconstructions (File Info section) and xorb/chunk metadata (CAS Info section). Used as the **request body for shard upload** (`POST /v1/shards`, `/v2/shards`) and the **response body of the global dedupe API** (`GET /v1/chunks/default-merkledb/{hash}`).

### 2.1 Overall layout

```txt
Offset 0:                        Header  (48 bytes, fixed)
Offset footer.file_info_offset:  File Info Section (variable; blocks + bookend)
Offset footer.cas_info_offset:   CAS Info Section  (variable; blocks + bookend)
Offset footer.footer_offset:     Footer  (200 bytes, fixed — sometimes omitted)
```

The File Info section starts immediately after the header; CAS Info starts immediately after the File Info bookend — so a streaming reader never needs the footer.

### 2.2 Constants

- `MDB_SHARD_HEADER_VERSION` = **2**
- `MDB_SHARD_FOOTER_VERSION` = **1**
- `MDB_FILE_INFO_ENTRY_SIZE` = **48** bytes (every file-info struct)
- `MDB_CAS_INFO_ENTRY_SIZE` = **48** bytes (every CAS-info struct)
- `MDB_SHARD_HEADER_TAG`: 32-byte magic. Exact bytes (from xet-core source):

```
"HFRepoMetaData" (14 ASCII bytes) followed by:
0, 85, 105, 103, 69, 106, 123, 129, 87, 131, 165, 189, 217, 92, 205, 209, 74, 169
```

As a C# array:

```csharp
static readonly byte[] MdbShardHeaderTag = {
    (byte)'H',(byte)'F',(byte)'R',(byte)'e',(byte)'p',(byte)'o',
    (byte)'M',(byte)'e',(byte)'t',(byte)'a',(byte)'D',(byte)'a',(byte)'t',(byte)'a',
    0, 85, 105, 103, 69, 106, 123, 129, 87, 131, 165, 189, 217, 92, 205, 209, 74, 169
}; // hex: 48 46 52 65 70 6F 4D 65 74 61 44 61 74 61 00 55 69 67 45 6A 7B 81 57 83 A5 BD D9 5C CD D1 4A A9
```

### 2.3 Header — `MDBShardFileHeader`, 48 bytes at offset 0

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 32 | `tag` | Must equal `MDB_SHARD_HEADER_TAG` |
| 32 | 8 | `version` (u64) | Must be **2** |
| 40 | 8 | `footer_size` (u64) | Bytes in footer; **0 if footer omitted** |

Serialize (upload): tag, version=2, footer_size = 0 (footer MUST be omitted for `/v1/shards` upload body) or 200 if footer included.

### 2.4 File Info Section

Sequence of 0+ **File Info blocks**, terminated by a **bookend entry**. Each block = one file reconstruction:

```
FileDataSequenceHeader                       (1)
FileDataSequenceEntry × num_entries          (terms, in file order)
[FileVerificationEntry × num_entries]        (iff flag WITH_VERIFICATION)
[FileMetadataExt × 1]                        (iff flag WITH_METADATA_EXT; last in block)
```

Blocks are back-to-back; when the next 48-byte record's first 32 bytes are all `0xFF`, that's the bookend and the section is over.

#### FileDataSequenceHeader (48 bytes)

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 32 | `file_hash` | 32-byte file hash |
| 32 | 4 | `file_flags` (u32) | See flags below |
| 36 | 4 | `num_entries` (u32) | Number of `FileDataSequenceEntry` following |
| 40 | 8 | `_unused` | Reserved, zero |

Flags (test with bitwise AND ≠ 0):

- `MDB_FILE_FLAG_WITH_VERIFICATION` = `0x8000_0000` (1 << 31)
- `MDB_FILE_FLAG_WITH_METADATA_EXT` = `0x4000_0000` (1 << 30)

#### FileDataSequenceEntry (48 bytes) — one term

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 32 | `cas_hash` | Xorb hash of the term |
| 32 | 4 | `cas_flags` (u32) | Reserved; set to 0 |
| 36 | 4 | `unpacked_segment_bytes` (u32) | Term size when unpacked (decompressed) |
| 40 | 4 | `chunk_index_start` (u32) | Start chunk index in xorb (inclusive) |
| 44 | 4 | `chunk_index_end` (u32) | End chunk index (exclusive): `[start, end)` |

#### FileVerificationEntry (48 bytes) — OPTIONAL structurally, **MUST be present for shard uploads**

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 32 | `range_hash` | Verification hash of the term's chunk-hash range (see hashing spec, "Term Verification Hashes") |
| 32 | 16 | `_unused` | Reserved, zero |

Rules: nth verification entry ↔ nth data-sequence entry; there are exactly `num_entries` of them, after all data-sequence entries. If any file in a shard has verification entries, **all** files MUST (otherwise the shard is invalid).

#### FileMetadataExt (48 bytes) — OPTIONAL

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 32 | `sha256` | SHA256 of the full file contents — **stored in hash string order**, see note |
| 32 | 16 | `_unused` | Reserved, zero |

> **The SHA256 is byte-swapped like every other hash.** The reference shard for
> `Electric_Vehicle_Population_Data_20250917.csv` stores `1276f752b25512f4…`, while the file's actual
> SHA256 digest is `f41255b252f77612…` — each 8-byte group reversed. The reference implementation
> gets the digest as a hex string and parses it into the same 32-byte hash type it uses everywhere,
> so §3.5's hash↔hex rule applies here too. Writing raw digest bytes straight into the field would
> produce an invalid shard.

At most one per file block, always last in the block. **REQUIRED when uploading to Git-based HF Hub repos** (models/datasets/Spaces — LFS pointers reference the SHA256); OPTIONAL for Storage Buckets. If omitted, `WITH_METADATA_EXT` flag MUST NOT be set.

#### File Info bookend (48 bytes)

- Bytes 0–31: all `0xFF`
- Bytes 32–47: all `0x00`

Detected by reading a would-be `FileDataSequenceHeader` whose hash is all 1-bits.

#### File Info deserialization loop

1. Seek to `footer.file_info_offset` (or just past the 48-byte header).
2. Read `FileDataSequenceHeader`; if `file_hash` is all `0xFF` → bookend, stop.
3. Read `num_entries` × `FileDataSequenceEntry`.
4. If `file_flags & WITH_VERIFICATION`: read `num_entries` × `FileVerificationEntry`.
5. If `file_flags & WITH_METADATA_EXT`: read 1 × `FileMetadataExt`.
6. Goto 2.

### 2.5 CAS Info Section

Sequence of **CAS Info blocks** (one per xorb), terminated by the same style of bookend (32 × `0xFF` + 16 × `0x00`). Each block: `CASChunkSequenceHeader` then `num_entries` × `CASChunkSequenceEntry`.

#### CASChunkSequenceHeader (48 bytes)

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 32 | `cas_hash` | Xorb hash |
| 32 | 4 | `cas_flags` (u32) | Reserved; set to 0 |
| 36 | 4 | `num_entries` (u32) | Number of chunks in this xorb |
| 40 | 4 | `num_bytes_in_cas` (u32) | Total raw (uncompressed) chunk bytes in this xorb |
| 44 | 4 | `num_bytes_on_disk` (u32) | Serialized xorb length as uploaded |

#### CASChunkSequenceEntry (48 bytes)

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 32 | `chunk_hash` | Chunk hash (possibly HMAC-transformed — see footer) |
| 32 | 4 | `chunk_byte_range_start` (u32) | "Start position in CAS block" — see note |
| 36 | 4 | `unpacked_segment_bytes` (u32) | Chunk size when unpacked |
| 40 | 8 | `_unused` | Reserved, zero |

> **What `chunk_byte_range_start` actually holds.** In all three published reference shards it is the
> running sum of `unpacked_segment_bytes` — the chunk's offset in the *uncompressed* stream, ending
> at `num_bytes_in_cas` — not its offset in the serialized xorb. (Those shards also carry
> `num_bytes_on_disk = 0`, so they describe a xorb that had not been serialized when they were
> written; treat the field as uncompressed offsets unless a counter-example turns up.)

Deserialization: seek `footer.cas_info_offset` → read header → bookend check (all-`0xFF` hash) → read `num_entries` entries → repeat.

### 2.6 Footer — `MDBShardFileFooter`, 200 bytes

**MUST NOT be included when serializing the shard as the `/v1/shards` (or `/v2/shards`) upload body** (and header `footer_size` = 0). Located at `file_size − footer_size`.

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 8 | `version` (u64) | Must be **1** |
| 8 | 8 | `file_info_offset` (u64) | Offset of File Info section |
| 16 | 8 | `cas_info_offset` (u64) | Offset of CAS Info section |
| 24 | 48 | `_buffer` | Reserved *per the spec* — but not zero in practice, see below |
| 72 | 32 | `chunk_hash_hmac_key` (Hash) | HMAC key for chunk hashes; zero = no HMAC |
| 104 | 8 | `shard_creation_timestamp` (u64) | Seconds since Unix epoch |
| 112 | 8 | `shard_key_expiry` (u64) | Seconds since Unix epoch |
| 120 | 72 | `_buffer2` | Reserved *per the spec* — but not zero in practice, see below |
| 192 | 8 | `footer_offset` (u64) | Offset where the footer itself starts |

Read: seek `file_size − footer_size` (footer_size from header), read fields sequentially, verify version == 1.

> **Both "reserved" regions carry real fields.** Every reference shard has non-zero bytes inside
> them, matching `xet-core`'s own `MDBShardFileFooter`:
>
> | Offset | Field | Observed value |
> |---|---|---|
> | 24 / 40 / 56 | `file_lookup_offset`, `cas_lookup_offset`, `chunk_lookup_offset` (u64) | all equal `footer_offset` — the lookup tables are empty |
> | 32 / 48 / 64 | matching `*_num_entry` (u64) | all `0` |
> | 168 | `stored_bytes_on_disk` (u64) | `0` in all three (their xorb was not serialized) |
> | 176 | `materialized_bytes` (u64) | total unpacked bytes of the files — `0` in the dedupe shard, which has none |
> | 184 | `stored_bytes` (u64) | total unpacked bytes of the CAS-info chunks |
>
> Zeroing these on write would break byte-identical round-trips, so preserve them. The lookup tables
> would sit between the CAS-info bookend and the footer; a shard that has them can be detected by
> the sections not ending exactly `footer_size` bytes from EOF.

#### HMAC key protection (global dedupe responses)

If `chunk_hash_hmac_key` ≠ 0: chunk hashes stored in the CAS Info section are `HMAC(original_chunk_hash, key)`. To match a local chunk: compute `HMAC(local_chunk_hash, footer.chunk_hash_hmac_key)` and search the shard's CAS entries. A match lets you dedupe by referencing the matched chunk's xorb.

#### Shard key expiry

64-bit unix timestamp; after expiry, clients SHOULD NOT use the shard for dedup (typically days/weeks after issuance). Uploads referencing xorbs from an expired shard may be rejected at the server's discretion.

### 2.7 Shard usage variants

- **Upload shard** (client → server): File Info blocks = file reconstructions being uploaded; CAS Info blocks = every *new* xorb created. Footer omitted, `footer_size = 0`. Verification entries REQUIRED.
- **Global-dedupe response shard** (server → client): File Info section **empty** (just the bookend); CAS Info section describes xorbs containing the queried chunk (plus other likely-related xorbs). Usually HMAC-keyed; has a footer.
- Version mismatch (header ≠ 2, footer ≠ 1) ⇒ reject. Always verify magic, versions, offsets in bounds, and bookend presence.
- Reference files (dataset `xet-team/xet-spec-reference-files`): `Electric_Vehicle_Population_Data_20250917.csv.shard.verification-no-footer` (upload form), `...shard.verification` (with footer), `...shard.dedupe` (dedupe response form).

### 2.8 Complete deserialization (both options from the spec)

```text
// option 1 — streaming, no seeks:
header = read_header()               // 48 bytes, validate tag+version
file_info = read_file_info_section() // through its bookend
cas_info  = read_cas_info_section()  // through its bookend
footer    = read_footer()            // if footer_size != 0

// option 2 — seek with footer:
read header at 0
seek(end - header.footer_size); read footer
seek(footer.file_info_offset); read file info until footer.cas_info_offset
seek(footer.cas_info_offset);  read cas info until footer.footer_offset
```

---

## 3. File Reconstruction

### 3.1 Model

- **Term** = `(xorb_hash: 32-byte, chunk range [start, end))`. A **reconstruction** = ordered list of terms.
- File bytes = concatenation, in term order, of the decompressed chunks each term references.
- Terms follow file byte order; **gaps MUST NOT exist**.
- A file may reference the same xorb in multiple terms (possibly disjoint or overlapping ranges) — that's dedup. Overlapping/adjacent ranges in the same xorb SHOULD be coalesced into one retrieval (the v2 API does this server-side).
- Chunk ranges are in chunk-index space, not bytes; chunk decoded sizes are not uniform. Sub-range slicing happens at byte granularity in decoded output.
- Byte length contributed by a term = sum of decoded sizes of its chunks (minus initial skip / final truncation for sub-range requests).
- Fragmentation guidance: xet-core targets an **average of 8 chunks per term** (fragmentation prevention); prefer long contiguous ranges.

Example: terms `[(X1,[0,5)), (X2,[3,8)), (X1,[9,12))]` → fetch X1 chunks 0–4, X2 chunks 3–7, X1 chunks 9–11, decode, concatenate in that order.

### 3.2 Reconstruction API (download path)

`GET /v2/reconstructions/{file_id}` (preferred; fall back to `/v1/reconstructions/{file_id}` on 404/501). `file_id` = 64-hex file hash (see §3.5 hash-to-hex rule). Auth: `Authorization: Bearer <token>`, `read` scope. Optional `Range: bytes={start}-{end}` header (end **inclusive**) for partial files. Send `Accept-Encoding: gzip` or `zstd`. Errors: 400 malformed id, 401, 404 not found, 416 when range start ≥ file length.

#### v2 `QueryReconstructionResponse`

```json
{
  "offset_into_first_range": 0,
  "terms": [
    {
      "hash": "a1b2c3d4...123456",
      "unpacked_length": 263873,
      "range": { "start": 0, "end": 4 }
    }
  ],
  "xorbs": {
    "a1b2c3d4...123456": [
      {
        "url": "https://transfer.xethub.hf.co/xorb/default/a1b2...?X-Xet-Signed-Range=bytes%3D0-131071&Expires=...&Policy=...&Signature=...&Key-Pair-Id=...",
        "ranges": [
          { "chunks": { "start": 0, "end": 4 }, "bytes": { "start": 0, "end": 131071 } }
        ]
      }
    ]
  }
}
```

Field semantics:

- **`terms`** (`CASReconstructionTerm[]`, ordered): `hash` = xorb hash (64-char lowercase hex); `range` = chunk index range, end-**exclusive**; `unpacked_length` = expected total decompressed byte length of the term's chunks — **MUST validate**; mismatch ⇒ reject (truncated/corrupt data).
- **`xorbs`** (map xorb-hex → `XorbMultiRangeFetch[]`): typically 1 entry per xorb; multiple only if URL length limits force a split. Each entry:
  - `url`: short-lived signed URL; do not cache or rewrite. Signed-range query param `X-Xet-Signed-Range` (URL-encoded `bytes=...`) plus CDN params `Expires`, `Policy`, `Signature`, `Key-Pair-Id`.
  - `ranges` (`XorbRangeDescriptor[]`, ascending `chunks.start`): `chunks` = chunk index range end-**exclusive**; `bytes` = physical byte range in the serialized xorb, end-**inclusive**, used verbatim for the HTTP `Range` header (`{start,end}` → `bytes=start-end`).
- **`offset_into_first_range`** (number): `0` for full-file requests or when range start is 0. For range queries: the number of decoded bytes to skip from the start of the **first term's** decoded output (may span multiple chunks, since download granularity is whole chunks).
- v1 shape differs: `fetch_info` instead of `xorbs` (per-term fetch entries rather than coalesced multi-range URLs). Batch endpoint `POST /v1/reconstructions` (body: `[{"prefix":"default","hash":"<64hex>"}]`) returns `{ "files": {file_id: [terms]}, "fetch_info": {xorb_hash: [...]} }` — v1 shape only.

#### Fetching xorb data

- For each fetch entry, issue **one** GET to `url` with `Range` built from all its `bytes` ranges in given order: `Range: bytes=0-131071,500000-600000`. This MUST match the signed range set exactly — requesting other bytes fails authorization.
- Single range → `206 Partial Content`, raw body. Multiple ranges → `206` with `Content-Type: multipart/byteranges`; parse parts in order (parts correspond 1:1 with `ranges`).
- Each part's body is a slice of the serialized xorb: parse it as consecutive `[8-byte chunk header][compressed data]` records, yielding chunks at indices `chunks.start .. chunks.end − 1`. The part should be exactly consumed.

#### Assembly algorithm

1. Download chunks per the `xorbs` map (parallelizable) into `(xorb_hash, chunk_index) → decoded bytes`.
2. Walk `terms` in order; for each, gather decoded chunks `range.start..range.end`, assert total length == `unpacked_length`.
3. For the first term(s) only: consume `offset_into_first_range` — while it exceeds the current chunk's length, subtract and skip the chunk; then slice the partial chunk.
4. Concatenate everything in term order.
5. If a `Range` was requested: truncate the tail to the requested length (range end may fall mid-chunk). Start offset is handled by `offset_into_first_range`; the client computes the end from the requested length. Exception: if the requested range exceeds file length, returned content is shorter and no truncation is needed.
6. Note chunks can be reused across terms (download once, use many times).

Reference spec pseudocode (download-protocol page):

```python
for term in terms:
  term_chunks = [downloaded_chunks[(term["hash"], i)]
                 for i in range(term["range"]["start"], term["range"]["end"])]
  assert sum(len(c) for c in term_chunks) == term["unpacked_length"]
  for chunk in term_chunks:
    if offset_into_first_range > len(chunk):
      offset_into_first_range -= len(chunk); continue
    if offset_into_first_range > 0:
      chunk = chunk[offset_into_first_range:]; offset_into_first_range = 0
    file_chunks.append(chunk)
```

Performance notes from spec: batch reconstruction requests for big files (e.g. 10 GB windows via `Range`); parallel xorb downloads but in-order term assembly (or seek-write at offsets); cache chunks by range, not the signed URLs; exponential backoff on 429/5xx.

### 3.3 Upload-side reconstruction serialization

A file's reconstruction is serialized into the shard File Info section: one `FileDataSequenceHeader` per file, one `FileDataSequenceEntry` per term (`cas_hash` = xorb hash, `chunk_index_start/end` = term range, `unpacked_segment_bytes` = term decoded length), plus mandatory `FileVerificationEntry` per term and `FileMetadataExt` (SHA256) for HF Hub git repos. When forming xorbs, order/group chunks to maximize contiguous term ranges.

### 3.4 Related endpoints (context for a full client)

| # | Endpoint | Method | Scope | Body / Response |
|---|----------|--------|-------|-----------------|
| 1 | `/v2/reconstructions/{file_id}` | GET | read | → `QueryReconstructionResponse` (v2: `xorbs`) |
| 2 | `/v1/chunks/default-merkledb/{chunk_hash}` | GET | read | → shard bytes (global dedupe; 404 = chunk unknown) |
| 3 | `/v1/xorbs/default/{xorb_hash}` | POST | write | body: serialized xorb → `{"was_inserted": bool}` (`false` = already existed, not an error) |
| 4 | `/v1/shards` | POST | write | body: serialized shard (no footer) → `{"result": 0|1}` (0 = existed, 1 = SyncPerformed; 200 = success either way) |
| 5 | `/v2/shards` | POST | write | same body; response: NDJSON stream of `{"type": "validating"|"committing"|"result"|"error", ...}`; terminal event carries success/failure (status is 200 as soon as the stream starts). Fall back to `/v1/shards` on 404 |
| 6 | `/v1/reconstructions` (batch) | POST | read | body: `[{"prefix":"default","hash":...}]` → `BatchQueryReconstructionResponse` (v1 shape) |
| 7 | `/v1/xorbs/default/{hash}` | HEAD | read | `Content-Length` = stored xorb size |
| 8 | `/v1/files/{file_id}` | HEAD | read | `Content-Length` = full file size |
| 9 | `/v2/file-chunk-hashes/{file_id}` | GET | read | delta-upload helper; `X-Range-Dirty: bytes=a-b,...` (inclusive); response camelCase JSON |

Retryable: connection errors, 429 (backoff), 500, 503, 504. Non-retryable: 400, 401 (refresh token), 403 (need write scope), 404, 416.

### 3.5 Hash → hex-string procedure (critical, non-obvious)

Whenever a 32-byte hash appears as a 64-hex string (API paths, JSON keys, `terms[].hash`): **do NOT hex-encode bytes in order.** Treat each of the four 8-byte groups (0–7, 8–15, 16–23, 24–31) as a little-endian u64 and concatenate the four 16-hex-digit (zero-padded) representations. Equivalently: reverse each 8-byte group, then hex-encode.

Example: bytes `[0,1,...,31]` → reordered `[7,6,5,4,3,2,1,0, 15,...,8, 23,...,16, 31,...,24]` → string `0706050403020100 0f0e0d0c0b0a0908 1716151413121110 1f1e1d1c1b1a1918` (no spaces).

---

## 4. C# Implementation Checklist

**Parsing (download):**
- 8-byte chunk header reader: `version(1) | compressed_size(3 LE) | type(1) | uncompressed_size(3 LE)`; dispatch on type 0/1/2; enforce 128 KiB max; LZ4 *frame* decode (`K4os.Compression.LZ4.Streams.LZ4Frame.Decode`); BG4 un-grouping with remainder handling; stop at a trailing remainder shorter than a header.
- Shard reader: 48-byte header (magic + version 2 + footer_size), 48-byte fixed records throughout, all-0xFF-hash bookends, flag-driven optional sections, 200-byte footer (version 1), HMAC-keyed chunk-hash matching for dedupe shards.
- Reconstruction client: v2 JSON (snake_case; note `/v2/file-chunk-hashes` is camelCase), multi-range GET with exact signed `Range` header, `multipart/byteranges` parsing, `unpacked_length` validation, `offset_into_first_range` skip, tail truncation for ranged requests.

**Serializing (upload):**
- Xorb writer: per-chunk compression choice, ≤ 64 MiB serialized cap, mixed schemes OK.
- Shard writer: header with `footer_size = 0`, File Info blocks with verification entries (required) + SHA256 metadata ext (required for Hub git repos, flag `1<<30`; verification flag `1<<31`), CAS Info blocks for every new xorb (`num_bytes_in_cas` = raw bytes, `num_bytes_on_disk` = serialized xorb length), both bookends, **no footer** in upload body. When a footer *is* written, preserve the fields hiding in the two "reserved" regions.
- Hash-to-hex: 8-byte-group-reversed little-endian rendering for every string-form hash.
- Everything little-endian; ranges `[start, end)` in chunk space, inclusive ends in HTTP `Range`.

**Not covered here** (separate spec pages, fetch when needed): chunking algorithm (target 64 KiB chunks), chunk/xorb/file hash computation, term verification-hash computation, auth/token refresh flow.
