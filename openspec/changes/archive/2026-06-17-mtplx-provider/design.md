## Context

dmon's local-runtime providers (`Dmon.Providers.LlamaCpp`, `Ollama`, `Omlx`) all implement
`IProviderExtension` (lifecycle + discovery) and `IProviderFactory` (an OpenAI-compatible
`IChatClient`), composed by a `Use<Provider>` verb in `namespace Dmon.Hosting` (ADR-022/023).
MTPLX is an Apple-Silicon-only native runtime that, via `mtplx serve`, exposes an
OpenAI-compatible API at `127.0.0.1:8000` (`/v1/chat/completions`, `/v1/completions`,
`/v1/models`, `/health`, `/metrics`) plus an Anthropic-compatible `/v1/messages`. Critically,
the MTPLX app and CLI **share one server** — `mtplx serve` attaches to a model the app already
loaded rather than starting a second copy. This standing-server model is the main thing that
distinguishes MTPLX integration from llama.cpp, where dmon owns an ephemeral per-session
`llama-server` on a private port.

## Goals / Non-Goals

**Goals:**
- A first-party `Dmon.Providers.Mtplx` package that lets `default-core/Dmon.cs` say `.UseMtplx()`
  and run a dmon turn loop against a local MTPLX server.
- Attach to an already-running MTPLX server by default; offer to start one (permission-gated) only
  when none is found.
- Reuse the established OpenAI-`IChatClient` + `CapabilitiesDecorator` + probe-verified
  tool-calling machinery, so no wire-protocol or capability-detection change is needed.
- Self-exclude cleanly on non-Apple-Silicon hosts.

**Non-Goals:**
- Implementing model download/caching — that is `mtplx pull`'s job; we only list and target.
- Using MTPLX's Anthropic-compatible `/v1/messages` surface — out of scope (see Decisions).
- Exposing MTPLX's `/metrics`, fan control, tuning, or benchmark features through dmon.
- Bundling or auto-installing the `mtplx` binary.
- Multi-server / remote MTPLX. We attach to a local server at the configured host/port only.

## Decisions

### Decision: Attach-first lifecycle, not dmon-owned subprocess

Because the MTPLX app and CLI share one server, dmon spawning its own `mtplx serve` on a private
ephemeral port (the llama.cpp approach) would either collide with or duplicate the user's running
model, wasting memory on a 9B–35B model. Instead `IsRunningAsync()` probes the configured
host/port (`127.0.0.1:8000` default), and `EnsureRunningAsync()` attaches if a server answers
`/health`. Only when nothing is listening does it offer to run `mtplx serve --port <port>` behind
the ADR-006 permission gate. Disposal terminates **only** a process dmon itself started; an
attached server is left untouched.

- *Alternative considered — always own the subprocess (llama.cpp parity):* rejected; conflicts
  with MTPLX's shared-server design and double-loads the model.
- *Alternative considered — attach-only, never start:* rejected; the user's confirmed preference
  is "attach, else offer to start," which is also friendlier than failing when the server is down.

### Decision: Fixed/configured port, not a dmon-selected ephemeral port

MTPLX defaults to `127.0.0.1:8000` and that is where an existing app/CLI server lives. The
provider therefore targets a *configured* host/port (`MtplxOptions.Host`/`Port`, defaults
`127.0.0.1`/`8000`) rather than picking a free port as llama.cpp does. A dmon-started server is
launched on that same configured port so a subsequent attach is consistent.

### Decision: OpenAI-compatible surface via the existing OpenAI client

MTPLX speaks both OpenAI and Anthropic dialects. dmon already builds `IChatClient`s from
`OpenAI.Chat.ChatClient.AsIChatClient()` with a custom endpoint for llama.cpp/Ollama, and
`ProviderConfig`/`CapabilitiesDecorator`/tool-calling probe are all proven on that path. Reusing
it keeps MTPLX a thin variation on a known-good provider. The Anthropic `/v1/messages` surface
would force a second client construction path and a third-party request shape into the provider
for no behavioural gain, so it is excluded. `AdapterName = "mtplx"`.

### Decision: Model acquisition delegated; listing via `/v1/models`

Like llama.cpp delegating downloads to `-hf`, MTPLX owns its catalog (`mtplx pull`, the app's
recommender). `ListModelsAsync()` simply surfaces `GET /v1/models`. When `MtplxOptions.ModelId`
is unset, the provider targets whatever model the server reports active, matching MTPLX's
"one server, one loaded model" reality.

### Decision: Options + `FromEnvironment()` mirroring `LlamaCppOptions`

`MtplxOptions` is a sealed record: `Host` (`127.0.0.1`), `Port` (`8000`), `ModelId` (nullable),
`ServerPath` (nullable), `ReadyTimeout`. `FromEnvironment()` reads `MTPLX_HOST`, `MTPLX_PORT`,
`MTPLX_MODEL_ID`, `MTPLX_SERVER_PATH`. `MtplxRuntimeState` tracks `BaseUrl`, whether dmon owns the
process, and `ToolCallingVerified`. This matches the shape reviewers already know from llama.cpp.

### Decision: Apple-Silicon gate in `IsApplicable()`

`IsApplicable()` returns `true` only when `OperatingSystem.IsMacOS()` and
`RuntimeInformation.OSArchitecture == Architecture.Arm64` and the `mtplx` executable resolves.
Each negative path logs a distinct remediation `Warning` (wrong-OS / wrong-arch vs not-installed).
This keeps the provider inert and registration-free on Linux/Windows CI and Intel Macs.

## Risks / Trade-offs

- **A non-MTPLX service occupies `127.0.0.1:8000`** → `IsRunningAsync()` verifies identity via
  `/health` + `/v1/models`, not just a TCP connect, so a foreign listener is not mistaken for MTPLX
  and the provider falls through to the offer-to-start path (or errors clearly).
- **dmon starts a server the user didn't expect** → start is permission-gated (ADR-006) and only
  reached when nothing answers `/health`; a dmon-started process is the only one torn down on
  disposal.
- **Cold model load is slow** (large MTP models) → covered by a configurable `ReadyTimeout` with a
  clear `TimeoutException`; the same readiness-poll pattern llama.cpp uses.
- **CI cannot exercise the real runtime** (no Apple-Silicon + no MTPLX binary on runners) → the
  HTTP/lifecycle seams are unit-tested with injected fakes (the llama.cpp test project already does
  this via constructor-injected callbacks); `IsApplicable()` is asserted `false` on the CI platform.
- **Tool-calling varies by model** → resolved by the existing probe; capability reflects the probe,
  never a model-name heuristic.

## Open Questions

- None blocking. (If a future need arises to prefer MTPLX's Anthropic `/v1/messages` surface, that
  would be a separate change building on the Anthropic provider's client path.)
