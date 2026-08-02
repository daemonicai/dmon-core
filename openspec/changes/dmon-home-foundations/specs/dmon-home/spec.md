## ADDED Requirements

### Requirement: The macOS host lives in the `home/` bucket

The repository SHALL carry a `home/` top-level role bucket holding the macOS host product `dmon-home`. The bucket SHALL contain the product's requirements document, the XcodeGen manifest, the `DmonHomeApp` application target, and the host's local Swift packages. `home/` holds no .NET projects and SHALL NOT carry a `.slnx`.

#### Scenario: The host resides in the home bucket

- **WHEN** the repository is inspected
- **THEN** the macOS host product resides under `home/`, not under `daemon/`, `frontends/`, or `services/`
- **AND** no `home.slnx` exists, because `home/` has no C# member projects

#### Scenario: The existing Swift app is untouched

- **WHEN** this change is applied
- **THEN** `daemon/Daemon.App` still builds and tests via its existing `make` targets
- **AND** its CI job and release artifact are unchanged

### Requirement: The host targets Apple Silicon only

The host SHALL be built for `arm64` only and SHALL NOT produce an `x86_64` slice. The MLX runtime the host depends on requires Apple Silicon, and the host runs an MLX speech sidecar locally, so an Intel build could be compiled but never function. The build SHALL NOT present an ambiguous choice of architecture.

#### Scenario: The built binary is arm64 only

- **WHEN** the built `.app` bundle's executable is inspected
- **THEN** it reports `arm64` as its only architecture

#### Scenario: No ambiguous destination

- **WHEN** the app is built from the command line
- **THEN** the build does not warn that it is selecting between multiple matching destinations of differing architecture

### Requirement: The Xcode project is generated, never hand-edited

The host's Xcode project SHALL be generated from a checked-in XcodeGen manifest (`project.yml`). The generated `.xcodeproj` SHALL NOT be hand-edited, and SHALL be reproducible from the manifest alone.

#### Scenario: Project regenerates from the manifest

- **WHEN** the generated `.xcodeproj` is deleted and XcodeGen is run against `project.yml`
- **THEN** a working project is produced and the app builds from it without further edits

### Requirement: Application logic lives in local Swift packages

All host logic except the application shell SHALL live in local Swift Package Manager packages inside `home/`, so it is testable headlessly without an Xcode host application. The application target SHALL be a thin shell that wires those packages together. A package SHALL NOT be created before it contains real code.

#### Scenario: Logic is testable without the app

- **WHEN** `swift test` is run against the host's package manifest
- **THEN** the supervision and gateway-client tests execute and pass without launching the application

#### Scenario: No empty placeholder packages

- **WHEN** the host's packages are inspected
- **THEN** every declared package target contains source code that ships in this change
- **AND** no package exists solely as a placeholder for a later phase

### Requirement: The host ships as an unsandboxed `.app` bundle that can prompt for microphone access

The host SHALL be built as a genuine `.app` bundle with App Sandbox disabled, and its `Info.plist` SHALL carry `NSMicrophoneUsageDescription`. Without both, macOS returns silence from the microphone and never presents a permission prompt.

#### Scenario: Bundle carries the microphone usage description

- **WHEN** the built `.app` bundle's `Info.plist` is inspected
- **THEN** it contains a non-empty `NSMicrophoneUsageDescription` string

#### Scenario: App Sandbox is disabled

- **WHEN** the built `.app` bundle's entitlements are inspected
- **THEN** the App Sandbox entitlement is absent or false, so the host may spawn interpreters outside its container and read model files outside its container

#### Scenario: The microphone permission prompt appears

- **WHEN** a human launches the built `.app` bundle for the first time and the host requests microphone authorisation
- **THEN** macOS presents the microphone permission prompt showing the configured usage description
- **AND** the host records the resulting authorisation status

### Requirement: Child process output is streamed to a log pane

The host SHALL stream the stdout and stderr of every supervised child into a log pane viewable in the application, tagged by child, so failures are diagnosable without leaving the app.

#### Scenario: Child output is visible

- **WHEN** a supervised child writes to stdout or stderr
- **THEN** that output appears in the host's log pane attributed to that child

#### Scenario: Output survives a child restart

- **WHEN** a supervised child crashes and is restarted
- **THEN** the log pane retains the output preceding the crash

### Requirement: The host holds an activity assertion while the gateway is enabled

The host SHALL hold a `ProcessInfo` activity assertion covering user-initiated work and idle system sleep while the gateway is enabled, and SHALL release it when the gateway is disabled. The host SHALL NOT use the unconditional `LSAppNapIsDisabled` mechanism.

#### Scenario: Assertion is held while serving

- **WHEN** the gateway is enabled
- **THEN** the host holds an activity assertion for the duration

#### Scenario: Assertion is released when not serving

- **WHEN** the gateway is disabled
- **THEN** the host releases the activity assertion

### Requirement: The host accepts typed turn input and renders streamed replies

The host SHALL provide a text input in its UI that submits a turn to the attached session, and SHALL render the streamed reply incrementally as it arrives rather than only on completion.

#### Scenario: A typed turn produces a rendered reply

- **WHEN** a user types a message and submits it while attached to a session
- **THEN** the host submits it as a turn and renders the streamed response incrementally until the turn ends

#### Scenario: Input is refused when not attached

- **WHEN** a user attempts to submit a turn while no session is attached
- **THEN** the host refuses the submission and surfaces the unattached state rather than failing silently
