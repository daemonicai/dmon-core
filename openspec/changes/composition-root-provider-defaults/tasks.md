## 1. Default synthesis + merge

- [x] 1.1 Add a `ProviderConfigComposer` (helper/class in `core/Dmon.Core/Providers/`) with `Compose(IReadOnlyList<ProviderConfig> fromConfig, IEnumerable<IProviderFactory> factories) : IReadOnlyList<ProviderConfig>` that appends a synthesized default for each factory whose `AdapterName` is not already the `Adapter` of any config entry (case-insensitive), config entries first then synthesized in factory order.
- [x] 1.2 Implement the synthesized-default shape per design Decision 2: `Name`=`Adapter`=`AdapterName`, `DefaultModelId`=`factory.DefaultModelId`, `Auth`=`{ Type="envVar", EnvVar=DefaultEnvVar }` when `DefaultEnvVar` non-empty else `{ Type="none" }`, `BaseUrl`=null.
- [x] 1.3 Wire the composer into the shared `IEnumerable<ProviderConfig>` registration in `DaemonServiceExtensions.AddDmonCore` (resolve `IEnumerable<IProviderFactory>` and the loader result, return the composed list) so `ProviderRegistry` and `CredentialResolver` share it.

## 2. Registry empty-case relaxation

- [ ] 2.1 Confirm `ProviderConfigLoader.Load()` returns an empty list (no throw) when the `providers:` section is absent; add a unit test pinning this if not already covered.
- [ ] 2.2 Verify `ProviderRegistry.EnsureProviderConfigured` throws only on a genuinely empty composed list (no config AND no factories) and no longer fires when factories are wired; keep the existing warn-and-skip for config entries whose adapter has no factory.

## 3. Composition root + config

- [ ] 3.1 Update root `Dmon.cs` to wire providers explicitly via verbs (`UseAnthropic().UseOpenAI().UseGemini().UseOllama()` plus the matching `#:package Dmon.Providers.*` directives) alongside `AddBuiltinTools()`.
- [ ] 3.2 Remove the `providers:` map from `.dmon/config.yaml`, keeping the `Dmon:` session/provider/compaction settings.

## 4. Tests

- [ ] 4.1 Unit-test `ProviderConfigComposer`: synth-when-absent, suppress-when-config-represents-adapter, ordering (config first then factory order), keyless default when `DefaultEnvVar` empty.
- [ ] 4.2 Registry/resolver tests: verb-only provider is listed and its env-var key resolves via `CredentialResolver` (shared list); no-config+factory does not throw; no-config+no-factory throws; unknown-adapter config entry warns and is skipped.
- [ ] 4.3 `make build` clean (TreatWarningsAsErrors) and `make test` green.

## 5. Spec sync

- [ ] 5.1 `openspec validate composition-root-provider-defaults --strict` passes.
- [ ] 5.2 Manual verification recipe: run `build/dmon` in the repo root, confirm no "in config but no factory" warnings and that `[Ready]` reports with the wired providers selectable.
