## MODIFIED Requirements

### Requirement: Tag-driven release pipeline
The system SHALL provide a tag-triggered release workflow that publishes packages and artifacts on **per-package tags of the form `<area>/<name>-v<X.Y.Z>`** (ADR-035 D1). The workflow SHALL map a pushed tag to its single target project via that project's own `<MinVerTagPrefix>` — the single source of tag→project truth (ADR-035 D6/D7); the shared area→paths map (`.github/area-map.yml`) remains the CI path-filter's and is NOT duplicated as a tag-resolution map in the release workflow (it is area-granular and cannot derive a `<name>` segment's exact project). For NuGet-family tags it SHALL run `dotnet pack` + `dotnet nuget push` to nuget.org (including `.snupkg` symbols where produced). Publishing to nuget.org SHALL use **Trusted Publishing (OIDC)**: the release job SHALL be granted `id-token: write`, mint a short-lived nuget.org API key at run time by exchanging a GitHub OIDC token via a trusted-publishing login step (`NuGet/login`), and push using only that ephemeral key. The workflow SHALL NOT reference a long-lived `NUGET_API_KEY` (or any other stored nuget.org publishing secret); the only nuget.org identity input SHALL be the account's non-secret profile username. The pull-request CI SHALL NOT publish. Every NuGet-family package (ADR-035 D7) SHALL have a release path; the legacy `sdk-*`/`dmon-*`/`core-*` tag lines are retired. A protocol-cycle boundary SHALL be releasable as a wave that tags every NuGet-family package at `<prefix>X.Y.0` — including unchanged packages — so that `@X.Y.*` always resolves (ADR-035 D2).

#### Scenario: Publish a single package on its per-package tag
- **WHEN** a tag `providers/anthropic-v0.2.5` is pushed
- **THEN** the release workflow packs and pushes only `Dmon.Providers.Anthropic` at `0.2.5` to nuget.org

#### Scenario: Publish uses a short-lived OIDC key, not a stored secret
- **WHEN** the release job runs on a NuGet-family tag push
- **THEN** it requests a GitHub OIDC token (the job declares `id-token: write`), exchanges it via the trusted-publishing login step for a temporary nuget.org API key, and `dotnet nuget push` authenticates with that temporary key — with no long-lived `NUGET_API_KEY` secret referenced anywhere in the workflow

#### Scenario: Pull request never publishes
- **WHEN** a pull request is opened
- **THEN** no package or artifact is published

#### Scenario: Cycle wave re-releases the whole set
- **WHEN** a protocol cycle opens at `X.Y` and the cycle-wave release is run
- **THEN** every NuGet-family package is tagged and published at `X.Y.0`, including packages with no source change since the previous cycle
