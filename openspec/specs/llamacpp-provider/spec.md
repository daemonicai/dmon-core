## Purpose

Define the `Dmon.Providers.LlamaCpp` provider: a managed local llama.cpp integration for the dmon coding agent. It resolves and owns a `llama-server` subprocess (PATH-located, never bundled), acquires GGUF models from Hugging Face by delegation, exposes the server as a `Microsoft.Extensions.AI` `IChatClient` over the OpenAI-compatible endpoint, probe-verifies tool-calling before advertising it, and ships the `UseLlamaCpp` composition verb that registers the provider and sets an overridable default model.

## Requirements

### Requirement: PATH-based applicability

The provider SHALL resolve the `llama-server` executable from `PATH`, an explicit
`LlamaCppOptions.ServerPath`, or the `LLAMA_SERVER_PATH` environment variable. When the
executable cannot be resolved, `IsApplicable()` SHALL return `false` so the provider is not
registered, and a `Warning` SHALL be logged containing remediation guidance for installing
`llama.cpp`. The package SHALL NOT bundle or download a native `llama-server` binary.

#### Scenario: Binary present on PATH

- **WHEN** `llama-server` is resolvable from `PATH` (or an override) on a supported platform
- **THEN** `IsApplicable()` returns `true` and the provider is eligible for registration

#### Scenario: Binary absent

- **WHEN** `llama-server` cannot be resolved from `PATH`, `ServerPath`, or `LLAMA_SERVER_PATH`
- **THEN** `IsApplicable()` returns `false`, no provider is registered, and a `Warning` is
  logged that names how to install `llama.cpp`

### Requirement: Managed llama-server lifecycle

`EnsureRunningAsync()` SHALL start a `llama-server` child process the extension owns, bound
to `127.0.0.1` on a free ephemeral port it selects, launched with `--jinja` and the
configured model and options. It SHALL poll the server's readiness endpoint until ready or a
configurable timeout elapses, throwing `TimeoutException` on timeout. The extension SHALL be
disposable and SHALL terminate the child process on disposal, leaving no orphaned server.
`IsRunningAsync()` SHALL verify server identity, not merely port reachability.

#### Scenario: Cold start to ready

- **WHEN** `EnsureRunningAsync()` is called and no managed server is running
- **THEN** a `llama-server` process is started with `--jinja`, a free `--port`, and the
  configured model, and the call returns once the readiness endpoint reports healthy

#### Scenario: Readiness timeout

- **WHEN** the server does not become ready within the configured timeout
- **THEN** `EnsureRunningAsync()` throws `TimeoutException` and no half-started server is
  left running

#### Scenario: Teardown leaves no orphan

- **WHEN** the extension is disposed (or host shutdown occurs) while the managed server is
  running
- **THEN** the child `llama-server` process is terminated

### Requirement: Hugging Face model acquisition via delegation

The provider SHALL pass the configured model identifier — a Hugging Face repo id,
optionally suffixed `:QUANT` — to `llama-server -hf`, delegating download and caching to
`llama.cpp`. When no quant suffix is supplied, the provider SHALL apply the configured
default quant. The package SHALL NOT implement its own GGUF download, resume, or checksum
logic.

#### Scenario: Repo id with explicit quant

- **WHEN** the model is `"owner/repo-GGUF:Q4_K_M"`
- **THEN** `llama-server` is launched with `-hf owner/repo-GGUF:Q4_K_M` and llama.cpp
  performs the download into its own cache

#### Scenario: Repo id without quant

- **WHEN** the model is `"owner/repo-GGUF"` and a default quant is configured
- **THEN** `llama-server` is launched with the default quant applied

### Requirement: OpenAI-compatible chat client

`CreateFactory().CreateAsync(...)` SHALL return an `IChatClient` built from
`OpenAI.Chat.ChatClient.AsIChatClient()` whose endpoint is the managed server's
`http://127.0.0.1:<port>/v1`, wrapped in `CapabilitiesDecorator`. Tool definitions SHALL be
carried as the standard OpenAI `tools` field on chat completion requests; no dmon
wire-protocol, command, or event shape SHALL change.

#### Scenario: Client targets the managed server

- **WHEN** the factory creates the chat client after the server is running
- **THEN** the returned `IChatClient` issues OpenAI-compatible requests to the managed
  server's port and is wrapped in `CapabilitiesDecorator`

### Requirement: Probe-verified tool-calling capability

Before declaring tool-calling support, the provider SHALL issue a probe chat request
carrying a trivial tool definition and observe whether a tool call round-trips.
`SupportsToolCalling` SHALL be declared `true` only when the probe yields a tool call;
otherwise it SHALL be `false` and a `Warning` SHALL be logged. The declared capability SHALL
reflect the probe outcome, not a heuristic over the model identifier.

#### Scenario: Probe succeeds

- **WHEN** the startup probe request returns a tool call for the dummy tool
- **THEN** the capabilities exposed for the model report `SupportsToolCalling = true`

#### Scenario: Probe fails

- **WHEN** the startup probe request returns no tool call (e.g. the model template lacks
  tool support despite `--jinja`)
- **THEN** the capabilities report `SupportsToolCalling = false` and a `Warning` is logged

### Requirement: Builder registration sets an overridable default

The package SHALL expose `UseLlamaCpp<T>(this T registration, string model) where T : IProviderRegistration`
(and a `UseLlamaCpp<T>(this T, LlamaCppOptions)` overload for full control) in
`namespace Dmon.Hosting`. Calling the verb SHALL register a `LlamaCppProviderExtension` via
`AddProvider` and set `llamacpp/<modelId>` as the default active model via `UseModel`. The
default SHALL remain overridable by `config.yaml` and the runtime provider-switch RPC; the
provider SHALL NOT be pinned.

#### Scenario: One-line composition sets the default model

- **WHEN** a composition root calls `builder.UseLlamaCpp("owner/repo-GGUF")` on any
  `IProviderRegistration` (including `DmonHost.CreateBuilder(args)`)
- **THEN** the llama.cpp provider extension is registered with the host and `llamacpp/owner/repo-GGUF`
  is the default active model at startup

#### Scenario: Runtime override wins over the code default

- **WHEN** `UseLlamaCpp` set a default model and a later configuration source (e.g.
  `config.yaml` loaded after composition, or a `/provider.switch` RPC) selects a different
  provider/model
- **THEN** the runtime selection takes effect at the next turn boundary, overriding the
  code-set default
