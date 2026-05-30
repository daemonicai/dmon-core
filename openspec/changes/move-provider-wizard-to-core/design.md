## Context

The provider setup wizard is an interactive, multi-step flow: a user picks a provider, then answers a provider-specific sequence of questions (API key, model, …) that the provider's own `IProviderFactory.GetNextStepAsync(WizardState)` produces one step at a time. Today this loop runs **inside the Terminal process**:

```
TERMINAL PROCESS                                  CORE PROCESS
┌──────────────────────────────────┐            ┌──────────────────────┐
│ Program.cs                        │            │ ProviderRegistry      │
│   new AnthropicProviderFactory()  │            │   (own factory set,   │
│   new OpenAiProviderFactory()     │            │    used for turns +   │
│   new GeminiProviderFactory()     │            │    GetAvailableModels)│
│   new OllamaProviderFactory()     │            │                       │
│            │                      │            │                       │
│            ▼                      │            │                       │
│ ConsoleEventHandler               │            │                       │
│   HandleAddProviderAsync          │            │                       │
│            ▼                      │            │                       │
│ WizardEngine.RunAsync             │            │                       │
│   loop: factory.GetNextStepAsync  │            │                       │
│   renderer.render(WizardStep)     │            │                       │
│   capture answer → WizardState    │            │                       │
│            ▼ WizardResult         │  RPC       │                       │
│   ProviderConfigureCommand ───────┼───────────▶│ configure + persist   │
└──────────────────────────────────┘            └──────────────────────┘
```

Constraints that shape the design:

- **ADR-003** — Core and Terminal are separate processes communicating over JSONL/stdio; the wire shape lives in `Dmon.Protocol`. New message types must be documented in specs.
- **ADR-007 / ADR-008** — provider factories can arrive as extensions loaded only into Core's `AssemblyLoadContext`. The Terminal cannot see them.
- **Existing facts** (verified): `Dmon.Protocol` is a dependency-free leaf; `Dmon.Abstractions` references only `Microsoft.Extensions.AI`; `Dmon.Providers` references only `Dmon.Abstractions`; the `WizardStep` family already lives together in `src/Dmon.Abstractions/Wizard/`. Core already runs stateful prompt round-trips (`UiInputRequestEvent`/`UiInputResponseCommand`, `tool.confirmRequest`/`Response`) via a `ConcurrentDictionary<string, TaskCompletionSource<…>>` keyed by event id in `TurnHandler`.

## Goals / Non-Goals

