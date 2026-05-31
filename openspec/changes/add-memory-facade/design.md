## Context

The memory contracts (`Dmon.Abstractions.Memory`) define `IMemory : IMemoryStore` with `ShortTerm` (`IShortTermMemory`, never null) and `LongTerm` (`ILongTermMemory?`, null when unconfigured). The short-term tier (`Dmon.Memory`) is implemented and merged; the long-term tier is implemented in the separate `Dmon.Memory.Meko` package (consumes `Dmon.Abstractions`). This change adds the facade that sits over both and the DI entry point.

`MemoryHit { Id, Text, Source (ShortTerm|LongTerm), Score, Metadata?, Relations? }` is the shared currency. Short-term `SearchAsync` already fuses its own vector+FTS results via RRF internally; this change fuses across the two *tiers* — a second RRF level over incomparable score spaces (a local embedding model vs. Meko's server model).

## Goals / Non-Goals

**Goals**
- A thin `IMemory` facade: unconditional write fan-out, fused reads, both-tier flush, graceful short-term-only degradation.
- Cross-tier rank-based fusion with provenance and dedup.
- `AddDmonMemory()` that composes the tier without coupling to any long-term implementation.

**Non-Goals**
- Changing the short-term tier or `Dmon.Abstractions`.
- Implementing or referencing a specific long-term backend (Meko lives in its own package and registers itself).
- Scope-widening/promotion between tiers, or background sync.

## Decisions

### D1. Facade is a thin composer over the two interfaces
`Memory : IMemory` takes `IShortTermMemory` (required) and `ILongTermMemory?` (optional) via the constructor. `RecordAsync`/`FlushAsync` dispatch to short-term always and to long-term only when non-null. The facade holds no storage of its own.
- *Why:* the tiers own their semantics (opt-in capture, eventual consistency); the facade only fans out and fuses.

### D2. Cross-tier fusion is rank-based RRF (mirrors the short-term level)
`SearchAsync` queries both tiers (in parallel), then fuses the two result lists by **rank**: `score = Σ weight_tier / (k + rank_in_tier)` with `k = 60` and per-tier weights. Raw `MemoryHit.Score` values are NOT compared across tiers (a local-model cosine vs. Meko's score are incomparable). Each result keeps its originating `Source`. This is the same rank-based discipline the short-term index already uses internally, applied one level up.

### D3. De-duplicate near-identical cross-tier hits
The same fact can surface from both tiers (recorded verbatim in short-term and distilled into long-term). Dedup by a stable key (normalized `Text`, falling back to `Id`); when a duplicate is detected, keep the higher fused rank and prefer retaining `Relations` if only one side carries them. Dedup is best-effort (exact/normalized match), not semantic.

### D4. Long-term is resolved from DI, never referenced directly (decoupled)
`AddDmonMemory()` registers the embedder, the short-term store, and `IMemory` → `Memory`. It resolves `ILongTermMemory?` from the container (e.g. `GetService<ILongTermMemory>()`), so:
- If the host also called `AddMekoLongTermMemory(...)` (from `Dmon.Memory.Meko`), the facade picks it up.
- If not, `LongTerm` is null and the facade is short-term only.

`Dmon.Memory` therefore takes **no `PackageReference` on `Dmon.Memory.Meko`** — the dependency direction stays core-agnostic and dmon runs with long-term off. Registration order is documented (either order works since resolution is at facade construction).

### D5. Conventions
`Task<T>` for I/O, `ValueTask` for `FlushAsync`, `CancellationToken = default`, `IReadOnlyList<T>`, `record` DTOs (per `Dmon.Abstractions`). `SearchAsync` parallelizes the two tier queries and tolerates one tier failing/empty (a long-term error degrades to short-term results rather than throwing).

## Risks / Trade-offs

- **One tier slow/failing** (Meko is remote, eventually consistent) → fused search latency or partial failure. Mitigation: query in parallel; on a long-term fault, log and return short-term results (don't fail the whole search).
- **Dedup false-positives/negatives** (exact-text dedup is crude) → a near-duplicate may slip through or a legitimately distinct hit be merged. Mitigation: conservative normalized-text/Id match only; semantic dedup is out of scope.
- **Fusion weighting** is a heuristic (k=60, equal tier weights by default) → tuning may be needed. Mitigation: keep weights/k in one place; default to the conventional values.

## Migration Plan

Additive. Hosts adopt by calling `AddDmonMemory()` (and optionally `AddMekoLongTermMemory(...)`). No existing requirements change. Rollback = don't register the facade; callers can still use the tiers directly.

## Open Questions

- **[deferred — local]** Default RRF tier weights (start equal; revisit if long-term dominates/recedes).
- **[deferred — local]** Whether dedup should prefer the short-term or long-term copy when both exist (start: keep higher fused rank; prefer the one carrying `Relations`).
