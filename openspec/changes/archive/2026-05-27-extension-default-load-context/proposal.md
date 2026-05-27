## Why

The extension loaders use per-load collectible `AssemblyLoadContext`s to enable hot-unload, but that capability was never realised: the tool registry holds live references so the contexts never collect, there is no `name → ALC` map to target an unload, and a single `_activeContext` field per loader makes each load request `Unload()` on the *previous* extension's still-live context. ADR-008 (superseding ADR-002's loading mechanism) replaces this with the Default `AssemblyLoadContext` and uses process restart as the unload boundary. This must land **before** `extension-middleware-tier`, because the "unload previous context on next load" behaviour directly conflicts with that change's design decision D6 (middleware needs a stable pipeline reference for the process lifetime).

## What Changes

- Load NuGet/local-assembly extensions into `AssemblyLoadContext.Default`. Remove the `_activeContext` field and all `Unload()` calls from `NuGetExtensionLoader`.
- Add transitive dependency resolution for extension assemblies (directory probing via `Assembly.LoadFrom` semantics and/or an `AssemblyDependencyResolver` reading the extension's `.deps.json`), closing the existing gap where only the single named `.dll` was loaded.
- Remove the inert `ScriptAssemblyLoadContext` field from `CsxScriptLoader` (created and stored but never passed to `ScriptRunner`; Dotnet.Script owns its own loading). No behavioural change to script loading.
- Redefine `extension.unload`/`ExtensionService.Unload` as **deregister-from-registry only**: tools stop being exposed to the LLM; assemblies remain resident until process restart. Update protocol/UX wording to say so.
- **BREAKING (capability semantics):** conflicting dependency versions across extensions are no longer supported — first-writer-wins. No change to the `IDmonExtension` / `AIFunction` contract.

> **Scope boundary:** this change covers only the loading **mechanism** (the `extension-middleware-tier` prerequisite). Config-driven startup loading (user + project `extensions` lists), the `/reload` command, and `CoreProcessManager` restart are split into the separate `config-driven-extensions` change.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `extension-model`: extension assemblies load into the Default `AssemblyLoadContext` with explicit transitive-dependency resolution; the inert script load context is removed; `unload` is redefined as registry deregistration with no assembly reclaim until restart.

## Impact

- **`src/Dmon.Core/Extensions/NuGetExtensionLoader.cs`**: Default-context loading, dependency resolution, removal of collectible-ALC lifecycle.
- **`src/Dmon.Core/Extensions/CsxScriptLoader.cs`**: removal of the unused `ScriptAssemblyLoadContext` field and its `Dispose` unload.
- **`src/Dmon.Core/Extensions/ExtensionService.cs`**: `Unload` semantics and documentation.
- **Docs/protocol**: `extension.unload` description clarifies that code is not reclaimed until restart.
- **ADRs**: backed by ADR-008; supersedes ADR-002's loading mechanism only. No change to the extension contract, RPC message shapes, session storage, or permission model.
- **Sequencing**: prerequisite for `extension-middleware-tier`. Followed by `config-driven-extensions` (config loading + `/reload`).
