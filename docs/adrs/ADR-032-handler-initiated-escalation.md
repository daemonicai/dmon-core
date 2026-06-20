# ADR-032: Handler-Initiated Escalation

**Date:** 2026-06-20
**Status:** Accepted
**Amends:** ADR-027 (Decision 5 — routing-policy shape; closes its Open Question A)
**Builds on:** ADR-007 (provider-extension lifecycle)

## Context

The Daemon's `TriageRouter` (ADR-027, `daemon/Daemon.Routing`) runs an upfront structured-output classify pass and dispatches each turn to one of three pre-built backends: an e2b-with-tools handler, a local reasoner (`Tier.Reasoner`), or a gated cloud egress. The tier is chosen *before* any work runs; the handler cannot escalate mid-turn if it encounters a hard problem.

Amending a binding ADR requires an accepted amending ADR first (CLAUDE.md workflow rule). This ADR is that gate for the routing-policy shape change. Later blocks in the `daemon-triage-escalation` change implement the code against this accepted decision.

The upfront-dispatch design has two weaknesses:

1. The classifier must correctly predict difficulty before seeing any generation, which forces a heavier (hence slower or more expensive) first-line model to handle everything the classifier flags as non-trivial, even when most turns would have been fine on a lighter model.
2. The reasoner is a separate local model that responds independently rather than *continuing* accumulated work. The router cannot hand it partial results.

The `daemon-triage-escalation` change replaces upfront tier-dispatch with a **handler-initiated escalation ladder**: a fast first-line model (e4b) attempts every local turn and self-escalates to a larger model (26b) only when struggling, with the larger model *continuing* the gathered work. Both models are served by oMLX. ADR-027 D1–D4 — the core seams `ITerminalClientFactory`, `IAbilityProvider`, `AbilityRegistry`, and the opaque string scope — are **entirely unchanged** by this ADR; only the routing-policy shape in `daemon/Daemon.Routing` is amended.

## Decision

1. **Routing policy becomes handler-initiated escalation.** The first-line client (e4b) is offered a registered `think_harder` tool alongside the turn's ability-scoped manifest. Calling `think_harder` terminates the first-line function-invocation loop. The router detects `FunctionCallContent.Name == "think_harder"` and re-dispatches the turn to the escalation client (26b), which *continues* with the accumulated messages (first-line response messages included, minus the `think_harder` call+result pair — the escalation client is never offered `think_harder`). Upfront classification narrows to **egress-eligibility only**: the classifier pass decides only whether the turn crosses the privacy boundary for cloud egress; per-local-tier dispatch is eliminated.

2. **Escalation mechanism: `FunctionInvocationContext.Terminate` (spike-confirmed M.E.AI 10.5.2).** `think_harder` is a **registered** `AIFunction` whose delegate sets `FunctionInvokingChatClient.CurrentContext!.Terminate = true` and returns a sentinel string. `FunctionInvocationContext.Terminate` instructs M.E.AI to stop the function-invocation loop immediately rather than continuing; `FunctionInvokingChatClient.CurrentContext` is the per-call ambient context accessor that exposes it. This leaves a **well-formed call+result pair** in `response.Messages`. `think_harder` is specified to the model as call-alone, because `Terminate` may drop other same-iteration tool calls.

   *Rejected alternative:* `FunctionInvokingChatClient.TerminateOnUnknownCalls` — declare `think_harder` but do not register it, letting the unknown-call path terminate. Rejected: it leaves a dangling call (no result) that can upset strict providers; it fires for *any* hallucinated tool name (conflating model error with intentional escalation); and it treats a deliberate control signal as an error path. `Terminate` is well-formed, selective, and intentional.

3. **Lazy backend resolution — `Create` stays synchronous (closes ADR-027 OQ-A).** The three backend verbs (`UseTriage`, `AddEscalation`, `AddEgress`) take `Func<IServiceProvider, ValueTask<IChatClient>>` delegates. `TriageRouterFactory.Create(sp)` only *captures* the delegates and the `IServiceProvider` (no I/O, no awaits — ADR-027 D1 honoured). Each backend is resolved **lazily on first turn** inside `TriageRouter.GetResponseAsync` (already async), cached per-backend via `Lazy<Task<IChatClient>>` (concurrency-safe under simultaneous first turns). This is the "deferred async backend constructors resolved lazily on first turn" pattern that **closes ADR-027 Open Question A** without making `ITerminalClientFactory.Create` async and without widening the blast radius to core.

   oMLX bring-up lives **inside the factory delegate** (`await ext.EnsureRunningAsync(); return await factory.CreateAsync(modelCfg)`), the natural home given that `Build()` does not gate a non-active provider (ADR-007). `AddEgress` retains an eager `IChatClient` overload (no async bring-up needed for a cloud client constructed at build time).

