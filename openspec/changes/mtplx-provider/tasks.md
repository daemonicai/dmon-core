## 1. Package scaffold and options

- [x] 1.1 Create `providers/Dmon.Providers.Mtplx/Dmon.Providers.Mtplx.csproj` referencing `Dmon.Abstractions`, `Microsoft.Extensions.AI`, and the OpenAI client package used by `Dmon.Providers.LlamaCpp`; add it to the providers `.slnx` and any `Everything.slnx`.
- [x] 1.2 Add `MtplxOptions` (sealed record: `Host` default `127.0.0.1`, `Port` default `8000`, `ModelId` nullable, `ServerPath` nullable, `ReadyTimeout`) with a `FromEnvironment()` factory reading `MTPLX_HOST`, `MTPLX_PORT`, `MTPLX_MODEL_ID`, `MTPLX_SERVER_PATH`.
- [x] 1.3 Add `MtplxRuntimeState` tracking `BaseUrl`, whether dmon owns the started process, and `ToolCallingVerified`.

## 2. Provider extension — applicability and lifecycle

- [x] 2.1 Implement `MtplxProviderExtension : IProviderExtension` skeleton (`ProviderName = "mtplx"`) with constructor-injected seams (HTTP probe + process-start callbacks) mirroring `LlamaCppProviderExtension` so the lifecycle is unit-testable without a real server.
- [x] 2.2 Implement `IsApplicable()`: `true` only on macOS + arm64 with `mtplx` resolvable (PATH / `ServerPath` / `MTPLX_SERVER_PATH`); log distinct remediation `Warning`s for wrong-OS/arch vs not-installed; never bundle a binary.
- [x] 2.3 Implement `IsRunningAsync()` verifying server identity via `/health` plus `/v1/models` at the configured host/port (not bare TCP reachability).
- [x] 2.4 Implement attach-first `EnsureRunningAsync()`: attach if a server answers `/health`; otherwise launch `mtplx serve --port <port>` under the ADR-006 permission gate, poll `/health` to ready, throw `TimeoutException` on timeout; record process ownership in `MtplxRuntimeState`.
- [x] 2.5 Implement `IDisposable`/`IAsyncDisposable` to terminate only a dmon-started process; leave an attached server running.
- [x] 2.6 Implement `ListModelsAsync()` from `GET /v1/models`; when `ModelId` is unset, target the server's reported active model.

## 3. Provider factory — chat client and capabilities

- [x] 3.1 Implement `MtplxProviderFactory : IProviderFactory` (`AdapterName = "mtplx"`, `DisplayName = "MTPLX"`) producing an `IChatClient` from `OpenAI.Chat.ChatClient.AsIChatClient()` pointed at `http://<host>:<port>/v1`, wrapped in `CapabilitiesDecorator`.
- [x] 3.2 Implement the probe-verified tool-calling capability (trivial tool round-trip → `SupportsToolCalling`; `Warning` + `false` on failure); reflect the probe outcome in `GetCapabilities`, never a model-name heuristic.
- [x] 3.3 Implement `GetNextStepAsync`/wizard surface consistent with the other local providers.

## 4. Composition verb and wiring

- [ ] 4.1 Add `UseMtplxExtensions` in `namespace Dmon.Hosting`: `UseMtplx<T>(this T, string model)`, `UseMtplx<T>(this T, MtplxOptions)`, and parameterless `UseMtplx<T>(this T)` (options from `FromEnvironment()`), each `AddProvider(new MtplxProviderExtension(...))` then `UseModel("mtplx", modelId)`; constrained `where T : IProviderRegistration`.
- [ ] 4.2 Wire `.UseMtplx()` into `default-core/Dmon.cs`.
- [ ] 4.3 Add a composition-root sample if the repo carries per-provider samples (match the websearch/llamacpp sample convention).

## 5. Tests

- [ ] 5.1 Create `test/Dmon.Providers.Mtplx.Tests` (xunit) wired into the test solution.
- [ ] 5.2 Test `IsApplicable()`: asserts `false` on the CI platform (non-Apple-Silicon) and on missing binary; asserts the platform/arch/installed branches via injected probes.
- [ ] 5.3 Test attach-first lifecycle: attach path (no process started), permission-gated cold-start path, and readiness `TimeoutException`, using injected HTTP/process seams.
- [ ] 5.4 Test `ListModelsAsync()` parsing of `/v1/models` and default-model-follows-server behaviour.
- [ ] 5.5 Test the factory: endpoint targets `http://<host>:<port>/v1`, `CapabilitiesDecorator` wrap, and probe-driven `SupportsToolCalling` true/false branches.
- [ ] 5.6 Test `UseMtplx` verbs register the extension and set `mtplx/<modelId>` as the overridable default.

## 6. Validation and spec sync

- [ ] 6.1 `make build` clean (no warnings; `TreatWarningsAsErrors`) and `make test` green.
- [ ] 6.2 `openspec validate mtplx-provider --strict` passes.
- [ ] 6.3 On archive, sync the new standing spec to `openspec/specs/mtplx-provider/spec.md` and update any provider index/`monorepo-layout` notes that enumerate providers.
