## Context

Extensions (`IDmonExtension`) are loaded into the Default `AssemblyLoadContext` (ADR-008), declared in `config.yaml` (ADR-009), and instantiated by the loader via `Activator.CreateInstance(type, serviceProvider)` or a no-arg constructor (`NuGetExtensionLoader`). They expose `AIFunction`s and reference only the contract assemblies plus `Microsoft.Extensions.AI`.

Two facts make sub-agent extensions cheap to enable:
- `IProviderFactory` (in `Dmon.Abstractions`) already turns a `ProviderConfig` + key into an `IChatClient` via `CreateAsync`, exposes `AdapterName`, and exposes `DefaultEnvVar` for credential resolution. `GeminiProviderFactory.CreateAsync` is pure (no hidden DI). Factories are registered as `IEnumerable<IProviderFactory>` in core DI.
- `IConfiguration` is already registered in the core container (`DaemonServiceExtensions`), and the injected `IServiceProvider` is that container — so extensions can already resolve it. The in-flight `extension-middleware-tier` change formalises the same idea for middleware (`GetSection("middleware:<name>")`).

What is missing is not capability but **sanction and convention**: an explicit decision that this is in scope, a documented contract that the sub-agent client must be independent, and a named config section so an extension can read `model:`. No new contract type is needed — `IProviderFactory` is sufficient.

## Goals / Non-Goals

**Goals:**
- Sanction sub-agent-backed tools with a clear, recorded boundary against V1's deferred multi-agent orchestration.
- Guarantee an extension can build an independent sub-agent `IChatClient` from a `<adapter>/<model-id>` string using only public types.
- Give command extensions a named config section, consistent with middleware.
- Keep `IDmonExtension` binary-compatible; no RPC/session/permission/auth changes.

**Non-Goals:**
- Multi-agent orchestration (multiple `dmon-core` processes over stdio/RPC) — explicitly out, and this ADR draws the line.
- A general agent framework, tool-nesting, multi-turn sub-agent loops, or sub-agent tool injection. Sub-agents here are single-turn.
- Implementing any specific sub-agent extension (e.g. web search) — that lives out-of-tree.

## Decisions

### D1 — Record the scope boundary as an ADR
A tool extension that constructs a scoped, single-turn in-process `IChatClient` is **not** multi-agent orchestration. Orchestration = multiple `dmon-core` processes over stdio/RPC.
- **Why:** the distinction is not self-evident from code; a reviewer or the Pi agent will otherwise flag the second `IChatClient` as the deferred feature. One accepted ADR settles it permanently.
- **Alternative:** a CLAUDE.md sentence — weaker, not binding like an ADR.

### D2 — Sub-agent construction uses the public `IProviderFactory`, never the registry
The supported path: resolve `IEnumerable<IProviderFactory>` from the injected `IServiceProvider`, select by `AdapterName`, resolve the key from `DefaultEnvVar`, call `CreateAsync`.
- **Why:** `IProviderRegistry.GetCurrentAsync()` is the primary agent's client and `SetModel` mutates it; reuse would corrupt primary state or lose the sub-agent's provider/grounding. Independence is mandatory and must be specified.

### D3 — Per-extension config via a named section, aligned with middleware
Command extensions read their settings from `IConfiguration` resolved off the injected `IServiceProvider`. The section convention mirrors `middleware:<name>`.
- **Why:** reuses an already-registered mechanism; consistency with the middleware tier reduces surface area. Note the existing `extensions:` list parser (`ExtensionsConfigReader`) deliberately avoids the layered `IConfiguration` because array elements collapse by index across layered files; a **name-keyed map** section does not have that problem, so reading a `commands:<name>` map via the layered `IConfiguration` is safe.
- **Resolved (section path):** a **peer top-level `commands:<name>`** section, alongside `middleware:` — *not* nested under `extensions:`. `extension-middleware-tier` settled on a top-level `middleware` section (read as `middleware:<ClassName>:…`), so a peer top-level `commands:` section is the consistent placement.
- **Resolved (key identity):** keyed by the extension's `IDmonExtension.Name` (e.g. `commands:dmon-websearch`). Perfect key-scheme symmetry with middleware is impossible because the contracts differ: `IDmonMiddleware` exposes no name (so middleware keys by `<ClassName>`), whereas `IDmonExtension` exposes `Name`. The harmonization is at the **section shape** (top-level, name-keyed map, arbitrary fields incl. `model:`), which is the part that reduces surface area. *Do not "fix" this asymmetry — it is contract-driven.*
- **Resolved (config service):** read via `IConfiguration`, already registered in `DaemonServiceExtensions` (`sp.GetRequiredService<IConfiguration>()`); no dependency on the `IConfigurationRoot` the middleware tier adds. A name-keyed map reads safely from the layered `IConfiguration`, so task 2.2 holds by construction.

### D4 — No new contract type
Sub-agent construction uses the existing public `IProviderFactory` directly; no `ISubAgentFactory` wrapper is added.
- **Why:** `IProviderFactory` already exposes everything needed (`AdapterName`, `DefaultEnvVar`, `CreateAsync`). A convenience seam would save ~15 lines per extension but adds a contract type to maintain; not worth it for the current, single command extension.

## Risks / Trade-offs

- **[Config-section convention diverges from middleware]** Two in-flight changes define config sections. → Align this change's section path with `extension-middleware-tier`; coordinate before both archive.
- **[Extensions could abuse sub-agents]** Nothing structurally stops a long multi-turn inner loop. → The ADR and spec scope sub-agents to single-turn, tool-call-fulfilling use; not mechanically enforced in V1, documented as the contract.
- **[Credential coupling]** A command extension needs its sub-agent provider's key even when the primary uses a different provider. → Specify a clear error path; this is inherent, not a defect.
- **[`IServiceProvider` contents are implicit]** Extensions depend on `IConfiguration` (and factories) being resolvable from the injected provider. → Make this an explicit, tested guarantee of the loader rather than an incidental fact.

## Migration Plan

Additive. Steps:
1. Write and accept the scope ADR.
2. Confirm/guarantee `IConfiguration` and `IEnumerable<IProviderFactory>` resolve from the extension-facing `IServiceProvider`; add a loader test.
3. Define and document the command-extension config section convention.
No rollback concerns: no existing behaviour changes; absent any command extension, nothing is exercised.

## Open Questions

- ~~Final config-section path, harmonised with `extension-middleware-tier`.~~ **Resolved (D3):** peer top-level `commands:<name>`, keyed by `IDmonExtension.Name`, read via `IConfiguration`.
- ~~Whether the `commands` taxonomy is a sub-key of `extensions:` or a peer top-level section alongside `middleware:`.~~ **Resolved (D3):** peer top-level section alongside `middleware:`.

None outstanding.
