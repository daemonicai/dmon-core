## Context

The Daemon's `TriageRouter` (`daemon/Daemon.Routing`, ADR-027) is a `DelegatingChatClient` that runs an upfront structured-output classify pass and dispatches each turn to one of three backends: e2b-with-tools, a local reasoner (`Tier.Reasoner`), or egress. The classifier picks the compute tier *before* any work runs; the handler cannot escalate mid-turn. Backends are pre-built as bare `IChatClient`s in `Daemon.cs` (`UseTriage(e2b)` + `AddReasoner` + `AddEgress`) and resolved synchronously in `TriageRouterFactory.Create`.

This change replaces upfront tier-dispatch with a **handler-initiated escalation ladder** (e4b first-line → `think_harder` → 26b), drops the reasoner, and moves both local models onto **oMLX** as a lifecycle-managed provider. The design was locked in an `/opsx:explore` session, including a spike that confirmed the escalation mechanism against `Microsoft.Extensions.AI` 10.5.2.

Binding constraints:
- **ADR-027 D1**: `ITerminalClientFactory.Create` is synchronous and does no I/O.
- **ADR-027 D2–D4**: `IAbilityProvider`/`AbilityRegistry`, opaque string scope — unchanged here.
- **ADR-007**: only the *active* provider's `EnsureRunningAsync` is gated by `Build()`. The `TriageRouter` is the terminal client via `ITerminalClientFactory`, so oMLX is never the active provider — `Build()` will not bring it up.
- **CLAUDE.md**: amending a binding ADR requires an accepted superseding/amending ADR first (ADR-032 below).

## Goals / Non-Goals

**Goals:**
- A fast first-line model attempts every local turn and self-escalates to a larger model only when struggling, with the larger model *continuing* the work.
- Both local models served by oMLX, auto-launched on demand.
- Keep the privacy guarantees (egress gate, personal-bias override, misclassification metric) intact and unchanged.
- Keep `ITerminalClientFactory.Create` synchronous while still allowing async backend construction + bring-up.

**Non-Goals:**
- No change to the core seams `ITerminalClientFactory`, `IAbilityProvider`, `AbilityRegistry`, or the string-scope model.
- No multi-rung ladder beyond e4b → 26b (26b is the top rung).
- No startup warm-up of oMLX in this change (deferred; first turn pays cold-start).
- No `daemon-scheduler` work (this change lands first and reshapes the router it builds on).

## Decisions

### D1 — Escalation via `FunctionInvocationContext.Terminate` (spike-confirmed)
`think_harder` is a **registered** `AIFunction` in the first-line manifest whose delegate sets `FunctionInvokingChatClient.CurrentContext!.Terminate = true` and returns a sentinel. M.E.AI 10.5.2 exposes both `FunctionInvocationContext.Terminate` ("terminate the request" — the loop stops immediately rather than continuing) and `FunctionInvokingChatClient.CurrentContext`. Calling `think_harder` stops the first-line function-invocation loop, leaving a **well-formed call+result pair** in `response.Messages`. The router detects `FunctionCallContent.Name == "think_harder"` and re-dispatches.

