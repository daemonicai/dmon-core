## Context

`Dmon.Providers.Mlx` was built for the daemon's triage composition (ADR-027, ADR-034): two keyed runtimes — `AddMlxFirstline` (port 8800, gemma-4-e4b) and `AddMlxEscalation` (port 8810, gemma-4-26B) — registered as keyed `MlxProviderExtension` singletons the router resolves via `sp.MlxClient(key)`. The verb comment in `AddMlxExtensions.cs` is explicit that these runtimes "do NOT register as `IProviderExtension` — the runtimes are router backends, not active-provider candidates (ADR-027)."

`MlxProviderExtension` itself already implements `IProviderExtension` (it is the same shape `AddProvider` consumes). The llama.cpp sibling exposes `UseLlamaCpp<T>(this T, string)` / `UseLlamaCpp<T>(this T, LlamaCppOptions)` in `namespace Dmon.Hosting`, each doing `registration.AddProvider(new LlamaCppProviderExtension(options)).UseModel("llamacpp", options.ModelId)`. MLX has no equivalent, so a single-model (non-triage) MLX agent is currently impossible.

## Goals / Non-Goals

**Goals:**
- Add `UseMlx` verbs symmetric to `UseLlamaCpp`, registering MLX as the active terminal provider in a non-triage composition root.
- Default the convenience overload's port to 8666 so a standalone runtime does not collide with the daemon's 8800/8810.
- Keep the change additive and non-breaking; leave the keyed-runtime path and ADR-027's triage composition untouched.

**Non-Goals:**
- No change to `MlxProviderExtension`, `MlxRuntimeOptions`, or the keyed verbs.
- No MLX coding-model default (the caller supplies the model id).
- The motivating `sandbox-code/Dmon.cs` consumer is **out of scope** — this change is the verb + tests + spec only.
- No idle-eviction / warming wiring (that is daemon-scheduler territory; a standalone `UseMlx` agent has no escalation partner to warm).

## Decisions

**D1 — Mirror `UseLlamaCpp` exactly.** `UseMlx` is `registration.AddProvider(new MlxProviderExtension(options)).UseModel("mlx", options.ModelId)`. Same generic constraint `where T : IProviderRegistration`, same `namespace Dmon.Hosting`, same active-model key convention (`<provider>/<modelId>`). Rationale: symmetry with the established provider-verb pattern is the least-surprise design and reuses the proven `AddProvider`/`UseModel` seam. *Alternative rejected:* a bespoke keyed-active registration — unnecessary; `MlxProviderExtension` is already an `IProviderExtension`.

**D2 — Port default 8666, distinct from 8800/8810.** MLX is fixed-port attach-first: `EnsureRunningAsync()` attaches to whatever healthy server is already on the runtime's port. If `UseMlx` defaulted to 8800, a standalone agent started while the daemon is up would silently attach to the daemon's firstline E4B **chat** model instead of the agent's chosen model — a confusing, hard-to-diagnose failure. 8666 gives the standalone runtime its own port so it spawns its own server. *Alternative rejected:* dynamic/ephemeral port — MLX's whole lifecycle model is fixed-port attach-first (unlike llama.cpp's ephemeral port); a fixed distinct default fits the existing design and keeps attach-first reuse working across restarts.

**D3 — No silent model default.** `MlxRuntimeOptions` defaults `ModelId` to empty; the keyed defaults come from `Firstline()`/`Escalation()`, which are **chat** models tuned for the triage pair. Baking one of those in as the active-provider default would mislead (a user calling `UseMlx()` expecting a coding agent would silently get a chat model). The caller passes an explicit `modelId`; the convenience overload's only defaulted argument is the port.

**D4 — ADR-027 is honoured, no ADR change.** ADR-027 governs the daemon's triage routing, where the two MLX runtimes are backends and must not compete as active-provider candidates. `UseMlx` introduces a *separate, non-triage* registration path for single-model composition roots — it does not add active-provider candidates to a triage composition, and does not alter how `AddMlxFirstline`/`AddMlxEscalation` register. The two paths are orthogonal and never mixed in one root. Therefore a design note suffices; **no new or amending ADR is required.** The existing `AddMlxExtensions.cs` comment stays accurate for the keyed verbs.

## Risks / Trade-offs

- **Port collision if a user overrides `UseMlx` back onto 8800/8810 while the daemon runs** → Not prevented in code (fixed-port attach-first is the provider's contract). Documented via the 8666 default and the design rationale; overriding the port is an explicit user choice.
- **A user expects `UseMlx()` to "just work" like `UseLlamaCpp("repo")` with a default model** → Mitigated by requiring an explicit model id; the XML doc on the verb states there is no chat/coding default and points at `mlx-community` model ids.
- **`IsApplicable()` still gates MLX to Apple-Silicon-with-uv** → Unchanged and correct; on a non-applicable host the provider simply is not registered as active, matching llama.cpp's behaviour when its binary is absent.

## Migration Plan

Additive, non-breaking. No migration: existing daemon composition roots continue to use the keyed verbs unchanged. New single-model roots opt in via `UseMlx`. Rollback is deleting the new file (no other code references it).

## Open Questions

None. (ADR-027 framing resolved in D4; port default fixed at 8666 by user decision; model-default policy fixed in D3.)
