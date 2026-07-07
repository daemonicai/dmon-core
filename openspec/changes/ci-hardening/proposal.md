## Why

CI has blind spots that let `main` break silently and leave whole components untested (AUDIT.md issue 4, High; issue 10, Medium). `.github/workflows/ci.yml` triggers on `pull_request` only, so a merge commit — which differs from the PR head — is never built. The Swift menu-bar app (`daemon/Daemon.App`, 7 test targets) has **zero** CI. `make test` runs the whole `Everything.slnx` with no `--filter "Category!=Live"`, so the documented Meko live-smoke hang stays armed. ADR-025's path-filtered "core ⇒ all" CI is unimplemented (every PR builds everything). And `spike/ScriptingSpike` — throwaway code — is still in `Everything.slnx`, compiled on every build, alongside tracked root clutter.

## What Changes

- **Test `main` post-merge:** add `push: branches: [main]` to `ci.yml` (keep `pull_request`).
- **Disarm the live-smoke hang:** add `--filter "Category!=Live"` to the `make test` target so `Category=Live` tests (e.g. `MekoLiveSmokeTests`) never run in CI or local `make test`; add a `make test-live` (or documented opt-in) for when they are wanted.
- **Give the Swift app CI:** add a `make daemon-app-test` target (`swift test --package-path daemon/Daemon.App`) and a **macOS** CI job that runs `make daemon-app` + `make daemon-app-test`, triggered only on `daemon/Daemon.App/**` changes.
- **Path-filtered "core ⇒ all" CI** (ADR-025 D9, area map per ADR-035 D6): a PR/push touching only one area builds/tests that area's `.slnx`; a change under `core/**` or root build config (`Directory.*.props`, `*.slnx`, `.github/**`, `Makefile`) builds/tests `Everything.slnx`.
- **Build hygiene:** remove `spike/ScriptingSpike` from the repo and from `Everything.slnx`; delete tracked root clutter (`podcast-talking-points.md`, `terminal.md`) and the orphaned `test/Dmon.Extensions.Tests` (named for a component that no longer exists). *(This absorbs the separately-planned `build-hygiene` follow-up.)*

Out of scope: the granular NuGet + app-artifact **release matrix** (its own change `release-matrix`, unblocked by ADR-035). This change does not touch `release.yml`.

## Capabilities

### New Capabilities
- `continuous-integration`: what the CI workflow guarantees — triggers (PR + `main` push), path-filtered per-area build/test with the "core ⇒ all" rule, a macOS Swift build+test job, and exclusion of `Category=Live` tests from automated runs.

### Modified Capabilities
- `monorepo-layout`: the "Per-area solutions and a root superset" requirement is clarified — `Everything.slnx` includes every **shipped** first-party and test project but SHALL NOT include throwaway/experimental spikes; such spikes SHALL NOT be tracked in the repository.

## Impact

- **Workflows:** `.github/workflows/ci.yml` (triggers + path-filtered matrix + macOS job). `release.yml` untouched.
- **Makefile:** `test` target gains `--filter "Category!=Live"`; new `daemon-app-test` (and `test-live`) targets.
- **Repo tree:** delete `spike/ScriptingSpike/`, `podcast-talking-points.md`, `terminal.md`, `test/Dmon.Extensions.Tests/`; remove the spike from `Everything.slnx`.
- **ADR:** implements ADR-025 D9 and ADR-035 D6 (shared area map). ADR-025 D2's stale bucket enumeration (prose) is *not* reconciled here — that doc drift is left to `docs-adr-spec-realignment`; CI uses the real tree paths.