**Goals:**
- The wizard engine and factory enumeration run in `Dmon.Core`; the Terminal only renders steps and returns answers over RPC.
- A provider factory loaded as a Core-only extension automatically participates in the wizard (provider-selection enumerates Core's DI factories).
- `Dmon.Terminal` no longer references `Dmon.Providers`.
- One source of truth for `WizardStep`: the type the factory returns is the type that travels on the wire — no DTO mirroring, no mapping boundary.
- `Dmon.Protocol` stays a dependency-free leaf.

**Non-Goals:**
- Changing the model-switch flow (`model.list` / `model.models` round-trip) — it is already RPC-correct and stays as-is.
- Authoring a real provider extension (this change only unblocks the capability).
- Adding new provider SDKs or changing `IProviderFactory`'s method *set*.
- Multi-connection / concurrent-wizard support (single human, single stdio connection).

## Decisions

### D1 — Re-home `WizardStep` into `Dmon.Protocol`; `Dmon.Abstractions` references `Dmon.Protocol`

The wizard payload must cross the wire. Three ways to get it there:

- **(A) Reuse `ui.inputRequest`.** Cheapest (no new types), but its `Options` is `IReadOnlyList<string>` — it cannot carry `WizardOption.{Label,Value,Description}`, so it **regresses the existing tested requirement** and caps every future extension wizard at Text/Secret/Select. Rejected.
- **(B) Mirror `WizardStep` as Protocol DTOs.** Faithful, but every step type exists twice with a hand-written `Abstractions ↔ Protocol` mapping. Boilerplate and drift risk. Rejected.
- **(C) `Dmon.Protocol → Dmon.Abstractions` reference.** Drags `Microsoft.Extensions.AI` and the whole abstraction tree into the wire layer and couples the protocol to types that change for non-wire reasons. Rejected.
- **(D, chosen) `Dmon.Abstractions → Dmon.Protocol` reference, move the types down.** The serializable wizard types are *re-homed* into `Dmon.Protocol`. `WizardStep` becomes a third `[JsonPolymorphic]` serialization root alongside `Command` and `Event`. The factory returns a `WizardStep` that **is** the wire type. Protocol stays the leaf (the edge flows *into* it; M.E.AI does not leak in). The "wire couples to an internal type" objection from (C) dissolves: `WizardStep`'s identity is now *defined* to be a protocol contract, so changes to it are protocol changes by construction, reviewed under ADR-003.

Acyclic check: `Dmon.Protocol` references nothing today and continues to reference nothing; `Dmon.Abstractions → Dmon.Protocol` introduces no cycle. `Dmon.Providers` (refs only `Dmon.Abstractions`) gains a transitive `Dmon.Protocol` dependency — harmless, since Protocol is a pure leaf and this does not violate the "Providers references no `Dmon.Core` type" rule.

### D2 — `WizardState` stays in `Dmon.Abstractions`

`WizardState` is the accumulating answered-step list threaded through `GetNextStepAsync(WizardState)`. With the engine in Core, **it never crosses the wire** — Core holds it across round-trips. It belongs with the factory *contract*, not the wire payload. Clean split: **Protocol = what serializes (`WizardStep`, `WizardOption`); Abstractions = the contract (`IProviderFactory`) + in-process state (`WizardState`)**. `WizardState` referencing `WizardStep` across the new `Abstractions → Protocol` edge is fine.

### D3 — Answer fields are `[JsonIgnore]` on the wire type

The spec mandates a settable answer that flips `IsAnswered`. On the wire Core emits only the **question** portion of a step (unanswered); the answer returns via `WizardAnswerCommand` and Core applies it to its own `WizardState` copy. So the mutable answer fields are retained for in-process accumulation but marked `[JsonIgnore]` — they never serialize outbound. No behavioural change to the engine's view of a step.

### D4 — Engine extends the existing `ProviderSetupHandler` in `Dmon.Core/Rpc`

Setup is not a turn, so the engine does **not** belong on `TurnHandler`. `ProviderSetupHandler` **already exists** in `Dmon.Core/Rpc` and today handles only `ProviderConfigureCommand` — it persists the provider YAML stanza (global/local scope), calls `IProviderRegistry.AddDynamicProvider`, and emits `ProviderConfiguredEvent`. This change **extends** that handler (not a new one) to also own the wizard step loop, routed by `CommandDispatcher`. It builds provider-selection (step 0) by enumerating Core's DI-registered `IProviderFactory` instances — the same set Core already uses for turns and model listing. The model-list step calls `factory.GetAvailableModelsAsync` **in-process** (no `model.models` RPC during setup). At a `WizardCompletedStep`, the handler completes by reusing its **existing `ConfigureAsync` persistence path** (which already persists to scope and emits `ProviderConfiguredEvent`) — no duplicated persistence logic.

### D5 — Three new RPC carrier messages; reuse `ProviderConfiguredEvent` for completion

Re-homing the payload (D1) does not remove the need for envelopes. New types:

| Message | Direction | Shape | Discriminator |
|---|---|---|---|
| `WizardStartCommand` | Terminal → Core | `{ id }` | `wizard.start` |
| `WizardStepEvent` | Core → Terminal | `{ wizardId, WizardStep step }` (embeds the polymorphic step) | `wizard.step` |
| `WizardAnswerCommand` | Terminal → Core | `{ id, wizardId, outcome: Answered\|Back\|Cancel, value }` | `wizard.answer` |

Completion reuses the existing `ProviderConfiguredEvent` — no new terminal-facing completion event. Back (truncate the last answered step, re-invoke `GetNextStepAsync`) and cancel (abandon, persist nothing) are carried by the `outcome` field.

```
TARGET
 TERMINAL (no Dmon.Providers ref)            CORE (factories live here only)
 ┌─────────────────────────────┐            ┌──────────────────────────────────┐
 │ /add-provider               │            │ ProviderSetupHandler              │
 │   → WizardStartCommand ──────┼───RPC─────▶│   WizardSession{State,factory}    │
 │                             │            │   step0 = ChooseOne(DI factories) │
 │ render step ◀ WizardStepEvt ─┼────────────┤   loop: factory.GetNextStepAsync  │
 │ capture answer              │            │         (GetAvailableModelsAsync   │
 │   → WizardAnswerCommand ─────┼───RPC─────▶│          in-process)              │
 │   (Answered|Back|Cancel)    │            │   on Completed → persist global    │
 │ done ◀ ProviderConfiguredEvt─┼────────────┤   + emit ProviderConfiguredEvent  │
 └─────────────────────────────┘            └──────────────────────────────────┘
```

### D7 — Non-blocking dispatch for long-running commands (substrate fix, discovered during apply)

**Problem found during apply review.** The stdin reader is serial: `RpcHostedService` reads a line, `await`s `CommandDispatcher.DispatchAsync`, and only then reads the next line. `DispatchAsync` `await`s the route inline, and `turn.submit → TurnHandler.SubmitAsync` `await`s `RunTurnAsync` to completion. `RunTurnAsync` suspends on a `TaskCompletionSource` waiting for `tool.confirmResponse`/`ui.inputResponse` — which can only arrive as a *later stdin line* the loop cannot read while blocked. The wizard's `wizard.start → StartWizardAsync` has the identical shape, blocking on `wizard.answer`. This is a **deadlock**, latent in the existing turn round-trips (no end-to-end test ever exercised a response over the real loop) and fatal to the wizard, whose entire purpose is the round-trip.

**This is a bug against ADR-003, not a new decision.** ADR-003 already states: *"Commands are fire-and-forget with an event stream back. `turn.submit` does not return a long-running response"* and *"the core suspends the turn until the response arrives."* "Suspends the **turn**" — not the reader. The inline-await implementation contradicts that. The fix conforms the code to the accepted ADR; we add a clarifying **"Dispatch concurrency"** subsection to ADR-003 rather than a superseding ADR.

**Decision.** Long-running interactive commands (`turn.submit`, `wizard.start`) are dispatched on a **tracked background task**; `DispatchAsync` returns to the reader immediately for these, so subsequent lines (`tool.confirmResponse`, `ui.inputResponse`, `wizard.answer`, `turn.abort`) are read and routed to resolve the suspended operation's `TaskCompletionSource`. Short, non-interactive commands (`model.*`, `session.*`, `provider.configure`, the response/answer commands themselves) continue to be awaited inline — they don't block on a future line. Concurrency safety is unchanged: at most one turn is enforced by the existing `_turnGate`, and at most one wizard by the session guard (D6); errors on the background task are surfaced as `ErrorEvent`s, never lost. Background tasks are tracked (not bare `_ = Task.Run`) so shutdown can await/observe them.

**Alternatives considered.** (a) Leave it inline and require the host to pre-send answers — impossible, the request must be emitted before the host can answer. (b) A second reader thread / channel — more concurrency surface than needed; the background-task-per-long-command approach keeps a single reader and bounded concurrency. (c) A separate prerequisite change — rejected by the user in favour of folding the fix in, since the wizard cannot function without it and the engine work is already in flight.

### D6 — Session keying: by wizard id, at most one active

Following the `_pendingUiInputs`/`_pendingConfirms` precedent, `ProviderSetupHandler` holds the active `WizardSession` keyed by the start command's id. At most one wizard is active (single human, single connection per ADR-003). Keying by id lets the handler reject/ignore a stale `WizardAnswerCommand` that arrives for a wizard that was already cancelled or completed.

## Risks / Trade-offs

- **Public namespace move of `WizardStep` types** → `using` updates across all factory implementations (`Dmon.Providers/*Factory.cs`, `Dmon.Providers.Ollama/OllamaProviderFactory.cs`) and the engine. Mechanical, caught at compile time by `TreatWarningsAsErrors`.
- **`Dmon.Protocol` gains behaviour-bearing records** (init question + `[JsonIgnore]` settable answer + computed `IsAnswered`) where it previously held pure data → mild. Mitigation: keep the answer state minimal and clearly `[JsonIgnore]`; the wire surface is question-only.
- **`Dmon.Providers` gains a transitive `Dmon.Protocol` dependency** → acceptable; Protocol is a dependency-free leaf and the existing rule only forbids referencing `Dmon.Core`. Mitigation: encode the leaf invariant as a requirement so it is enforced going forward.
- **Two provider-knowledge sources removed → one** (Core only). The Terminal's hard-coded factory list disappears; if a user expected a provider the Terminal "knew" but Core does not register, it now correctly will not appear. This is the intended behaviour, not a regression.
- **Wizard becomes a live RPC conversation** rather than a local loop → more messages on the wire. Mitigation: the round-trip primitive (TCS-keyed pending interaction) already exists and is proven for turns/tools; the wizard reuses the same discipline.

## Migration Plan

1. Move `src/Dmon.Abstractions/Wizard/*` (the serializable step types + `WizardOption`) to `src/Dmon.Protocol/Wizard/*`; add `[JsonPolymorphic]`/`[JsonDerivedType]` to `WizardStep`; `[JsonIgnore]` on answer fields. Keep `WizardState` in `Dmon.Abstractions`.
2. Add the `Dmon.Abstractions → Dmon.Protocol` project reference. Build to confirm no cycle and that factory `using` directives resolve.
3. Add the three carrier messages and register their discriminators on `Command`/`Event`.
4. Add `ProviderSetupHandler` to `Dmon.Core/Rpc` (engine + session dict + DI factory enumeration + persistence on completion); route it in `CommandDispatcher`.
5. Rework `ConsoleEventHandler.HandleAddProviderAsync` to drive the RPC conversation; delete `WizardEngine.cs` from the Terminal.
6. Delete the factory list in `Program.cs` and remove the `Dmon.Terminal → Dmon.Providers` project reference.
7. Update the `provider-setup-wizard` and `provider-factories` specs.

Rollback: the change is a single branch; revert restores the terminal-side engine and the `Dmon.Providers` reference. No persisted data format changes (config persistence already exists in Core via `ProviderConfigureCommand`).

## Known limitations

- **ChooseMany renders as single-select in the terminal.** The terminal renderer maps `ChooseManyStep` onto a single-pick selection prompt (emitting one index), because no built-in factory emits a `ChooseManyStep` and the terminal's prompt surface has no multi-select primitive yet. The wire contract (`WizardAnswerCommand.Value` as comma-separated indices) and the Core decode already support multiple indices, so adding true multi-select is a terminal-only enhancement when an extension wizard first needs it. Documented so it is not mistaken for full support.

## Open Questions

- Discriminator naming: `wizard.start`/`wizard.step`/`wizard.answer` vs a `provider.setup*` prefix. Leaning `wizard.*` since the carrier is generic over wizards; resolve during implementation, keep consistent with the spec.
- ~~Whether the existing `ProviderConfigureCommand` path is now dead.~~ **Resolved during apply pre-flight:** `ProviderConfigureCommand` is handled by the existing `ProviderSetupHandler.ConfigureAsync`, which is the persistence path the wizard now reuses internally at completion. Keep `ProviderConfigureCommand` (do not remove it in this change); the wizard reuses its handler logic rather than re-sending the command over RPC.
