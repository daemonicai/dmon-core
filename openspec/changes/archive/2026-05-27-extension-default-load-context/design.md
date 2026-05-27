## Context

`NuGetExtensionLoader` and `CsxScriptLoader` are registered as singletons (`DaemonServiceExtensions`). Each holds a single `_activeContext` field intended to provide collectible-`AssemblyLoadContext` isolation. In practice:

- The tool registry holds live references to extension instances and their `AIFunction`s, so a collectible context never collects while the daemon runs.
- `NuGetExtensionLoader.LoadLocalAssemblyAsync` does `_activeContext?.Unload(); _activeContext = new(...)` on every load — requesting unload of the *previous* extension's context while its tools are still registered.
- `ExtensionService.Unload(name)` only deregisters from the registry; there is no `name → ALC` map, so it cannot target a context anyway.
- `CsxScriptLoader` constructs a `ScriptAssemblyLoadContext` and stores it in `_activeContext`, but never passes it to `ScriptRunner` — Dotnet.Script manages its own loading. The field is inert.
- `NuGetExtensionLoader` loads only the single named `.dll` via `LoadFromAssemblyPath`; transitive dependencies are never resolved.

The topology is now settled: `Dmon.Core` is a JSONL/stdio child process spawned and supervised by `Dmon.Terminal` (`CoreProcessManager`). Session state is durable on disk (ADR-004). ADR-008 records the decision to load extensions into the Default context and use process restart as the unload boundary. This change implements ADR-008 and is a prerequisite for `extension-middleware-tier` (whose design decision D6 requires a stable pipeline reference that the previous unload-on-next-load behaviour would break).

## Goals / Non-Goals

**Goals:**
- Load NuGet/local-assembly extensions into `AssemblyLoadContext.Default`; remove collectible-ALC lifecycle from the loaders.
- Resolve an extension's transitive dependencies explicitly so extensions with their own dependencies load successfully.
- Redefine `unload` as registry deregistration with honest semantics (no assembly reclaim until restart).

**Non-Goals:**
- The restart path, `/reload` command, and config-driven startup loading — split into the `config-driven-extensions` change. This change only makes the loading *mechanism* correct.
- Supporting two versions of the same dependency simultaneously (first-writer-wins; recoverable in a future ADR if it ever bites).
- Any change to the `IDmonExtension` / `IProviderExtension` / `AIFunction` contract, the `.csx` return convention, the `promote` path, RPC message shapes, session storage, or the permission model.
- NuGet package *resolution* (still deferred to V1.1 per ADR-002; this change only changes how an already-resolved assembly is loaded).
- Sandboxing extension code (ALC was never a security boundary).

## Decisions

### D1 — Load into `AssemblyLoadContext.Default`
`NuGetExtensionLoader` loads extension assemblies into the Default context. The `_activeContext` field and all `Unload()` calls are removed. With a single context, the contract assemblies (`Dmon.Extensions`, `Dmon.Abstractions`, `Microsoft.Extensions.AI`) resolve to one type identity for host and every extension, so reflection discovery (`IsAssignableFrom`) is correct by construction — the previous implicit "fallback to Default" gamble is no longer load-bearing.

**Alternative considered:** one non-collectible ALC shared by all extensions. Rejected — it adds a context boundary that buys nothing over Default (no isolation, no collectibility) while keeping the type-identity fallback fragile.

### D2 — Dependency resolution via `AssemblyDependencyResolver` + directory probing
For each loaded extension assembly path, construct an `AssemblyDependencyResolver(extensionAssemblyPath)` and register a handler on `AssemblyLoadContext.Default.Resolving` that consults the resolver (which reads the extension's `.deps.json`), falling back to probing the extension assembly's own directory. Resolvers are keyed per extension path and additive (multiple extensions can each contribute).

**Rationale:** `.deps.json` captures the full dependency graph (including framework/RID-specific assets) that flat directory probing misses. Directory probing is the fallback for extensions shipped without a `.deps.json`.

**Alternative considered:** `Assembly.LoadFrom` (legacy LoadFrom context, probes the source directory automatically). Rejected as the primary mechanism — it does not honour `.deps.json` and mixes load contexts in confusing ways; acceptable only as conceptual fallback behaviour.

### D3 — `unload` = deregister only
`ExtensionService.Unload(name)` continues to call `IToolRegistry.Unregister(name)` and emit `ExtensionUnloadedEvent`, but the contract is clarified: tools stop being offered to the LLM; the assembly stays resident until the process exits. Protocol/UX text for `extension.unload` states this. (No behavioural code change beyond removing the now-absent ALC teardown; the change is semantic + documentation.)

### D4 — Remove the inert script load context
Delete the `ScriptAssemblyLoadContext` field and its `Dispose` unload from `CsxScriptLoader`. Script loading is unchanged: Dotnet.Script compiles and loads the script; the loader extracts returned `AIFunction`s.

## Risks / Trade-offs

- **[Conflicting dependency versions]** Two extensions needing different versions of the same package → first-writer-wins; the second gets that version or fails on a strong-name mismatch. → Accepted for V1 and documented. Future ADR can reintroduce per-extension contexts for the conflicting subset, with contract assemblies pinned to Default.
- **[No mid-process memory reclaim]** Loading/unloading many extensions in a long session accumulates resident assemblies. → Matches today's reality (collectible ALCs never collected); restart (delivered in `config-driven-extensions`) is the reclaim path.
- **[`Resolving` handler accumulation]** Registering a handler per extension on a long-lived `Default.Resolving` event could accumulate across many loads. → Keep one resolver per distinct extension path; de-duplicate; this is bounded by the number of loaded extensions.

## Migration Plan

1. No data migration — session format unchanged.
2. Land this change before `extension-middleware-tier`; that change then folds its pipeline over a stable, never-unloaded provider client.
3. Rollback: revert the loader changes; ADR-008 would move to a superseded/abandoned state. No persisted state depends on this change.

## Open Questions

- None blocking. The reload/restart UX and where unload semantics surface to the user are decided in the `config-driven-extensions` change (config is the source of truth for active extensions; `/reload` restarts and re-reads it).
