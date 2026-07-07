## 1. Make ReadMessagesAsync tolerant of malformed lines

- [x] 1.1 Add a private static `TryDeserializeElement(string line, out JsonElement element)` helper in `SessionStore` that returns `false` (and skips) on `JsonException`, mirroring `ReadRecordsAsync`'s per-line tolerance (design D2/D3).
- [x] 1.2 Route the no-compaction early return (`lines.Select(l => Deserialize(l))`, ~L307) through the helper so malformed lines are dropped from the result.
- [x] 1.3 Route the "no compaction marker found" early return (~L339) through the helper.
- [x] 1.4 Route the final compaction-aware materialization loop (~L371–380) through the helper, keeping the raw `lines` list intact so `lastCompactionIndex`/`supersedesUpToIndex` positions stay correct (design D1).

## 2. Tests

- [x] 2.1 Add a test: a `messages.jsonl` with well-formed records plus a partial (truncated) trailing line reads back only the well-formed records via `ReadMessagesAsync` (both `applyCompaction: false` and `true`), and does not throw.
- [x] 2.2 Add a test: a malformed *interior* line is skipped while surrounding well-formed records are returned, and (with a compaction marker present) the compaction skip window is still applied correctly.
- [x] 2.3 Add a test asserting reader parity — the same file with a malformed line yields consistent well-formed results from both `ReadMessagesAsync` and `ReadRecordsAsync` (neither throws).

## 3. Gates

- [x] 3.1 `make build` clean (TreatWarningsAsErrors), `env -u MEKO_API_KEY make test` green (new + existing), `openspec validate session-store-partial-line-tolerance --strict`.