4. **Escalation continues with inherited messages, `think_harder` pair stripped.** On escalation, the escalation client receives `[...inputMessages, ...firstLineResponse.Messages]` with the `think_harder` `FunctionCallContent`/`FunctionResultContent` pair removed. The escalation client sees the accumulated tool calls and results from first-line work but never sees a tool it was not offered.

5. **Streaming buffers the first-line; only the committed backend streams to the caller.** Because streamed tokens cannot be un-sent, the streaming path buffers first-line generation. The ack is emitted before the first-line runs but no longer names the final route upfront; a distinct escalation marker precedes the escalation stream on handoff.

6. **Type and config deltas.** Drop `Tier` enum; `RouteDecision` becomes `{ Scope, Impersonal, Confidence }`. Rename `ReasonerClient` → `EscalationClient`; `AddReasoner` → `AddEscalation`. Rename the `e2b` vocabulary (`E2bRawClient`, `e2bWithTools`) to role-based names (`firstLine`/`local`). Config: drop `DMON_E2B_URL`, `DMON_REASONER_URL`, `DMON_REASONER_MODEL`; add `DMON_FIRSTLINE_MODEL`, `DMON_ESCALATION_MODEL` (both routed to `OMLX_BASE_URL`); `DMON_EGRESS_MODEL` default → `gemini-3.1-flash-lite`. `Daemon.cs` acquires a `#:package` ref to `Dmon.Providers.Omlx`; the OpenAI provider ref (used only for the reasoner) is removed.

**ADR-027 D1–D4 are explicitly unchanged:** D1 — `ITerminalClientFactory.Create(IServiceProvider)` is synchronous and does no I/O; D2 — `IAbilityProvider`/`AbilityRegistry` `ForScope` is called per-turn, never cached; D3 — `Scope` is an opaque `string` in core (no enum); D4 — `AbilityRegistry` is orthogonal to `IToolExtension`. The location rule from ADR-027 D5 is also unchanged: routing policy lives in `daemon/Daemon.Routing`, not `middleware/`, and `middleware/` stays empty.

## Consequences

- **Mid-turn escalation is now possible.** The first-line model can detect its own limitations and hand off, rather than the classifier having to predict difficulty upfront.
- **No upfront route name in the ack.** Clients observing the streaming wire see the ack first, then either first-line output or `[escalating]` + escalation output.
- **Reasoner removed.** The separate local reasoner model is dropped; the escalation client (26b on oMLX) takes its role but operates as a continuation, not an independent response.
- **Lazy-in-router closes ADR-027 OQ-A without a core breaking change.** `ITerminalClientFactory.Create` stays synchronous; no change to core, `Build()`, or non-Daemon consumers.
- **oMLX bring-up on first turn.** `Build()` does not warm up oMLX (it is never the active provider — ADR-007). The first message pays the cold-start cost. An opt-in startup warm-up `IHostedService` is a clean future enhancement, deferred to a later change.
- **`Lazy<Task<IChatClient>>` per backend.** Concurrent first turns are handled safely without a lock; subsequent turns pay only a `Task` read.

## Alternatives

- **`TerminateOnUnknownCalls`.** Rejected — see Decision 2. Leaves a dangling call, fires for any hallucinated tool name, conflates error with intent.
- **Make `ITerminalClientFactory.Create` async.** Rejected — widens the blast radius to the core seam, `Build()`, and every consumer of `ITerminalClientFactory`. This would turn ADR-027 OQ-A into a breaking core change rather than a local Daemon.Routing concern.
- **Sync `Func<IServiceProvider, IChatClient>` with sync-over-async on `EnsureRunningAsync`.** Rejected — code smell; `EnsureRunningAsync` has no clean home in a sync delegate.

## Open Questions

None. ADR-027 Open Question A (async terminal-client construction) is closed by Decision 3 of this ADR via the lazy-in-router pattern.

## Relationship to other ADRs

- **ADR-027** — amended: Decision 5 (routing-policy shape) is replaced by this ADR's escalation ladder. Open Question A is closed by Decision 3. Decisions 1–4 (core seams, ability registry, opaque scope, registry orthogonality) are **unchanged**.
- **ADR-007** — relied on: only the active provider's `EnsureRunningAsync` is gated by `Build()`. Because the `TriageRouter` is the terminal client via `ITerminalClientFactory`, oMLX is never the active provider; its bring-up therefore lives inside the lazy factory delegate, the natural home.
- **ADR-019** — unaffected: `DmonHostBuilder.Build()` terminal-client selection (the `ITerminalClientFactory` precedence rule added by ADR-027) is unchanged. The hosting surface and `RunAsync` loop are unmodified.
- **ADR-022** — unaffected: the host-verb grammar (`UseTriage`/`AddEscalation`/`AddEgress`) is amended in shape (lazy delegates, renamed verb) but the composition-root facet model and `IDmonHostBuilder` surface are unchanged.
