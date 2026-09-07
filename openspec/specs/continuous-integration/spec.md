## Purpose

Define the standing contract for the dmon monorepo's continuous-integration pipeline (ADR-025 path-filtered CI, ADR-035 shared area→paths map): what events trigger CI, the path-filtered per-area build/test scoping with the "core ⇒ all" rule, the macOS Swift-app job, and the exclusion of live-category tests from automated runs.

## Requirements

### Requirement: CI triggers on pull requests and main pushes

The CI workflow SHALL run on both `pull_request` events and `push` events to the `main` branch, so that the merge commit on `main` (which differs from any PR head) is built and tested after merge.

#### Scenario: Pull request is validated
- **WHEN** a pull request targeting `main` is opened or updated
- **THEN** CI runs the affected build/test jobs against the PR head

#### Scenario: Main is validated after merge
- **WHEN** a commit is pushed to `main` (e.g. a merge commit)
- **THEN** CI runs the affected build/test jobs against that commit

### Requirement: Path-filtered per-area CI with the "core ⇒ all" rule

CI SHALL build and test only the areas affected by the changed paths, using a single area→paths map (the map shared with the release pipeline per ADR-035 D6). A change under `core/**` or under root build configuration (`Directory.*.props`, any `*.slnx`, `.github/**`, `Makefile`, `nuget.config`) is upstream of the whole repository and SHALL cause the full `Everything.slnx` to be built and tested. A change confined to a single leaf area SHALL build and test only that area's `.slnx`.

#### Scenario: Leaf-area change builds only that area
- **WHEN** a change touches only files under `providers/**`
- **THEN** CI builds and tests `providers.slnx` and does not build unrelated areas

#### Scenario: Core change builds everything
- **WHEN** a change touches any file under `core/**` or a root build-configuration file
- **THEN** CI builds and tests `Everything.slnx`

### Requirement: Swift app is built and tested on macOS

CI SHALL build and test each first-party Swift package on a macOS runner via that package's own `make` build target and a matching `make` test target that runs `swift test`. The Swift packages are `daemon/Daemon.App` (the dmonium menu-bar app, via `make daemon-app` and `make daemon-app-test`) and the `home/` macOS host `dmon-home`. Because macOS runners are costly, each Swift package SHALL have its **own independent path filter** scoped to that package's paths (and `main` pushes), so a change to one Swift package does not run the other's job. The Swift packages are orthogonal to the .NET "core ⇒ all" rule and SHALL NOT be triggered by .NET-area changes.

#### Scenario: Swift change runs the macOS job

- **WHEN** a change touches files under a Swift package's paths (e.g. `daemon/Daemon.App/**` or `home/**`)
- **THEN** a macOS CI job runs that package's `make` build and test targets, and its Swift tests pass
- **AND** the other Swift package's macOS job does not run

#### Scenario: Non-Swift change skips the macOS job

- **WHEN** a change touches no files under any Swift package's paths
- **THEN** no macOS Swift job runs

### Requirement: Live-category tests are excluded from automated runs

The default test target (`make test`) SHALL exclude tests traited `Category=Live` (e.g. `MekoLiveSmokeTests`, which dial a live external endpoint and can hang when a secret is present). Live tests SHALL retain their trait and SHALL be runnable via a dedicated opt-in target (`make test-live`). CI SHALL use the excluding target.

#### Scenario: make test skips live tests
- **WHEN** `make test` is run (locally or in CI)
- **THEN** tests traited `Category=Live` are not executed and the run does not hang on a live endpoint

#### Scenario: Live tests remain runnable on demand
- **WHEN** `make test-live` is run
- **THEN** the `Category=Live` tests are executed
