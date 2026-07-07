# DEVLOG — ci-hardening

Running narrative of implementation decisions, one section per block. Newest-relevant context is the architect's cross-block memory.

## Status

- **Block 1 (tasks 1.1–1.3) — DONE.** Makefile-only. Committed.
- Remaining: Section 2 (build hygiene, 2.1–2.4), Section 3 (CI workflow, 3.1–3.4), Section 4 (gates, 4.1–4.2).

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
- **Heads-up for Section 2 / task 2.3:** orphaned `test/Dmon.Extensions.Tests/` is currently a member of `Everything.slnx` and contains three live test files (`DaemonAIFunctionFactoryTests.cs`, `DmonMiddlewareAttributeTests.cs`, `IDaemonExtensionContractTests.cs`). The 2.3 "verify coverage is dead/duplicated or stop-and-ask" condition is real and needs genuine investigation before deletion.
