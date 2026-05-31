> Both tiers exist: short-term in `src/Dmon.Memory` (this repo), long-term in the separate `Dmon.Memory.Meko` package. This change adds the facade + DI **in `Dmon.Memory`** and MUST NOT take a dependency on `Dmon.Memory.Meko` (D4). Build against the `Dmon.Abstractions.Memory` contracts.

## 1. IMemory facade + cross-tier fusion

- [x] 1.1 Implement `Memory : IMemory` in `Dmon.Memory`: constructor takes `IShortTermMemory` (required) + `ILongTermMemory?` (optional); expose `ShortTerm` (never null) and `LongTerm` (nullable) properties (D1)
- [x] 1.2 `RecordAsync` fans out to short-term always and to long-term when non-null; `FlushAsync` flushes every configured tier (D1)
- [x] 1.3 `SearchAsync`: query both tiers in parallel, fuse by rank-based RRF (`1/(k+rank)`, `k=60`, per-tier weights at a single adjustable point), set `MemoryHit.Source`, and de-duplicate near-identical cross-tier hits (normalized `Text`, fallback `Id`; keep higher fused rank, prefer the copy carrying `Relations`) (D2, D3)
- [x] 1.4 Degrade gracefully: `LongTerm` null → short-term only; a long-term search fault is contained (log + return short-term results), not propagated (D5)

## 2. DI wiring (decoupled)

- [x] 2.1 Implement `AddDmonMemory()` `IServiceCollection` extension registering the local embedder, the short-term store, and `IMemory` → `Memory`; resolve `ILongTermMemory?` from the container at facade construction (null if none registered) (D4)
- [x] 2.2 Confirm `Dmon.Memory` has **no** `PackageReference`/`ProjectReference` to `Dmon.Memory.Meko`; document the host composition (`AddDmonMemory()` + optionally the Meko package's `AddMekoLongTermMemory(...)`, either order) (D4)

## 3. Tests

- [ ] 3.1 Record/flush fan-out reaches both tiers (fake `IShortTermMemory` + fake `ILongTermMemory`)
- [ ] 3.2 Fused search: `Source` provenance correct; ordering is rank-based RRF (not raw score); cross-tier duplicate collapses to one; one-tier-empty returns the other
- [ ] 3.3 Long-term-disabled path: facade operates short-term only, no long-term calls, `LongTerm` null
- [ ] 3.4 Resilience: a throwing/timing-out long-term `SearchAsync` degrades to short-term results without surfacing the error
- [ ] 3.5 DI: `AddDmonMemory()` alone resolves a short-term-only facade; with an `ILongTermMemory` also registered (either order), the facade fuses both
