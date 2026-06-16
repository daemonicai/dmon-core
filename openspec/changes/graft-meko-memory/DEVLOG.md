# DEVLOG — graft-meko-memory

Promote memory to its own top-level `memory/` bucket (ADR-026) and graft the
`dmon-meko` long-term tier as `memory/Dmon.Memory.Meko`.

## Status
- [x] Group 1 — Promote memory to the `memory/` bucket (in-tree move) — commit `b10409d`
- [ ] Group 2 — History-preserving import of the dmon-meko long-term tier
- [ ] Group 3 — Re-wire the grafted package to monorepo conventions
- [ ] Group 4 — Solutions
- [ ] Group 5 — Verification gates

---

## Group 1 — Promote memory to the `memory/` bucket (commit `b10409d`)

History-preserving in-tree move realising ADR-026's `memory/` bucket.

- `git mv middleware/Dmon.Memory → memory/Dmon.Memory` — 12 files, all tracked `R100` (byte-identical blobs; reviewer confirmed history at object level, not just the rename flag). No C# source edits — namespaces stay `Dmon.Memory`, no `Dmon.Middleware.*` rename (the `Dmon.Memory.<Name>` family already fits, per ADR-026).
- Contracts (`Dmon.Abstractions.Memory`) **left in `core/`** — implementations only moved (design D1 / ADR-026 D3).
- `test/Dmon.Memory.Tests` stays under `test/`; only its `ProjectReference` to `Dmon.Memory` repathed (`..\..\memory\…`). `memory/Dmon.Memory`'s own refs are depth-invariant (`..\..\core\…`).
- **`middleware/` removed entirely** — it was memberless after the move; per the updated `monorepo-layout` spec a memberless role has no directory and no `.slnx`. `middleware.slnx` → `memory.slnx`; the `Dmon.Middleware.<Name>` family stays *defined* for the first real `IDmonMiddleware`.
- Repathed `Everything.slnx` (`/middleware/`→`/memory/`) and the `Makefile` `build-memory` target. Only stale ref was the Makefile line; all other `middleware` hits are ADR prose / ASP.NET concept mentions.

**Decision applied:** design D2 — in-tree move precedes the graft, leaving `memory/` a stable, independently-green target for Group 2.

**Gates:** `make build` 0W/0E · `make test` 51 pass / 1 skip (unchanged) · `dotnet build memory.slnx -c Release` clean · `openspec validate --strict` valid. Reviewer: sign-off, no blockers, no nits.

**Carry-forward note (not for this change):** `Everything.slnx` still lists `test/Dmon.Extensions.Tests` — residue from the ADR-022 `Dmon.Extensions` deletion, pre-existing, out of scope here.
