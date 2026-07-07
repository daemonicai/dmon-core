# DEVLOG — ci-hardening

Running narrative of implementation decisions, one section per block. Newest-relevant context is the architect's cross-block memory.

## Status

- **Block 1 (tasks 1.1–1.3) — DONE.** Makefile-only. Committed.
- **Block 2 (tasks 2.1–2.2) — DONE.** Spike + root-clutter removal. Committed.
- **Block 3 (task 2.3 rename + 2.4 gate) — DONE.** `Dmon.Extensions.Tests` → `Dmon.Abstractions.Tests`. Committed. Section 2 complete.
- Remaining: Section 3 (CI workflow, 3.1–3.4), Section 4 (gates, 4.1–4.2).

## Block 1 — Makefile live-safe test targets + Swift test target (1.1–1.3)

**Deliverable:** `make test` now excludes `Category=Live` by default; opt-in `make test-live`; new `make daemon-app-test` (`swift test`).

**Changes (Makefile only):**
- `test:` → `dotnet test Everything.slnx -c $(CONFIG) --filter "Category!=Live"` (design D3). `test: build-core` prerequisite preserved.
- New `test-live: build-core` → `--filter "Category=Live"`. Added to `.PHONY`.
- New `daemon-app-test:` → `swift test --package-path daemon/Daemon.App` (design D4). Added to `.PHONY`. Runs in default (debug) config — faithful to D4 which specifies no `-c` flag; CI runs `make daemon-app` (release build) first.

**Verification:** `make build` clean; `env -u MEKO_API_KEY make test` green (all projects, no failures); `make daemon-app-test` green (DaemonAppTests suite); `make test-live` confirmed correct filter selection (only `Dmon.Memory.Meko.Tests` selected its live test, hit real endpoint as expected). `openspec validate ci-hardening --strict` valid. Reviewer signed off — diff is exactly the three tasks, nothing extraneous.

**Notes for next architect:**
- Live tests keep their `[Trait("Category","Live")]` — filter-based exclusion, trait untouched (D3).
- **Section 3 (CI) will consume these targets:** `make test` (live-safe), `make daemon-app` + `make daemon-app-test` (macOS job).

## Block 2 — spike + root-clutter removal (2.1–2.2)

**Changes:** removed the `/spike/` folder block from `Everything.slnx`; `git rm` on the whole `spike/ScriptingSpike/` tree (Program.cs, SPIKE.md, ScriptingSpike.csproj — resolved Dotnet.Script.Core embedding spike, D5); `git rm` on root clutter `podcast-talking-points.md`, `terminal.md`. Delete-not-exclude per D5 / ADR-025 D9.

**Verification:** `make build` clean; `env -u MEKO_API_KEY make test` green (all projects incl. Dmon.Extensions.Tests 26/26 — the deferred 2.3 project, untouched); `openspec validate ci-hardening --strict` valid. Reviewer signed off — diff is exactly 2.1/2.2, `/test/` block intact, no dangling solution reference.

**Task 2.3 — user decision (RESOLVED, RENAME not delete):** the orphaned `test/Dmon.Extensions.Tests/` is orphaned in NAME only — its `.csproj` already `ProjectReference`s `core/Dmon.Abstractions`, and its three test files provide **live, unique** unit coverage of production types in `core/Dmon.Abstractions/Extensions/`: `DmonAIFunctionFactory` (DaemonAIFunctionFactoryTests.cs, ~10 facts on `.Create` — NOT duplicated elsewhere), `DmonMiddlewareAttribute` (Priority default/custom), `IToolExtension` contract. Deleting per 2.3's literal text would drop real coverage. **User chose: rename/relocate `test/Dmon.Extensions.Tests/` → `test/Dmon.Abstractions.Tests/`** (project + namespace `Dmon.Extensions.Tests`→`Dmon.Abstractions.Tests`, update `Everything.slnx` `/test/` entry), preserving all three files. This is the next block; task 2.4 (green-gate over ALL Section-2 removals) ticks after 2.3 lands.

## Block 3 — rename Dmon.Extensions.Tests → Dmon.Abstractions.Tests (2.3) + Section-2 gate (2.4)

**Changes (all via `git mv`, history preserved):** renamed dir + `.csproj`; flipped `namespace Dmon.Extensions.Tests;` → `namespace Dmon.Abstractions.Tests;` in the 3 test files (namespace line only — no logic/using/assertion/ProjectReference change). Updated BOTH solution files: `Everything.slnx:43` **and** `core.slnx:11` (the latter was the gotcha the task text omitted — `core.slnx` also carried the reference). csproj needed no internal edit (no explicit `<AssemblyName>`/`<RootNamespace>`; both default off the new filename). Task 2.3's text amended from "Delete…" to record the rename disposition for archive fidelity.

**Verification (orchestrator-run, authoritative):** `make build` clean (0 errors); `env -u MEKO_API_KEY make test` fully green — `Dmon.Abstractions.Tests` 26/26, `Dmon.Core.Tests` 610/611+1 skip (the unrelated flaky `WizardEngineTests.InvalidChooseOneAnswer_RePromptsStep` passed this run), all other projects green; `openspec validate ci-hardening --strict` valid; no residual `Dmon.Extensions.Tests` in any `.cs`/`.csproj`/`.slnx`. Reviewer signed off — diff is exactly the rename, git detects renames (R095–R100).

**Section 2 complete.** Next: Section 3 CI workflow (3.1–3.4) — the substantive block. It consumes the Block-1 Makefile targets (`make test` live-safe, `make daemon-app`/`make daemon-app-test`) and implements the path-filtered "core ⇒ all" matrix per design D1/D2 + ADR-024/025/035 area→paths map (must be kept in ONE documented place shared with `release-matrix`), plus the macOS Swift job (D4), `push: branches: [main]` trigger, and `lfs: true`/NuGet cache retention on the .NET jobs.
