## Purpose

Define the `Dmon.Providers.Mtplx` provider: an Apple-Silicon-only managed integration of the MTPLX local inference server for the dmon coding agent. It resolves the `mtplx` executable (PATH-located, never bundled), follows an attach-first lifecycle over a standing MTPLX server, delegates model acquisition to MTPLX, exposes the server as a `Microsoft.Extensions.AI` `IChatClient` over its OpenAI-compatible endpoint, probe-verifies tool-calling before advertising it, and ships the `UseMtplx` composition verb that registers the provider and sets an overridable default model.

## Requirements

### Requirement: Apple-Silicon applicability

The provider SHALL be applicable only on macOS running on an Apple-Silicon (arm64) architecture
with the `mtplx` executable resolvable from `PATH`, an explicit `MtplxOptions.ServerPath`, or the
`MTPLX_SERVER_PATH` environment variable. When the platform is not macOS/arm64, or the executable
cannot be resolved, `IsApplicable()` SHALL return `false` so the provider is not registered, and a
`Warning` SHALL be logged containing remediation guidance for installing MTPLX (e.g. `brew install
youssofal/mtplx/mtplx`). The package SHALL NOT bundle or download a native MTPLX binary.

#### Scenario: Apple Silicon with mtplx present

- **WHEN** the host is macOS on arm64 and `mtplx` is resolvable from `PATH` (or an override)
- **THEN** `IsApplicable()` returns `true` and the provider is eligible for registration

#### Scenario: Unsupported platform

- **WHEN** the host is not macOS, or not arm64
- **THEN** `IsApplicable()` returns `false`, no provider is registered, and a `Warning` is logged
  that MTPLX requires Apple Silicon

#### Scenario: Binary absent

- **WHEN** the host is macOS/arm64 but `mtplx` cannot be resolved from `PATH`, `ServerPath`, or
  `MTPLX_SERVER_PATH`
- **THEN** `IsApplicable()` returns `false`, no provider is registered, and a `Warning` is logged
  that names how to install MTPLX

### Requirement: Attach-first managed lifecycle

MTPLX runs as a standing server shared by its native app and CLI. `IsRunningAsync()` SHALL verify a
genuine MTPLX server is listening at `http://<host>:<port>` (default `127.0.0.1:8000`) by checking
its `/health` endpoint and confirming `/v1/models` responds — server identity, not mere port
reachability. When a server is already running, the provider SHALL attach to it and SHALL NOT spawn
a second process. When no server is running, `EnsureRunningAsync()` SHALL launch `mtplx serve --port
<port>` under the dmon permission gate (ADR-006), poll `/health` until ready or a configurable
timeout elapses, and throw `TimeoutException` on timeout. The provider SHALL terminate on disposal
only a server process it itself started; a server it merely attached to SHALL be left running.

#### Scenario: Attach to an already-running server

- **WHEN** `EnsureRunningAsync()` is called and an MTPLX server already answers `/health` at the
  configured host/port
- **THEN** the provider attaches to the existing server, starts no new process, and returns ready

#### Scenario: Permission-gated cold start

- **WHEN** `EnsureRunningAsync()` is called, no server is running, and the user approves the start
  prompt
- **THEN** an `mtplx serve --port <port>` process is started and the call returns once `/health`
  reports healthy

#### Scenario: Readiness timeout

- **WHEN** a started server does not become ready within the configured timeout
- **THEN** `EnsureRunningAsync()` throws `TimeoutException` and no half-started server is left
  running

#### Scenario: Disposal leaves an attached server running

- **WHEN** the extension is disposed after having attached to a server it did not start
- **THEN** that server is left running and only a dmon-started process (if any) is terminated

### Requirement: Model listing via the MTPLX server

`ListModelsAsync()` SHALL enumerate the models the running server reports from `GET /v1/models`.
Model acquisition (downloading and caching, e.g. `mtplx pull`) SHALL remain MTPLX's responsibility;
the package SHALL NOT implement its own model download, resume, or checksum logic. When no
`ModelId` is configured, the provider SHALL use the model the server reports as active.

#### Scenario: List returns server models

- **WHEN** `ListModelsAsync()` is called against a running server
- **THEN** it returns the model identifiers from the server's `/v1/models` response

#### Scenario: Default model follows the server

- **WHEN** no `MtplxOptions.ModelId` is configured
- **THEN** the provider targets the model the running server reports as active rather than
  downloading or selecting a different one

### Requirement: OpenAI-compatible chat client

`CreateFactory().CreateAsync(...)` SHALL return an `IChatClient` built from
`OpenAI.Chat.ChatClient.AsIChatClient()` whose endpoint is the server's
`http://<host>:<port>/v1`, wrapped in `CapabilitiesDecorator`. The `AdapterName` SHALL be `"mtplx"`.
Tool definitions SHALL be carried as the standard OpenAI `tools` field on chat completion requests;
no dmon wire-protocol, command, or event shape SHALL change.

#### Scenario: Client targets the MTPLX server

- **WHEN** the factory creates the chat client after the server is running
- **THEN** the returned `IChatClient` issues OpenAI-compatible requests to the server's
  `http://<host>:<port>/v1` endpoint and is wrapped in `CapabilitiesDecorator`

### Requirement: Probe-verified tool-calling capability

Before declaring tool-calling support, the provider SHALL issue a probe chat request carrying a
trivial tool definition and observe whether a tool call round-trips. `SupportsToolCalling` SHALL be
declared `true` only when the probe yields a tool call; otherwise it SHALL be `false` and a
`Warning` SHALL be logged. The declared capability SHALL reflect the probe outcome, not a heuristic
over the model identifier.

#### Scenario: Probe succeeds

- **WHEN** the startup probe request returns a tool call for the dummy tool
- **THEN** the capabilities exposed for the model report `SupportsToolCalling = true`

#### Scenario: Probe fails

- **WHEN** the startup probe request returns no tool call
- **THEN** the capabilities report `SupportsToolCalling = false` and a `Warning` is logged

### Requirement: Builder registration sets an overridable default

The package SHALL expose `UseMtplx<T>(this T registration, string model) where T : IProviderRegistration`
(and a `UseMtplx<T>(this T, MtplxOptions)` overload for full control) in `namespace Dmon.Hosting`.
Calling the verb SHALL register an `MtplxProviderExtension` via `AddProvider` and set
`mtplx/<modelId>` as the default active model via `UseModel`. A parameterless `UseMtplx<T>(this T)`
overload SHALL register the provider with options sourced from `MtplxOptions.FromEnvironment()`. The
default SHALL remain overridable by `config.yaml` and the runtime provider-switch RPC; the provider
SHALL NOT be pinned.

#### Scenario: One-line composition sets the default model

- **WHEN** a composition root calls `builder.UseMtplx("Youssofal/Qwen3.5-9B")` on any
  `IProviderRegistration` (including `DmonHost.CreateBuilder(args)`)
- **THEN** the MTPLX provider extension is registered with the host and `mtplx/Youssofal/Qwen3.5-9B`
  is the default active model at startup

#### Scenario: Runtime override wins over the code default

- **WHEN** `UseMtplx` set a default model and a later configuration source (e.g. `config.yaml`
  loaded after composition, or a `/provider.switch` RPC) selects a different provider/model
- **THEN** the runtime selection takes effect at the next turn boundary, overriding the code-set
  default
