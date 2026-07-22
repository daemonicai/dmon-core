## 1. Factory owns the self-heal

- [x] 1.1 Add an `MlxProviderExtension` parameter to `MlxProviderFactory`'s constructor(s) and store it; update `MlxProviderExtension.CreateFactory()` to pass `this`.
- [x] 1.2 In `MlxProviderFactory.CreateAsync`, `await extension.EnsureRunningAsync(cancellationToken)` before reading `GetCapabilities()`, so the tool-calling probe has set `ToolCallingVerified` before the capabilities snapshot is taken.
- [x] 1.3 Wrap the built stack (`ChatClient → MlxMaxTokensDefaulter → CapabilitiesDecorator`) outermost in `new EnsureRunningChatClient(inner, extension)` and return it.

## 2. Single-source the keyed path

- [ ] 2.1 Simplify `MlxClientExtensions.MlxClient(key)` to resolve the keyed `MlxProviderExtension`, build the `ProviderConfig`, and return `((MlxProviderFactory)ext.CreateFactory()).CreateAsync(...)` — removing its duplicate up-front `EnsureRunningAsync` call and its own `EnsureRunningChatClient` wrap.
- [ ] 2.2 Confirm the keyed registration/warm/stop seams (`AddMlxFirstline`/`AddMlxEscalation`, `EscalationWarmingService`, `MlxRuntimeKeys`) are untouched.

## 3. Tests

- [x] 3.1 Update all `MlxProviderFactory` construction sites in `test/Dmon.Providers.Mlx.Tests` for the new extension parameter.
- [x] 3.2 Add coverage that `CreateAsync` invokes `EnsureRunningAsync` (runtime start path) and returns an `EnsureRunningChatClient`-wrapped client on the active-provider path.
- [x] 3.3 Add coverage that the returned active-provider client advertises `SupportsToolCalling == true` when the probe verified tool support (capabilities snapshot taken after the probe).
- [ ] 3.4 Add coverage that `MlxClient(key)` returns a self-healing client sourced from the factory and no longer double-invokes/double-wraps the self-heal.

## 4. Gates

- [ ] 4.1 `make build` clean (TreatWarningsAsErrors) and `env -u MEKO_API_KEY make test` green (new + existing).
- [ ] 4.2 `openspec validate mlx-active-provider-self-heal --strict` passes.
- [ ] 4.3 Rebuild `sandbox-code` (`bash sandbox-code/build.sh`) and confirm a `.UseMlx(...)` turn reaches the model instead of connection-refused (human-in-the-loop: the architect provides a copy-pasteable verification recipe; this task is ticked only after the user confirms).