*Alternative considered:* `FunctionInvokingChatClient.TerminateOnUnknownCalls` (declare `think_harder` but don't register it, let the unknown-call path terminate). Rejected: it leaves a **dangling** call (no result) that can upset strict providers; it fires for *any* hallucinated tool name (conflating model error with intentional escalation); and it treats a deliberate control signal as an error path. `Terminate` is well-formed, selective, and intentional.

### D2 — Lazy backend resolution keeps `Create` sync and closes ADR-027 OQ-A
The three verbs take `Func<IServiceProvider, ValueTask<IChatClient>>`. `TriageRouterFactory.Create(sp)` only *captures* the delegates + `IServiceProvider` (no I/O — ADR-027 D1 honored) and constructs the `TriageRouter`. Each backend is resolved **lazily on first turn** inside `TriageRouter.GetResponseAsync` (already async), cached per-backend via `Lazy<Task<IChatClient>>` (concurrency-safe under simultaneous first turns). This is exactly the "deferred async backend constructors resolved lazily on first turn" pattern that **closes ADR-027 Open Question A**.

oMLX bring-up lives **inside the factory delegate** (`await ext.EnsureRunningAsync(); return await factory.CreateAsync(modelCfg)`), the natural home given `Build()` won't gate a non-active provider (ADR-007).

*Alternatives considered:* (a) Make `ITerminalClientFactory.Create` async — rejected: widens the blast radius to the core seam, `Build()`, and every consumer, and turns OQ-A into a breaking core change. (b) Sync `Func<ISP, IChatClient>` with sync-over-async on `CreateAsync` — rejected: code smell and no clean home for `EnsureRunningAsync`.

### D3 — `UseOmlx` registers a lifecycle provider; per-model clients via the factory
`UseOmlx` follows the `UseMtplx` pattern: `AddProvider(new OmlxProviderExtension(...))`, **non-hijacking** (does not force the active model), so the router-as-terminal-client is not overridden. A small `sp.OmlxClient(model)` helper composes `EnsureRunningAsync` + `OmlxProviderFactory.CreateAsync(modelCfg)` into a `ValueTask<IChatClient>`, so `Daemon.cs` reads:
```
builder.UseOmlx();
builder.UseTriage    (sp => sp.OmlxClient(firstLineModel));   // classifier + first-line (one raw client, two uses)
builder.AddEscalation(sp => sp.OmlxClient(escalationModel));
builder.AddEgress    (egress);                                // Gemini, eager overload
```
The classifier and first-line share the one e4b client (mirroring today's reuse of `e2bRaw`): raw for the structured classify call, FIC-wrapped + `think_harder` for first-line handling.

### D4 — Continue, with the `think_harder` pair stripped
On escalation the escalation client receives `[...inputMessages, ...firstLineResponse.Messages]` minus the `think_harder` `FunctionCallContent`/`FunctionResultContent` pair, so it continues the gathered work but never sees a tool it wasn't offered. `think_harder` is specified to the model as call-alone, because `Terminate` may drop other same-iteration tool calls.

### D5 — Streaming buffers the first-line
Because streamed tokens can't be un-sent, the streaming path buffers the first-line generation and streams only the *committed* backend. The ack is emitted before the first-line runs but no longer names the final route; a distinct escalation marker precedes the escalation stream on handoff.

### D6 — Type and config deltas
- `RouteContracts`: delete the `Tier` enum; `RouteDecision` becomes `{ Scope, Impersonal, Confidence }`.
- Rename `ReasonerClient` → `EscalationClient`; `AddReasoner` → `AddEscalation`.
- Rename the misleading `e2b` vocabulary (`E2bRawClient`, `e2bWithTools`) to role-based (`firstLine`/`local`).
- Config: drop `DMON_E2B_URL`, `DMON_REASONER_URL`, `DMON_REASONER_MODEL`; add `DMON_FIRSTLINE_MODEL`, `DMON_ESCALATION_MODEL` (both over `OMLX_BASE_URL`); `DMON_EGRESS_MODEL` default → `gemini-3.1-flash-lite`.
- `Daemon.cs` `#:project`: add `Dmon.Providers.Omlx`; remove the OpenAI provider ref (only the reasoner used it); remove Ollama only if nothing else needs it (verify first).

## Risks / Trade-offs

- **First-turn latency**: lazy resolution means the first message pays oMLX cold-start (`open -a oMLX` + model load). → Acceptable for a personal daemon; an opt-in startup warm-up `IHostedService` is a clean future enhancement.
- **4-bit classifier reliability**: the e4b structured-output classify pass on a heavily-quantized model could emit unparseable JSON. → Existing fail-safe is confident-*personal* (privacy-safe degrade); covered by tests for the unparseable path.
- **`Terminate` + parallel tool calls**: a `think_harder` called alongside real tools in one iteration may drop the real calls. → Spec constrains `think_harder` to be called alone; escalation still triggers safely if violated.
- **`Terminate` behavior pin**: relies on M.E.AI 10.5.2 semantics (spike-confirmed). → A test asserts first-line termination + handoff; an M.E.AI bump must re-verify.
- **Stripping correctness**: removing the `think_harder` pair must keep the message list well-formed for the escalation provider. → Targeted test on the inherited-message construction.
- **Concurrent first turns**: lazy init races. → `Lazy<Task<IChatClient>>` per backend; test covers concurrent first calls.

## Migration Plan

No production deployments — clean break, no back-compat. The amending **ADR-032** must be written and accepted before implementing tasks that contradict ADR-027 D5. Implementation order: ADR-032 → oMLX verb/helper → router contracts/types → escalation + continue → streaming → `Daemon.cs` rewire + config → spec/standing-sync. Rollback is reverting the change branch.

## Open Questions

- **User-visible escalation signal**: exact ack/marker text (`[triage: local]` then `[escalating]`?) — finalize during implementation; the spec only requires *an* ack and *a distinct* escalation marker.
- **Warm-up**: confirm deferring the startup warm-up `IHostedService` is acceptable for the first cut (current assumption: yes).
