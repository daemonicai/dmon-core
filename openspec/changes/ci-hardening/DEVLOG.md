# DEVLOG — ci-hardening

Running narrative of implementation decisions, one section per block. Newest-relevant context is the architect's cross-block memory.

## Status

- **Block 1 (tasks 1.1–1.3) — DONE.** Makefile-only. Committed.
- **Block 2 (tasks 2.1–2.2) — DONE.** Spike + root-clutter removal. Committed.
- **Task 2.3 — user decision made: RENAME/RELOCATE, not delete** (see Block 2 notes). Next block.
- Remaining: 2.3 (rename), 2.4 (gate — tick after 2.3), Section 3 (CI workflow, 3.1–3.4), Section 4 (gates, 4.1–4.2).

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
