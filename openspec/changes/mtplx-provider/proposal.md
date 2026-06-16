## Why

dmon has local-runtime providers for llama.cpp, Ollama, and oMLX, but none that exploit Apple-Silicon multi-token-prediction (MTP) decoding. [MTPLX](https://github.com/youssofal/MTPLX) runs MTP-equipped models (Qwen 3.5/3.6, Gemma) ~1.6–2.2× faster than conventional local runtimes on M-series Macs while preserving the exact output distribution, and it already exposes an OpenAI-compatible server. Adding it as a first-party provider gives Mac users a markedly faster local agent loop with no change to the dmon wire protocol.

## What Changes

- Add a new `Dmon.Providers.Mtplx` provider package (granular impl package per ADR-023), following the existing `Dmon.Providers.LlamaCpp` / `Dmon.Providers.Ollama` local-runtime pattern.
- Implement `IProviderExtension` with an **attach-first** lifecycle: detect an MTPLX server already listening on `127.0.0.1:8000` (the app and CLI share one server); if absent, `EnsureRunningAsync()` offers to launch `mtplx serve --port <port>` under the permission gate (ADR-006), mirroring the other local providers. dmon does not take ownership of a server it merely attached to.
- Implement `IProviderFactory` returning an OpenAI-compatible `IChatClient` (`OpenAI.Chat.ChatClient.AsIChatClient()`) pointed at `http://<host>:<port>/v1`, wrapped in `CapabilitiesDecorator`, with probe-verified tool-calling.
- Gate applicability to macOS on Apple Silicon with the `mtplx` executable resolvable (PATH / `MtplxOptions.ServerPath` / `MTPLX_SERVER_PATH`); otherwise `IsApplicable()` returns `false` and a remediation `Warning` is logged. No native binary is bundled.
- `ListModelsAsync()` enumerates the server's loaded/available models via `GET /v1/models`; model acquisition (`mtplx pull`) stays MTPLX's responsibility — the package implements no download logic.
- Add `MtplxOptions` (`Host`, `Port` default `8000`, `ModelId`, `ServerPath`) with a `FromEnvironment()` factory reading `MTPLX_*` env vars.
- Ship the `UseMtplx(...)` composition verb in `namespace Dmon.Hosting` (`AdapterName = "mtplx"`), registering the extension via `AddProvider` and setting an overridable `mtplx/<modelId>` default via `UseModel`; wire `UseMtplx()` into the default composition root `default-core/Dmon.cs`.

## Capabilities

### New Capabilities
- `mtplx-provider`: The `Dmon.Providers.Mtplx` package — Apple-Silicon applicability gating, attach-first managed lifecycle with permission-gated `mtplx serve` start, OpenAI-compatible chat client, `/v1/models` listing, probe-verified tool-calling, and the `UseMtplx` registration verb.

### Modified Capabilities
<!-- None. Existing provider-extension / provider-factory / provider-registry contracts are satisfied as-is; no requirement-level behaviour of existing capabilities changes. -->

## Impact

- **New code:** `providers/Dmon.Providers.Mtplx/` (csproj, `MtplxProviderExtension`, `MtplxProviderFactory`, `MtplxOptions`, `MtplxRuntimeState`, `UseMtplxExtensions`) + test project under `test/`.
- **Composition root:** `default-core/Dmon.cs` gains a `.UseMtplx()` call.
- **Standing spec:** new `openspec/specs/mtplx-provider/spec.md` (synced on archive).
- **Dependencies:** the new package references `Dmon.Abstractions`, `Microsoft.Extensions.AI`, and the OpenAI client library already used by `Dmon.Providers.LlamaCpp`/`OpenAI`; no vendor SDK enters `dmoncore` (ADR-023). No change to the JSONL/stdio wire protocol, RPC commands/events, or session storage.
- **Platform:** functional only on macOS/Apple Silicon; on other platforms the provider self-excludes via `IsApplicable()`.
