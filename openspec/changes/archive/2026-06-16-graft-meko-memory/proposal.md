## Why

ADR-026 establishes that memory is a contract-set + N-backends + facade family (like `providers/` and `tools/`), not an ADR-023 chat-pipeline middleware — so it gets its own top-level `memory/` bucket. This change realises that decision and, in the same move, grafts the `dmon-meko` satellite (the durable long-term tier) into the new bucket as the next monorepo consolidation phase (ADR-025).

## What Changes

- **Promote memory to a `memory/` bucket.** Move `middleware/Dmon.Memory` → `memory/Dmon.Memory` (history-preserving), and its tests to match the `test/` convention. Contracts (`Dmon.Abstractions.Memory`) STAY in `core/` — only implementations move.
- **Add `memory.slnx`; drop the now-memberless `middleware.slnx`.** Repath `Everything.slnx` and the `Makefile`. `middleware/` is retained as a *named ADR-023 role with no current members* — the physical directory and its `.slnx` go away until a real `IDmonMiddleware` ships. **BREAKING (structural):** the `Dmon.Memory` project path changes; any external reference to the old `middleware/Dmon.Memory` path breaks (no published consumers — pre-1.0, no migration owed).
- **Graft `dmon-meko` → `memory/Dmon.Memory.Meko`.** Import via the established filter-repo + `--allow-unrelated-histories` recipe, history preserved. No type rename — `Dmon.Memory.Meko` keeps its name (the earlier "→ `Dmon.Middleware.Meko`" idea is dead). Default: graft current `dmon-meko` `main` as-is; its unstarted `add-memory-abstraction` refactor lands later as its own monorepo change.
- **Rewire the grafted package to monorepo conventions.** `Dmon.Abstractions` (and `Dmon.Protocol` if used directly) become `ProjectReference`s; third-party pins (ModelContextProtocol, M.E.AI.Abstractions, …) move to root CPM `Directory.Packages.props`; wire into `memory.slnx` + `Everything.slnx`; skew-guard intact; pack produces `Dmon.Memory.Meko` on the protocol cycle.
- **Update the `monorepo-layout` standing contract** to add the `memory/` bucket, `memory.slnx`, the `Dmon.Memory.<Name>` naming family, and to recast `middleware/` as a possibly-memberless named role.

## Capabilities

### New Capabilities
- `memory-meko`: the behavioural contract of the long-term Meko backend — `ILongTermMemory` over Meko's `memory_*` MCP tools (Streamable HTTP, `mko_tkn_` bearer); `MemoryScope` → `run_id` mapping (Session = hex-normalized session id; durable scopes omit `run_id`); lazy per-session `conversation_id`; opt-in turn capture (`MekoCaptureMode.None` default); eventual-consistency (not read-your-writes); `FlushAsync` best-effort no-op; a disabled no-op store when no API key; and the `AddMekoLongTermMemory(...)` DI verb (falls back to the no-op store on empty key). Authored as this change's spec delta (all `ADDED`), synced to `openspec/specs/memory-meko/` on archive — the same provenance pattern Phase 1/2 used for `llamacpp-provider`/`dmail-tool`. The satellite's own `openspec/` (server/abstraction-side capabilities) is deliberately not imported.

### Modified Capabilities
- `monorepo-layout`: the **Top-level role buckets** requirement gains `memory/` (memory backend implementations + facade) and recasts `middleware/` as a named role that MAY have no members; the **Per-area solutions** requirement adds `memory.slnx` and allows a role with no members to have no `.slnx` (so `middleware.slnx` is removed); the **Package naming families** requirement adds the `Dmon.Memory.<Name>` family for memory backends. No other requirement changes.

## Impact

- **Moved:** `middleware/Dmon.Memory` → `memory/Dmon.Memory` (+ its test project). **Removed:** `middleware/` directory and `middleware.slnx`. **Added:** `memory/Dmon.Memory.Meko` (+ `test/Dmon.Memory.Meko.Tests`), `memory.slnx`.
- **Solutions/build:** `Everything.slnx`, `Makefile` repathed; root CPM `Directory.Packages.props` gains the Meko backend's third-party pins.
- **No API/contract change:** `IMemory`/`IShortTermMemory`/`ILongTermMemory`, `AddDmonMemory()`, `AddMekoLongTermMemory()`, and the `IMemory` facade are untouched — placement/taxonomy only.
- **Binding ADRs:** ADR-026 (this change realises it), ADR-025 (D4 ProjectReference, D13 import mechanics), ADR-023 (naming families), ADR-024 (protocol-cycle versioning + skew-guard).
- **Source repo:** `dmon-meko` is left intact-but-live; only the long-term backend is absorbed (tagged `absorbed-into-dmon-core` on its `main` on archive, per the Phase 1/2 pattern).
