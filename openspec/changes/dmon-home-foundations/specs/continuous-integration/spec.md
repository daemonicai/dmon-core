## MODIFIED Requirements

### Requirement: Swift app is built and tested on macOS

CI SHALL build and test each first-party Swift package on a macOS runner via that package's own `make` build target and a matching `make` test target that runs `swift test`. The Swift packages are `daemon/Daemon.App` (the dmonium menu-bar app, via `make daemon-app` and `make daemon-app-test`) and the `home/` macOS host `dmon-home`. Because macOS runners are costly, each Swift package SHALL have its **own independent path filter** scoped to that package's paths (and `main` pushes), so a change to one Swift package does not run the other's job. The Swift packages are orthogonal to the .NET "core ⇒ all" rule and SHALL NOT be triggered by .NET-area changes.

#### Scenario: Swift change runs the macOS job

- **WHEN** a change touches files under a Swift package's paths (e.g. `daemon/Daemon.App/**` or `home/**`)
- **THEN** a macOS CI job runs that package's `make` build and test targets, and its Swift tests pass
- **AND** the other Swift package's macOS job does not run

#### Scenario: Non-Swift change skips the macOS job

- **WHEN** a change touches no files under any Swift package's paths
- **THEN** no macOS Swift job runs
