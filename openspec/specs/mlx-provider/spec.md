## Purpose

Define the `Dmon.Providers.Mlx` provider: a headless, Apple-Silicon-only local inference runtime that runs each model as its own `mlx_lm.server` process on a fixed port, provisioned through a `uv`-managed Python environment with a pinned `mlx_lm`. It exposes two keyed runtimes (`"firstline"` and `"escalation"`) with verified gemma-4 quant defaults, follows an attach-first start/stop lifecycle, confirms readiness via completion/`/health` (never `/v1/models`), probes tool-calling once, accommodates gemma-4's separate `reasoning` output, and ships composition verbs that replace the removed `UseOmlx` verb.

## Requirements

### Requirement: Platform and uv applicability check
The provider SHALL implement `IsApplicable()` as a cheap, synchronous check with no network or process I/O: it returns true only when the host is Apple Silicon (macOS, arm64) AND the `uv` executable is discoverable on PATH. When not applicable it returns false with a remediation message naming the missing prerequisite.

#### Scenario: Applicable on Apple Silicon with uv present
- **WHEN** `IsApplicable()` runs on macOS/arm64 with `uv` on PATH
- **THEN** it returns true without starting any process or making network calls

#### Scenario: Not applicable when uv is missing
- **WHEN** `IsApplicable()` runs and `uv` is not found on PATH
- **THEN** it returns false and the remediation message instructs the user to install `uv`

#### Scenario: Not applicable off Apple Silicon
- **WHEN** `IsApplicable()` runs on a non-macOS or non-arm64 host
- **THEN** it returns false

### Requirement: uv-managed Python environment with pinned mlx_lm
The provider SHALL provision its Python runtime through a `uv`-managed virtual environment that owns a pinned interpreter and pins `mlx_lm` to at least the version that ships the gemma-4 tool parser (`tool_parsers/gemma4.py`, ≥ 0.31.3). The provider SHALL NOT depend on the host's system Python. Environment provisioning SHALL occur inside `EnsureRunningAsync()`, never in `IsApplicable()`.

#### Scenario: Environment built before first server launch
- **WHEN** `EnsureRunningAsync()` runs and the managed venv does not yet exist
- **THEN** the provider builds it via `uv` with the pinned interpreter and pinned `mlx_lm` before launching any server

#### Scenario: mlx_lm below the pinned version is rejected
- **WHEN** the resolved `mlx_lm` version is older than the pinned minimum
- **THEN** the provider fails fast with an error rather than launching a server that would silently drop tool calls

### Requirement: One server process per model on a fixed port
The provider SHALL run each model as its own `mlx_lm.server` process bound to a configured fixed port and SHALL retain the real server process handle for lifecycle control. It SHALL NOT use dynamic port assignment for the escalation runtime.

#### Scenario: First-line and escalation run as separate processes
- **WHEN** both the first-line and escalation runtimes are started
- **THEN** each is a distinct `mlx_lm.server` process on its own fixed port, each with a retained process handle

### Requirement: Two keyed runtimes with default model pairing
The provider SHALL expose two keyed runtimes: `"firstline"` defaulting to `mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit` and `"escalation"` defaulting to `mlx-community/gemma-4-26B-A4B-it-qat-nvfp4`. Model ids and ports SHALL be overridable via configuration. The provider SHALL NOT default the first-line runtime to an nvfp4 quant of the small E4B model.

#### Scenario: Default pairing resolves the verified quants
- **WHEN** the runtimes are configured with defaults
- **THEN** `"firstline"` resolves to the OptiQ-4bit E4B and `"escalation"` to the nvfp4 26B

#### Scenario: Model ids overridable
- **WHEN** configuration supplies explicit model ids and ports
- **THEN** the provider uses them instead of the defaults

### Requirement: Attach-first start lifecycle
`EnsureRunningAsync()` SHALL be attach-first and idempotent: if a healthy server is already serving the runtime's port it attaches without spawning; otherwise it spawns the server and waits for readiness. Repeated calls against a running server SHALL be no-ops beyond the readiness check.

#### Scenario: Attach to an already-running server
- **WHEN** `EnsureRunningAsync()` runs and a healthy server is already on the runtime's port
- **THEN** the provider attaches to it and does not spawn a second process

#### Scenario: Spawn when not running
- **WHEN** `EnsureRunningAsync()` runs and no server is on the port
- **THEN** the provider spawns the server and returns only after it reports ready

