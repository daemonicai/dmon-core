## 1. Package/project prerequisites

- [x] 1.1 Make `memory/Dmon.Memory/Dmon.Memory.csproj` packable: `IsPackable=true`, `PackageId=Dmon.Memory`, inheriting the shared package metadata other packages get from `Directory.Build.props` (ADR-035 D5).
- [x] 1.2 Verify the ADR-024 D7 skew-guard exists (`Directory.Build.props` errors any packable project whose `Major.Minor` ≠ `ProtocolVersion.Current`); add it if missing (design D7).
- [x] 1.3 Set `MinVerTagPrefix` for every NuGet-family package to its ADR-035 D7 prefix (`core/…-v`, `providers/<name>-v`, `tools/<name>-v`, `memory/<name>-v`, `frontends/dmon-v`, `frontends/ndmon-v`); ensure no prefix is a prefix of another (design D1). Prefer a per-area `Directory.Build.props` where it reduces repetition.
- [x] 1.4 Remove `make network`'s hardcoded `--version 0.1.0` so `ndmon` versions via MinVer (design D4).

## 2. Shared area map

- [x] 2.1 Ensure the area→paths map (ADR-035 D6) lives in exactly one shared location. If `ci-hardening` left it inline in `ci.yml`, extract it to a shared file; point both `ci.yml` and `release.yml` at it (design D6). If already shared, consume it here.

## 3. NuGet-family release job

- [x] 3.1 Rewrite `release.yml` trigger to per-package tags `*/**-v*` (retire `sdk-*`/`dmon-*`/`core-*`).
- [x] 3.2 Replace the `case "$TAG"` subset logic with map-driven selection: resolve the pushed `<area>/<name>-v…` tag to its project path(s) via the shared map, then `dotnet pack --no-build` + `dotnet nuget push --skip-duplicate` (keep the `.snupkg` symbol push, generalized to any package that produces symbols) (design D1/D3).
- [x] 3.3 Confirm every NuGet-family package (ADR-035 D7 table) is reachable by some tag prefix — no package left without a release path.

## 4. Cycle-wave

- [x] 4.1 Add a reproducible cycle-wave helper (`scripts/release-wave.sh` and/or `make release-wave X.Y`) that tags every NuGet-family package at `<prefix>X.Y.0`, guarded by the skew-check so it only runs when `ProtocolVersion.Current == X.Y` (design D2).

## 5. App-artifact family

- [x] 5.1 Add an app-artifact job to `release.yml` triggered by `app/<name>-v*` tags that builds the dmonium bundle (`make daemon-app` packaging) and attaches it to a GitHub Release (design D3). Unsigned first cut is acceptable; label it unsigned (signing/notarization deferred — ADR-035 Open Question).
- [x] 5.2 Investigate whether `Dmon.Desktop` has a working `dotnet publish` bundle recipe. If straightforward, add its `app/desktop-v*` packaging; if non-trivial, document it as deferred to a follow-up and ship dmonium + the full NuGet family (design Open Questions). Log the deferral explicitly.

## 6. Gates

- [x] 6.1 `make build` clean; `env -u MEKO_API_KEY make test` green; `openspec validate release-matrix --strict`.
- [x] 6.2 Dry-run the release paths without publishing (e.g. `dotnet pack` each mapped project succeeds; the wave script emits the correct tag set; workflow YAML parses). Confirm the skew-guard rejects a deliberately mis-`Major.Minor`'d pack.
- [ ] 6.3 Human-verify recipe (no secret in automation): a copy-pasteable sequence to push one real per-package tag and confirm nuget.org receipt, plus one `app/*` tag and confirm the GitHub Release attachment — to be run by the maintainer, then ticked.
