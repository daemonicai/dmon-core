# DEVLOG — graft-meko-memory

Promote memory to its own top-level `memory/` bucket (ADR-026) and graft the
`dmon-meko` long-term tier as `memory/Dmon.Memory.Meko`.

## Status
- [x] Group 1 — Promote memory to the `memory/` bucket (in-tree move) — commit `b10409d`
- [x] Group 2 — History-preserving import of the dmon-meko long-term tier — merge `06befff`
- [x] Group 3 — Re-wire the grafted package to monorepo conventions
- [x] Group 4 — Solutions
- [x] Group 5 — Verification gates

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

---

## Group 2 — History-preserving import (merge `06befff`)

Grafted the dmon-meko long-term tier via the established recipe (design D4):
- `git clone --no-local -b main` (single-reflog, fresh — the filter-repo precondition) → `uvx git-filter-repo` keeping only `src/Dmon.Memory.Meko/` (→ `memory/Dmon.Memory.Meko/`) and `test/Dmon.Memory.Meko.Tests/` → add remote → `git merge --allow-unrelated-histories`.
- 23 files landed (11 src + 12 test), 7 commits of history retained. Satellite `README.md` (at its repo root, not under `src/`) correctly NOT imported; no `openspec/`, `nuget.config`, vendored nupkgs, `Directory.*`, or the unstarted `add-memory-abstraction` change came across.
- **Decision applied:** design D6 — grafted dmon-meko `main` (`8ed0886`) as-is; the `add-memory-abstraction` refactor (0/36) is deferred to a later monorepo change. Tree is intentionally non-building until Group 3 (no API rewire yet).

## Groups 3 + 4 — Re-wire to monorepo conventions + solutions

Rewired the grafted package so the whole repo is green (design D5), then wired it into `memory.slnx` + `Everything.slnx`.

- **References:** `Dmon.Abstractions` PackageReference → ProjectReference. Added an explicit `core/Dmon.Protocol` ProjectReference (both src + test) — required, not transitive.
- **API-drift adaptation (the substantive part).** The satellite was written when `IMemoryStore.RecordAsync` took `IReadOnlyList<ChatMessage>` (an M.E.AI type). The current contract takes `IReadOnlyList<MessageRecord>` (`Dmon.Protocol.Conversation` — the dmon-owned type, per ADR-016 "no third-party types in the API"). Adapted both impls (`MekoLongTermMemory`, `DisabledLongTermMemory`): parameter type swapped, `SerializeMessages` now extracts text via `r.Parts.OfType<TextPart>()` (semantically equivalent to the old `ChatMessage.Text` concat), `using Microsoft.Extensions.AI` → `using Dmon.Protocol.Conversation`; 4 test files' call sites updated. **Reviewer verified** role+text serialization is faithful (no role dropped, no separator change). This moves the package's surface ONTO dmon-owned types — the correct direction per ADR-016. Architectural note for later: under `CaptureMode=EveryTurn`, non-text parts (tool calls/results) are dropped from long-term capture — faithful to the old behaviour, but a known incompleteness if richer capture is wanted.
- **CPM:** added `ModelContextProtocol` 1.3.0, `Microsoft.Extensions.AI.Abstractions` 10.5.2, `Microsoft.Extensions.Logging.Abstractions`/`Options`/`Options.ConfigurationExtensions` 10.0.8; **bumped the M.E.AI family 10.5.1→10.5.2** (MCP 1.3.0 requires Abstractions ≥10.5.2 — a backwards-compat patch; whole repo, incl. all providers, re-verified green). All grafted refs CPM-bare.
- **Packable hygiene:** `IsPackable=true`, `MinVerTagPrefix=sdk-` (sibling convention), inline `<Version>`/`<Authors>`/URLs/license stripped (URLs + MPL-2.0 license inherited from root `Directory.Build.props`). Authored a fresh package `README.md` (the satellite's lived at repo root and wasn't imported).
- **Test csproj** rebuilt to the `test/Dmon.Memory.Tests` convention (CPM-bare, net10.0/Nullable/ImplicitUsings); live smoke test stays `Category=Live`-gated.

**Reviewer:** Approve with two non-blocking nits (CPM `ModelContextProtocol` out of alphabetical order; missing packed README) — both fixed by the orchestrator directly (config/docs, not feature code): reordered the CPM pin and added the package README + `PackageReadmeFile`. Re-ran gates green afterward.

**Gates:** `dotnet build Everything.slnx -c Release` 0W/0E (31 projects) · `make test` 0 failures (`Dmon.Memory.Meko.Tests` 71 passed; full suite green) · `dotnet pack` → `Dmon.Memory.Meko.0.2.0-alpha.0.42` (skew-guard OK, no missing-readme advisory) · `validate --strict` valid · history preserved through both move and graft (`git log --follow`).
