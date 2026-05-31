## Why

dmon now has both memory tiers — short-term (`Dmon.Memory`: local embeddings + per-session hybrid `index.db`) and long-term (Meko, shipped separately as `Dmon.Memory.Meko`). What's missing is the unifying layer the `IMemory` interface already promises: a facade that fans writes out to both tiers and fuses reads into a single ranked list, plus the DI wiring that assembles it. Without it, callers must juggle `IShortTermMemory` and `ILongTermMemory` by hand and there is no cross-tier recall.

## What Changes

- Implement the **`IMemory` facade** (`Memory : IMemory`) in `Dmon.Memory`:
  - `RecordAsync` fans out to `ShortTerm` and (when present) `LongTerm`.
  - `FlushAsync` flushes both tiers.
  - `SearchAsync` queries both tiers and fuses results with **rank-based Reciprocal Rank Fusion** (RRF), tagging each `MemoryHit.Source` and de-duplicating near-identical cross-tier hits.
  - Exposes `ShortTerm` (never null) and `LongTerm` (null when long-term is not configured); degrades to short-term only when `LongTerm` is null.
- Add **`AddDmonMemory()`** — an `IServiceCollection` extension wiring the local embedder, the short-term store, and the facade.
- **Decoupled long-term:** `Dmon.Memory` takes **no dependency** on `Dmon.Memory.Meko`. The facade consumes whatever `ILongTermMemory` is registered in the container; the host opts into Meko by *separately* calling `Dmon.Memory.Meko`'s `AddMekoLongTermMemory(...)`. If none is registered, `LongTerm` is null and dmon runs short-term only.

## Capabilities

### Modified Capabilities

- `memory`: adds the facade (fan-out + fused reads), cross-tier rank-based RRF, result provenance/dedup, and the `AddDmonMemory()` composition contract. The existing short-term requirements are unchanged.

## Impact

- **`Dmon.Memory`**: new `Memory` facade type + `AddDmonMemory()` DI extension; no new external dependencies; **no reference to `Dmon.Memory.Meko`** (the load-bearing decoupling).
- **No impact** on the short-term tier's storage/contract or on `Dmon.Abstractions`.
- **Composition:** hosts that want durable memory call `AddDmonMemory()` **and** `AddMekoLongTermMemory(...)`; hosts that don't call only `AddDmonMemory()`.
