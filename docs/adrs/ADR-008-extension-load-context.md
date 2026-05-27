# ADR-008: Extension Loading Uses the Default AssemblyLoadContext

**Date:** 2026-05-27
**Status:** Accepted
**Supersedes:** ADR-002 — *assembly-loading mechanism only*. The `IDmonExtension` / `AIFunction` contract, the `.csx` script tier, the `promote` path, and the permission-gated load flow from ADR-002 all remain in force. This ADR changes **only** how NuGet/local-assembly extensions are loaded into the process.

## Context

ADR-002 specified that NuGet/local-assembly extensions are loaded via a **collectible `AssemblyLoadContext` (ALC)**. The stated motivation was hot-unload: the ability to reclaim an extension's assemblies and types mid-session without restarting.

In practice the implementation (`NuGetExtensionLoader`, `CsxScriptLoader`) does not realise that motivation:

- **Collectibility is never exercised.** The tool registry holds live references to each extension instance and its `AIFunction`s, so a collectible ALC never actually collects while the daemon runs. `Unload()` on a context whose types are still referenced is a no-op for memory reclaim.
- **There is no `name → ALC` map.** `ExtensionService.Unload(name)` only deregisters from the tool registry; it cannot target the originating context. So even an intentional unload reclaims nothing.
- **A single `_activeContext` field per (singleton) loader serialises loads.** Each load calls `_activeContext?.Unload()` then replaces it — so loading extension B requests unload of extension A's context *while A's tools are still live*. The "isolated from each other" comment describes an intent the code does not implement.
- **Type-identity hazard.** For reflection discovery to work, the extension's `IDmonExtension` must be the *same `Type`* as the host's. This works today only because the ALC's default `Load` returns `null` for the contract assemblies and resolution falls back to Default. An extension shipping its own differing-version copy of `Microsoft.Extensions.AI` would load it into its own context, get distinct type identity, and **silently fail discovery**.

Meanwhile the deployment topology has clarified: `Dmon.Core` runs as a **JSONL/stdio child process owned by `Dmon.Terminal`** (`CoreProcessManager` spawns and supervises it). Session state is durable on disk (ADR-004). That makes **process restart a clean, cheap unload boundary** — a fresh core re-attaches to the same session directory and rebuilds its registry from scratch.

A collectible ALC is the *only* .NET mechanism that lets two versions of the same dependency coexist in one process. That is the single capability we would forfeit by using the Default context — and it is low-risk for V1, where the dependency that matters most (`Microsoft.Extensions.AI`, carrier of the `AIFunction` contract) must be shared *anyway*.

## Decision

NuGet and local-assembly extensions are loaded into the **Default `AssemblyLoadContext`** (`AssemblyLoadContext.Default`), not per-load collectible contexts.

1. **No collectible ALCs for extensions.** `NuGetExtensionLoader` loads extension assemblies into the Default context. The `_activeContext` field and its `Unload()` calls are removed. The inert `ScriptAssemblyLoadContext` in `CsxScriptLoader` is removed (Dotnet.Script owns its own loading; the field was never wired to the runner).

2. **Type identity is automatic.** With a single shared context, the contract assemblies (`Dmon.Extensions`, `Dmon.Abstractions`, `Microsoft.Extensions.AI`) resolve to one identity for host and every extension. The previous "defer the contract assembly to Default" gamble is no longer load-bearing — there is only one context.

3. **Dependency resolution is explicit.** Loading a single `.dll` does not pull its transitive dependencies. The loader resolves them by probing the extension's own directory (`Assembly.LoadFrom` semantics) and/or an `AssemblyDependencyResolver` reading the extension's `.deps.json`. This *closes* a gap that the previous `LoadFromAssemblyPath`-only code had.

4. **Unload means deregister.** `extension.unload <name>` removes the extension's tools from the registry and stops exposing them to the LLM. The assemblies remain resident until the process exits. This is an honest description of what already happens today; the protocol/UX wording is updated to say so.

5. **Hot-reload is process restart.** Reclaiming an extension's code, or picking up a changed extension assembly, is done by restarting the `Dmon.Core` child process. `CoreProcessManager` gains a restart path; the terminal re-binds its stdio loop to the new process and re-opens the same session. Restart happens strictly between turns, consistent with the existing rule that extension registration never occurs during an active streaming call.

## Consequences

- **Conflicting dependency versions are not supported.** If two extensions require different versions of the same third-party package, the first one loaded wins; the second receives that version or fails on a strong-name mismatch. Accepted for V1. If this ever bites in practice, a future ADR can reintroduce per-extension contexts for the conflicting subset — the contract assemblies would still be pinned to Default.
- **No memory reclaim without restart.** Long-lived sessions that load and unload many extensions accumulate resident assemblies. This matches today's behaviour (collectible ALCs never collected anyway); restart is the reclaim path.
- **The staged `extension-middleware-tier` change becomes safe.** Its design decision D6 ("middleware needs a stable pipeline reference for the process lifetime") was in direct conflict with the previous "unload the previous context on next load" behaviour. With Default-context loading, nothing is ever unloaded out from under the folded `IChatClient` pipeline, so the conflict disappears. This ADR is therefore sequenced **before** the middleware change.
- **`.csx` tier is unaffected in substance.** Dotnet.Script continues to own script compilation and loading; only the dead, never-wired `ScriptAssemblyLoadContext` field is removed.
- **ALC was never a security boundary, and nothing here weakens trust.** Both tiers run extension code with full trust; ADR-006 gates the *act of loading*, not runtime capability. Moving to the Default context changes nothing about the trust model — it removes footguns, it does not remove a sandbox.
- **Simpler, more predictable loader.** Removing per-load context lifecycle, the inert script ALC, and the type-identity fallback eliminates the latent "unload a live context" bug and the silent-discovery-failure mode.

## Relationship to ADR-002

ADR-002 remains the authority for the extension **contract** (`IDmonExtension` exposing `AIFunction`), the two-tier model (`.csx` + NuGet/assembly), the `promote` path, and permission-gated loading. This ADR supersedes **only** ADR-002's statement that NuGet extensions are "loaded via collectible `AssemblyLoadContext`." Where the two disagree on loading mechanism, ADR-008 governs.
