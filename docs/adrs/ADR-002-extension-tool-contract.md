# ADR-002: Extension Tool Contract

**Date:** 2026-05-22
**Status:** Accepted

## Context

daemon has a two-tier extension model: `.csx` scripts (hot-loaded via `Dotnet.Script.Core`, written mid-session by the agent or user) and NuGet packages (full-fat extensions loaded via collectible `AssemblyLoadContext`). Both tiers need to expose tools to the agent's LLM in a way that is consistent, low-friction for extension authors, and not tightly coupled to daemon internals.

Two options were considered for the tool contract:

**Option A:** Extensions expose `IEnumerable<AIFunction>` directly (using `Microsoft.Extensions.AI`).

**Option B:** Extensions expose an `IDaemonTool` interface; the core wraps these into `AIFunction` adapters at registration time.

Option B would insulate extensions from a change in provider abstraction library. Option A means extension authors write standard `Microsoft.Extensions.AI` code — the same code they'd write for any M.E.AI-based system.

## Decision

**Option A.** Extensions expose `AIFunction` instances directly via a minimal daemon-specific interface:

```csharp
public interface IDaemonExtension
{
    string Name { get; }
    string Description { get; }
    IEnumerable<AIFunction> Tools { get; }
}
```

The only daemon-specific concern is `IDaemonExtension` itself. All tool logic — parameter schemas, invocation, descriptions — is pure `Microsoft.Extensions.AI`. Extension authors do not need to know anything about daemon internals.

### .csx scripts

`.csx` scripts are loaded and executed via `Dotnet.Script.Core` (not raw Roslyn scripting). The critical capability this adds over raw `Microsoft.CodeAnalysis.CSharp.Scripting` is NuGet package resolution via the `#r "nuget:..."` directive — scripts can pull in arbitrary packages at runtime, not only assemblies already loaded by the daemon host.

```csharp
// example agent-authored script
#r "nuget: HtmlAgilityPack, 1.11.54"
using HtmlAgilityPack;

return AIFunctionFactory.Create(
    ([Description("URL to scrape")] string url) => { /* ... */ },
    "ScrapeTable",
    "Extracts tables from a web page"
);
```

A script returns one or more `AIFunction` instances. The script host exposes `AIFunctionFactory` and other M.E.AI types in the scripting context. Scripts do not implement `IDaemonExtension` — they are loaded directly into the session tool registry.

NuGet resolution in scripts involves network access (feed) and filesystem writes (package cache). Both pass through the permission gate (ADR-006) with `risk: high`; the user sees what package is being pulled before resolution begins.

> **Spike required.** `Dotnet.Script.Core`'s embedding API (using it as a library rather than a CLI tool) is not well-documented. A throwaway spike must verify the embedding path, NuGet resolution, and `AssemblyLoadContext` isolation before implementation begins. See the implementation proposal for the spike task group.

### NuGet extensions

A NuGet extension package contains one or more types implementing `IDaemonExtension`. The core discovers them via reflection on load, instantiates them, and registers their `Tools` collections into the session tool registry.

Loading an extension is a permission-gated act (`risk: high`) — the same as runtime NuGet resolution in scripts. `extension.load {source}` may name either:

- A local path to an already-resolved assembly (no network access; gate covers loading untrusted code into the process)
- A NuGet package id, optionally with version (the core resolves and downloads to a per-extension cache under `~/.daemon/extensions/<package>/<version>/`; both the fetch and the load surface to the permission gate)

The prompt shows the source string and, for NuGet sources, the resolved package id and version before any network call begins. Loaded extensions are not implicitly trusted across sessions — the load action prompts each time unless the user approves the source at project or global scope.

### The `promote` path

The path from `.csx` script to NuGet package is:

1. Wrap the script's `AIFunction` instantiation in an `IDaemonExtension` class.
2. Scaffold a `.csproj` referencing `Microsoft.Extensions.AI` and `Daemon.Extensions` (the package that exports `IDaemonExtension`).
3. Extract any `#r "nuget:..."` directives from the script into `<PackageReference>` elements in the `.csproj`.
4. The script body becomes the extension's constructor or factory method.

The agent can scaffold this automatically via the `promote` command. The `#r` → `<PackageReference>` extraction makes the transition mechanical and automatable.

## Consequences

- **Low barrier for extension authors.** Writing a daemon extension is writing standard M.E.AI code. Authors already familiar with M.E.AI (or Semantic Kernel, which uses M.E.AI underneath) need only learn `IDaemonExtension`.
- **Ecosystem compatibility.** Semantic Kernel plugins can be bridged to `AIFunction` via SK's own utilities, making the SK plugin ecosystem a potential source of daemon extensions with a thin adapter shim.
- **Extensions are not daemon-exclusive.** An `AIFunction` implementation written for daemon can be used in any other M.E.AI-based system without modification.
- **The `promote` path is minimal.** Graduating a working `.csx` script to a NuGet extension requires only adding `IDaemonExtension` boilerplate and a `.csproj` — no rewriting of tool logic.
- **Coupling to `Microsoft.Extensions.AI`.** If the provider abstraction layer is ever replaced, all extensions would need updating. This is considered an acceptable risk: M.E.AI is a first-party Microsoft library with strong stability guarantees.
- **Tool registry is per-session and per-call.** `ChatOptions.Tools` is built from the current registry on each LLM call, so extensions loaded or unloaded mid-session are reflected immediately.
- **`Dotnet.Script.Core` is a community dependency.** Unlike raw Roslyn (first-party), this adds a community library. It is actively maintained and .NET 10 compatible (as of May 2026), but the embedding API requires a spike to validate before implementation. If the spike fails, fallback is raw Roslyn scripting with the limitation that scripts cannot reference NuGet packages.
- **NuGet resolution in scripts requires permission gate integration.** Scripts pulling packages at runtime go through the permission model with `risk: high`.
