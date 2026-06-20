## Why

The Daemon's `TriageRouter` decides a turn's compute tier **upfront** from a single classify pass, then dispatches once to one of three fixed backends (e2b-with-tools, a local reasoner, egress). The handler never gets to change its mind, so the cheap local model can't say "this is beyond me" and hand off — the only way to reach more capability is for the classifier to guess "reasoner" before any work happens. We want a **"think harder" ladder**: a fast first-line model attempts every local turn and *self-escalates* to a larger model only when it's actually struggling, with the larger model **continuing** the work rather than restarting. Separately, the local backends are wired to Ollama and a generic OpenAI endpoint; we want both local models served by **oMLX** as a lifecycle-managed provider (auto-launch, model listing), which today has no composition verb.

## What Changes

- **BREAKING** Replace upfront tier-dispatch with **handler-initiated self-escalation**: the first-line model (gemma4-e4b) is offered a `think_harder` tool; calling it hands the turn to the escalation model (gemma4-26b), which **continues** with the inherited message list (partial work + tool results). The 26b is the top rung and is not offered `think_harder`.
- **BREAKING** Drop the qwen reasoner entirely: remove the `AddReasoner` verb, the `ReasonerClient` wrapper, the `Tier` enum, `RouteDecision.Tier`, and the `DMON_REASONER_URL`/`DMON_REASONER_MODEL` config. The 26b takes the reasoner's structural slot but is tool-triggered, not upfront-tier-selected.
- Narrow the upfront classify pass to **egress-eligibility only** (personal vs impersonal-world + confidence). The privacy gate (world→personal below `EgressThreshold`) and the `dmon.triage.misclassify.personal_to_world` metric are retained unchanged.
- **BREAKING** The backend composition verbs take a **lazy factory delegate** `Func<IServiceProvider, ValueTask<IChatClient>>`: `UseTriage(firstLine)`, `AddEscalation(escalation)` (renamed from `AddReasoner`), `AddEgress(...)` (with an eager `IChatClient` convenience overload). Backends are resolved lazily on the first turn; `ITerminalClientFactory.Create` stays synchronous.
- Add the missing **`UseOmlx`** composition verb to `Dmon.Providers.Omlx`, registering oMLX as a lifecycle-managed provider (Apple-Silicon applicability, model listing, `EnsureRunningAsync` bring-up). Wire `Daemon.cs` so both local models are oMLX-served.
- Rework the streaming ack: the first-line response is **buffered** (tokens can't be un-sent if it escalates); only the committed backend streams. The ack can no longer name the final route upfront.
- Rename the misleading `e2b` vocabulary (`E2bRawClient`, `e2bWithTools`) to role-based names (first-line / local) across `daemon/Daemon.Routing`.
- Config: drop `DMON_E2B_URL`, `DMON_REASONER_URL`, `DMON_REASONER_MODEL`; add `DMON_FIRSTLINE_MODEL` and `DMON_ESCALATION_MODEL` (both over `OMLX_BASE_URL`, default `http://localhost:8666`); change `DMON_EGRESS_MODEL` default to `gemini-3.1-flash-lite`.

## Capabilities

### New Capabilities

(none — no new standing capability; the escalation behavior is folded into the existing `triage-routing` spec.)

### Modified Capabilities

- `triage-routing`: replace the upfront classify-dispatch flow (egress / reasoner-tier / e2b) with classify-for-egress + first-line→`think_harder`→escalation, add the `think_harder` `Terminate` mechanism + called-alone constraint + continue-with-inherited-messages/strip-the-pair rules, and rework the streaming-ack requirement for a buffered first-line. Retain the privacy invariant, personal-bias override, no-cross-turn-caching, and misclassification-metric requirements.
- `omlx-provider`: add a `UseOmlx` composition-verb requirement (lifecycle registration: applicability, model listing, `EnsureRunningAsync` bring-up).

## Impact

- **ADR**: New **ADR-032** amending **ADR-027 Decision 5** (handler-initiated escalation replaces upfront tier-dispatch; `AddReasoner`→`AddEscalation`; verbs take lazy `ValueTask` factory delegates) and **closing ADR-027 Open Question A** (async terminal-client construction, via lazy-in-router resolution). ADR-027 core seams D1–D4 (`ITerminalClientFactory`, `IAbilityProvider`/`AbilityRegistry`, string scope) are unchanged.
- **Code**: `daemon/Daemon.cs`; `daemon/Daemon.Routing/{TriageRouter.cs, TriageRegistrationExtensions.cs, RouteContracts.cs, DaemonTelemetry.cs}`; `providers/Dmon.Providers.Omlx/{OmlxProviderExtension.cs, OmlxProviderFactory.cs, OmlxConfig.cs}` + new `UseOmlxExtensions.cs`. New/updated xunit tests under `test/`.
- **Dependencies**: relies on `Microsoft.Extensions.AI` 10.5.2 `FunctionInvocationContext.Terminate` + `FunctionInvokingChatClient.CurrentContext` (spike confirmed). `Daemon.cs` `#:project` refs: add `Dmon.Providers.Omlx`; remove the OpenAI provider ref (was only the reasoner) and the Ollama ref if unused after rewire — verify before removing.
- **Sequencing**: lands **before** the active `daemon-scheduler` change (ADR-029, 0/34) — it reshapes the router the scheduler sits behind.
- **No production deployments**: clean break, no migration/back-compat required.
