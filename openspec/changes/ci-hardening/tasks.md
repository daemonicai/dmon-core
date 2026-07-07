## 1. Makefile test/target changes

- [x] 1.1 Change the `test` target to `dotnet test Everything.slnx -c $(CONFIG) --filter "Category!=Live"` so live-category tests never run by default (design D3).
- [x] 1.2 Add a `make test-live` target running `--filter "Category=Live"` (opt-in), and add it to `.PHONY`.
- [x] 1.3 Add a `make daemon-app-test` target running `swift test --package-path daemon/Daemon.App`; add to `.PHONY` (design D4).

## 2. Build hygiene

- [x] 2.1 Remove `spike/ScriptingSpike` from `Everything.slnx` and delete the `spike/ScriptingSpike/` directory (design D5).
- [x] 2.2 Delete tracked root clutter: `podcast-talking-points.md`, `terminal.md`.
- [x] 2.3 Verified the orphaned `test/Dmon.Extensions.Tests/` still holds live, unique coverage of `core/Dmon.Abstractions` types (`DmonAIFunctionFactory`, `DmonMiddlewareAttribute`, `IToolExtension`), so the stop-and-ask fired and — per user decision — it was **renamed/relocated** to `test/Dmon.Abstractions.Tests/` (dir, `.csproj`, namespace, `Everything.slnx` **and** `core.slnx` entries) rather than deleted, preserving all 26 tests.
- [x] 2.4 Confirm `make build` + `make test` remain green after the removals (no dangling solution references).

## 3. CI workflow

- [ ] 3.1 Add `push: branches: [main]` to `ci.yml` triggers (keep `pull_request`).
- [ ] 3.2 Implement the path-filtered "core ⇒ all" matrix (design D1/D2): compute affected areas from changed paths using the area→paths map; any `core/**` or root-build-config change (`Directory.*.props`, `*.slnx`, `.github/**`, `Makefile`, `nuget.config`) ⇒ build/test `Everything.slnx`; otherwise build/test each affected area's `.slnx`. Keep the area map in one documented place (shared with `release-matrix`).
- [ ] 3.3 Ensure every build/test invocation (full and per-area) uses the live-excluding filter (via `make test` / an equivalent `--filter "Category!=Live"`).
- [ ] 3.4 Add a macOS job (`runs-on: macos-*`) scoped to `daemon/Daemon.App/**` (and `main` push) that runs `make daemon-app` then `make daemon-app-test` (design D4). Keep the existing ubuntu `lfs: true` checkout and NuGet cache for the .NET jobs.

## 4. Gates

- [ ] 4.1 `make build` clean (TreatWarningsAsErrors); `env -u MEKO_API_KEY make test` green; `make daemon-app-test` green on macOS (or documented human-verify recipe if no macOS runner available locally); `openspec validate ci-hardening --strict`.
- [ ] 4.2 Validate the workflow YAML parses and the path-filter logic is exercised (e.g. `act` dry-run or a documented manual trigger matrix); confirm `Everything.slnx` no longer references the removed projects.
