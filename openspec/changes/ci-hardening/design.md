## Context

Current CI (`.github/workflows/ci.yml`): triggers on `pull_request` only; one ubuntu job running `make build` then `make test` over the whole `Everything.slnx`; `lfs: true` and NuGet cache already present. `make test` (Makefile:41) is `dotnet test Everything.slnx -c $(CONFIG)` — no filter. The Swift app `daemon/Daemon.App` builds via `make daemon-app` (`swift build`, build-only) and has a `DaemonAppTests` target (7 test files) that nothing runs in CI. `spike/ScriptingSpike/ScriptingSpike.csproj` is still a member of `Everything.slnx`.

Per-area solutions already exist: `core.slnx`, `providers.slnx`, `tools.slnx`, `memory.slnx`, `frontends.slnx`, `daemon.slnx`, `services.slnx`. ADR-025 D9 mandates path-filtered CI where a `core/**` change is upstream of everything ("core ⇒ all"). ADR-035 D6 makes the area map the shared source of truth for CI and releases.

## Goals / Non-Goals

**Goals:**
- `main` is built/tested on every push (merge commits included).
- No `Category=Live` test runs in automated CI or default `make test`.
- The Swift app is built and tested in CI on macOS.
- PRs touching one area don't rebuild the world; `core/**` and root config still do.
- `Everything.slnx` and the repo no longer carry throwaway spike/clutter.

**Non-Goals:**
- The release matrix / `release.yml` (change `release-matrix`).
- Reconciling ADR-025 D2's stale prose bucket list (change `docs-adr-spec-realignment`).
- Caching/perf tuning beyond what exists; self-hosted runners.
- Any product-code behavior change.

## Decisions

**D1 — Path-filter via a job-level matrix keyed on `dorny/paths-filter` (or `paths`/`paths-ignore` + a dispatcher), not naive per-directory `paths:`.** ADR-025 D9 is explicit that naive per-directory filtering is insufficient because ProjectReferences make `core/` changes ripple. The workflow computes a set of affected areas from the changed paths, with the rule: if any changed path is under `core/**` or root build config (`Directory.*.props`, `*.slnx`, `.github/**`, `Makefile`, `nuget.config`, `default-core/**`), the affected set is `{Everything}`; otherwise it is the union of the areas owning the changed paths. Each affected area runs `dotnet build/test <area>.slnx`. The exact filter mechanism (a `paths-filter` step feeding a matrix vs a small dispatch job) is a worker-level implementation choice; the *rule* is binding.

**D2 — The area→paths map is the ADR-035 D6 map, using the real tree.** Areas and their globs: `core → core/**`; `providers → providers/**`; `tools → tools/**`; `memory → memory/**`; `frontends → frontends/**`; `daemon-dotnet → daemon/Daemon.Routing/**` + the file-based composition root `daemon/Daemon.cs` (i.e. all .NET under `daemon/` except the Swift `daemon/Daemon.App/**`); `services → services/**`. `core` (and root build config, which per D1 includes `default-core/**`) ⇒ `Everything`. The Swift area `daemon/Daemon.App/**` is **orthogonal** — it links no .NET ProjectReference (it is a wire client), so it is not part of "core ⇒ all"; it triggers only the macOS Swift job. This map is the same one `release-matrix` will consume; keep it in one place (a documented block in the workflow or a small JSON the workflow reads) so the two stay in lockstep.

The file-based composition roots (`default-core/Dmon.cs`, `daemon/Daemon.cs`) are not members of any `.slnx`. `default-core/Dmon.cs` is compiled by `make build` (`build-core` publishes it), so the `Everything` leg validates it — hence `default-core/**` widens to `Everything`. `daemon/Daemon.cs` is not independently compiled by any current CI leg; mapping it to the `daemon` area ensures a change to it does not silently skip CI (it builds `daemon.slnx`/`Daemon.Routing` for ripple coverage), but a standalone compile of that file-based root is not yet a CI gate — a pre-existing gap left to a future change (and `release-matrix`, which owns tag→project resolution for the daemon app family). `samples/**` and `scripts/**` feed `make smoke` (not part of `make build`/`make test`) and are intentionally **not** mapped — a samples/scripts-only change runs no `.slnx` leg by design.

**D3 — Live tests are excluded by filter, not by removing the trait.** `make test` becomes `dotnet test Everything.slnx -c $(CONFIG) --filter "Category!=Live"`. Live tests keep their `[Trait("Category","Live")]`; a new `make test-live` runs `--filter "Category=Live"` for deliberate local use. This preserves the tests while disarming the hang everywhere `make test` is used (CI, release, local). Per-area test invocations in the matrix carry the same filter.

**D4 — Swift CI is a separate macOS job with its own trigger.** macOS runners are ~10× the cost of ubuntu, so the Swift job runs only when `daemon/Daemon.App/**` changes (and on `main` push). It runs `make daemon-app` (build) then a new `make daemon-app-test` = `swift test --package-path daemon/Daemon.App`. Adding the `swift test` Makefile target is itself part of the fix (the audit notes there is no such target today).

**D5 — Spike removal is a delete, not an exclude.** `spike/ScriptingSpike` is throwaway (a resolved scripting spike). Rather than special-casing `Everything.slnx` to skip it, remove the project from the solution and delete the directory. The `monorepo-layout` superset requirement is clarified so "every first-party and test project" cannot be read to mean "keep experimental spikes in the superset." Same disposition for `podcast-talking-points.md`, `terminal.md`, and `test/Dmon.Extensions.Tests` (orphaned).

## Risks / Trade-offs

- **[Path-filter lets a genuinely-needed downstream test be skipped]** → Mitigation: the "core ⇒ all" escape hatch plus routing all root build config to `Everything` covers the ripple cases ADR-025 D9 calls out; leaf areas (`providers`, `tools`, `memory`) have no first-party dependents other than the composition root, which is rebuilt via `core`/root-config triggers. When in doubt the filter widens to `Everything`, never narrower.
- **[macOS job flakiness/queue time slows PRs]** → Mitigation: path-scoped to `daemon/Daemon.App/**`, so most PRs never trigger it; it is not a required check for unrelated areas.
- **[Deleting `test/Dmon.Extensions.Tests` drops real coverage]** → Verify at implementation time that its assertions are dead/duplicated (the component it tested is gone). If any test still exercises live code, relocate rather than delete — a stop-and-ask if it's non-trivial.
- **[Filter matrix passes but a cross-area break slips through on merge]** → The `push: branches:[main]` trigger runs the *affected* set on the merge commit; a core/root-config change (the risky kind) always runs `Everything`. Residual risk is a genuinely cross-area non-core change, which ADR-025 OQ-C flags as an open topology question — out of scope here.

## Migration Plan

Pure CI/build/repo-hygiene change; no runtime or format impact. Rollback is reverting the commit(s). The first `main` push after merge exercises the new trigger.
