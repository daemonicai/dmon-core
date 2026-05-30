# ADR-010: Sub-Agent Extensions Scope Boundary

**Date:** 2026-05-30
**Status:** Accepted

## Context

Extensions (`IDmonExtension`, ADR-002/ADR-008) expose `AIFunction`s but cannot today run an LLM of their own. A class of useful tools — web search via provider grounding, summarisers, retrievers — are best implemented as a tool that internally runs a scoped, single-turn LLM call. The V1 brief explicitly defers "multi-agent orchestration," and a reviewer or the Pi agent could mistake a second in-process `IChatClient` (ADR-001) for that deferred feature.

`IProviderFactory` (in `Dmon.Abstractions`) already turns a `ProviderConfig` + key into an `IChatClient` via `CreateAsync`, and `IConfiguration` is already registered in the core DI container (resolved from the `IServiceProvider` handed to extension constructors). What is missing is not capability but sanction and convention: an explicit decision that this pattern is in scope, and a documented contract that the sub-agent client must be independent.

This ADR draws the line.

## Decision

1. **A scoped single-turn in-process `IChatClient` is in scope.** A tool extension that constructs and invokes a scoped, single-turn in-process `IChatClient` to fulfil a tool call is a supported pattern. It is not multi-agent orchestration.

2. **Multi-agent orchestration is defined as multiple `dmon-core` processes communicating over the stdio/RPC interface** (ADR-003). That pattern remains deferred in V1. A second `IChatClient` instantiated inside an extension to handle one tool call is simply an extension using an additional LLM model — not orchestration.

3. **Sub-agent construction must be independent.** The supported path is: resolve `IEnumerable<IProviderFactory>` from the injected `IServiceProvider`, select by `AdapterName`, resolve the provider credential from `DefaultEnvVar` (ADR-005), and call `CreateAsync`. An extension must never obtain its sub-agent client from `IProviderRegistry.GetCurrentAsync()` or call `SetModel` — `IProviderRegistry` is mutable primary-agent session state, and coupling to it would corrupt the primary agent's provider or lose sub-agent provider/grounding independence (ADR-007).

4. **Sub-agents are single-turn, scoped to fulfilling the tool call.** No multi-turn inner loops, tool-nesting, or sub-agent tool injection. This is the documented contract; it is not mechanically enforced in V1, but it is binding.

## Consequences

- **Unblocks the `dmon-websearch` extension.** The scope boundary is now an accepted ADR; extensions that run a single-turn grounded LLM call can be shipped without the pattern being flagged as out-of-scope.
- **The boundary is binding.** Accepted ADRs are binding per `CLAUDE.md`; reviewers and the Pi agent must treat this decision as settled.
- **No new contract type.** `IProviderFactory` (ADR-007) is sufficient to construct sub-agent clients; no `ISubAgentFactory` wrapper is introduced (D4 in the `sub-agent-extensions` design). The pattern is additive and backward-compatible with `IDmonExtension` (ADR-002, ADR-008).
- **Credential independence is inherent.** An extension that uses a different provider than the primary agent must supply that provider's key even when it is not the primary provider. This is expected and is not a defect; clear error paths (missing env var → `InvalidOperationException`) should be documented per-extension.

## Alternatives

**A sentence in `CLAUDE.md` only** — weaker, not binding like an ADR; reviewers could still challenge the pattern. Rejected in favour of a formal ADR (`design.md` D1).

## Relationship to other ADRs

- **ADR-001** — sub-agent construction uses `IChatClient` from `Microsoft.Extensions.AI`, consistent with the project-wide LLM abstraction.
- **ADR-002 / ADR-008** — extensions load into the Default `AssemblyLoadContext` and expose `AIFunction`s; this ADR does not change the loading mechanism or the `IDmonExtension` contract.
- **ADR-003** — confirms that multi-agent orchestration (multiple `dmon-core` processes over stdio/RPC) is the deferred pattern, not in-process sub-agents.
- **ADR-005** — credential resolution for sub-agents follows the same API-key-via-env-var pattern.
- **ADR-007** — `IProviderFactory.CreateAsync` is the sanctioned path; `IProviderRegistry` is explicitly off-limits for sub-agents. No new contract type is needed: `IProviderFactory` is sufficient to construct sub-agent clients.
- **ADR-009** — extension loading and config are unchanged; sub-agent capability is additive.
