# Captured responses

Real CAS API responses, captured from `https://cas-server.xethub.hf.co` on 2026-08-16 with an
anonymous (unauthenticated) Xet read token. Only the CDN credentials in the signed URLs are
redacted — `Policy`, `Signature` and `Key-Pair-Id` values are replaced with `REDACTED`. Everything
else is byte-for-byte what the server sent, reformatted with two-space indentation.

These are parser goldens: signed URLs expire within the hour, so nothing here can be fetched.
Live fetching lives in the opt-in tests (see `LiveDownloadTests`).

| File | Source | What it pins down |
|---|---|---|
| `ev-population.v2.json` | `GET /v2/reconstructions/118a5332…aaf97` | Whole file: one term, one xorb, one range — the same xorb the vendored reference prefix comes from |
| `ev-population-range.v2.json` | …same, with `Range: bytes=0-200000` | A range query truncated to the first two chunks |
| `ev-population-range.v1.json` | `GET /v1/reconstructions/118a5332…aaf97` | The deprecated v1 shape: `fetch_info` with `range` + `url_range` instead of `xorbs` |
| `gpt2-model-safetensors.v2.json` | `GET /v2/reconstructions/63bed808…8758` (`openai-community/gpt2`, `model.safetensors`) | Nine terms across nine xorbs — the multi-xorb case |
| `gpt2-model-safetensors-range.v2.json` | …same, with `Range: bytes=100000000-100200000` | A mid-chunk range start: `offset_into_first_range` is 24735, not 0 |

To recapture (any Xet-backed public file works; no Hub token needed):

```sh
hash=$(curl -sSI "https://huggingface.co/openai-community/gpt2/resolve/main/model.safetensors" |
  grep -i '^x-xet-hash:' | tr -d '\r' | awk '{print $2}')
read token cas < <(curl -sS "https://huggingface.co/api/models/openai-community/gpt2/xet-read-token/main" |
  python3 -c "import json,sys; d=json.load(sys.stdin); print(d['accessToken'], d['casUrl'])")
curl -sS --compressed -H "Authorization: Bearer $token" "$cas/v2/reconstructions/$hash"
```
