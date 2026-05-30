## Why

dmon extensions today expose `AIFunction`s but cannot use an LLM of their own. A whole class of useful tools — web search via provider grounding, summarisers, retrievers — are best implemented as a tool that internally runs a scoped, single-turn LLM call. This change establishes the small, deliberate foundation that lets a tool extension construct its own sub-agent `IChatClient` and read its own configuration, without opening the door to the multi-agent orchestration deferred in V1. It is the prerequisite for the out-of-tree `dmon-websearch` extension.

## What Changes

- Add an ADR recording the scope boundary: a tool extension invoking a **scoped, single-turn `IChatClient`** is *not* the multi-agent orchestration excluded from V1. Multi-agent orchestration is defined as multiple `dmon-core` **processes** communicating over the stdio/RPC interface. A second in-process `IChatClient` to fulfil a tool call is "extensions may use additional LLM models."
- Document and guarantee that a loaded extension can build an **independent** sub-agent client through the existing public provider abstraction (`IProviderFactory`, resolved from the injected `IServiceProvider`), using `CreateAsync` — never the primary agent's `IProviderRegistry` client, which is mutable session state.
- Add a **named per-extension config section** for sub-agent ("command") extensions, read off the injected `IServiceProvider`'s `IConfiguration`, mirroring the `middleware:<name>` mechanism the in-flight `extension-middleware-tier` change introduces. This lets an extension read settings such as `model:`.
- No new contract type is required: extensions construct sub-agent clients directly via the existing public `IProviderFactory`. (No `ISubAgentFactory` wrapper — `IProviderFactory` is sufficient.)
- No change to the RPC protocol, session storage, permission model, or provider auth. No breaking change to `IDmonExtension`.

## Capabilities

### New Capabilities
- `sub-agent-extensions`: The foundation for tool extensions that run a scoped sub-agent — the scope boundary, the guarantee that an independent sub-agent `IChatClient` can be constructed via the public provider abstraction (`IProviderFactory`), and the per-extension named config section.

### Modified Capabilities
<!-- None. The per-extension config mechanism intentionally lives in the new capability to avoid colliding with the in-flight extension-middleware-tier edits to `extension-model`. -->

## Impact

- **New ADR** in `docs/adrs/` (next free number) recording the sub-agent scope boundary.
- **Contract assemblies**: no new types — consumers use the existing public `IProviderFactory` from `Dmon.Abstractions`.
- **Agent core / extension loader**: ensure the `IServiceProvider` handed to extension constructors resolves `IConfiguration` (already registered) and define the peer top-level `commands:<name>` section convention (keyed by `IDmonExtension.Name`, alongside `middleware:`).
- **Config schema**: a peer top-level `commands:<name>` section (keyed by `IDmonExtension.Name`) carrying arbitrary fields (at minimum `model`), alongside the `middleware:` section.
- **Coordinates with** the in-flight `extension-middleware-tier` change: both rely on `IServiceProvider`-carried config and use peer top-level, name-keyed sections (`middleware:<ClassName>` ↔ `commands:<Name>`). The key identity differs because the contracts differ (`IDmonMiddleware` has no name; `IDmonExtension` does).
- **Downstream**: unblocks the `web-search-grounded-extension` change in the `dmon-websearch` repo.