### Requirement: Concurrent-safe start
`EnsureRunningAsync()` SHALL be safe under concurrent invocation. When multiple callers invoke it concurrently for the same runtime while no server is running, exactly **one** server process SHALL be spawned and environment provisioning SHALL occur at most **once**. Callers SHALL serialize on the check-then-spawn critical section and re-check liveness inside the serialized region, so that no caller spawns a second process on the runtime's fixed port and no spawned server process is left orphaned (unreachable by `StopAsync()`/`Dispose()`). This concurrency guarantee SHALL compose with the existing attach-first, idempotent lifecycle: a caller arriving while another caller's spawn is in progress SHALL wait and then attach to the resulting server rather than spawning its own.

#### Scenario: Concurrent cold-start spawns exactly one server
- **WHEN** two or more callers invoke `EnsureRunningAsync()` concurrently for the same runtime and no server is yet running
- **THEN** exactly one `mlx_lm.server` process is spawned, the remaining callers attach to it, and no orphaned process remains after all calls complete

#### Scenario: Environment provisioning runs once under concurrency
- **WHEN** concurrent callers trigger first-time `uv` environment provisioning for the same runtime
- **THEN** the venv is provisioned once, not concurrently against the same venv

#### Scenario: The retained process handle references the live server
- **WHEN** concurrent callers complete a cold start
- **THEN** the retained server-process handle references the single spawned server, so `StopAsync()`/`Dispose()` can terminate it with no process left running

### Requirement: Stop lifecycle
The provider SHALL implement `StopAsync()` to terminate a runtime it owns, killing the retained server process and releasing the port. Calling `StopAsync()` on a runtime the provider did not spawn (attached-only) SHALL leave the external process running.

#### Scenario: Owned runtime is stopped
- **WHEN** `StopAsync()` is called for a runtime the provider spawned
- **THEN** the provider terminates the server process and frees its port

#### Scenario: Attached-only runtime is not killed
- **WHEN** `StopAsync()` is called for a runtime that was attached, not spawned
- **THEN** the external process is left running

### Requirement: Readiness via completion or health, not model listing
The provider SHALL determine readiness by confirming the resident model responds — a minimal completion request (or a `/health` endpoint that reflects model-load state) — and SHALL NOT treat `/v1/models` as a readiness or resident-model signal, because it lists cached models irrespective of what is loaded.

#### Scenario: Readiness confirmed by a live completion
- **WHEN** the provider polls readiness after spawning
- **THEN** it issues a minimal completion (or `/health`) and considers the server ready only when the resident model responds

#### Scenario: Model listing is not used for readiness
- **WHEN** `/v1/models` returns entries for cached-but-not-loaded models
- **THEN** the provider does not infer readiness or the resident model from that response

### Requirement: Tool-calling capability probe
After a runtime becomes ready the provider SHALL run a one-time tool-calling probe and record the verified capability, mirroring the existing local-provider probe pattern.

#### Scenario: Probe records tool-calling capability
- **WHEN** a runtime first becomes ready
- **THEN** the provider runs a tool-calling probe once and caches the verified result

### Requirement: Reasoning-aware request defaults
The provider's client SHALL accommodate gemma-4's reasoning output: it SHALL default `max_tokens` high enough that reasoning clears before a tool call is emitted, and SHALL handle the server's separate `reasoning` field without corrupting `content` or `tool_calls` parsing.

#### Scenario: Generous max_tokens default
- **WHEN** a request is issued without an explicit token cap
- **THEN** the provider applies a default large enough that the reasoning phase does not truncate the tool call

#### Scenario: Reasoning field does not break parsing
- **WHEN** a response carries a separate `reasoning` field alongside `tool_calls`
- **THEN** the provider parses `tool_calls`/`content` correctly and does not treat the reasoning text as content

### Requirement: Mlx composition verbs
The provider SHALL supply composition verbs to register the first-line and escalation runtimes (each as a keyed runtime resolvable for warming and client construction) and to wire them as the daemon's backends, replacing the removed `UseOmlx` verb.

#### Scenario: Runtimes registered via composition verbs
- **WHEN** a composition root calls the mlx verbs for first-line and escalation
- **THEN** both keyed runtimes are registered and resolvable by their keys for `EnsureRunningAsync`/`StopAsync` and client construction
