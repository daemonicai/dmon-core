## 1. ADR amendment (must be accepted before contradicting ADR-027 D5)

- [x] 1.1 Write `docs/adrs/ADR-032-handler-initiated-escalation.md` (status Accepted; **Amends:** ADR-027): Decision 5 routing policy becomes handler-initiated escalation (first-line → `think_harder` → escalation) replacing upfront tier-dispatch; `AddReasoner`→`AddEscalation`; the three backend verbs take lazy `Func<IServiceProvider, ValueTask<IChatClient>>` delegates resolved on first turn. Explicitly state core seams D1–D4 are unchanged and that this **closes ADR-027 Open Question A** via the lazy-in-router pattern.
- [x] 1.2 Update the ADR table in `CLAUDE.md` with the ADR-032 row.

## 2. oMLX as a lifecycle-managed provider

- [ ] 2.1 Add `providers/Dmon.Providers.Omlx/UseOmlxExtensions.cs`: `UseOmlx` verb(s) in the `Dmon.Hosting` namespace on `IProviderRegistration`, registering `OmlxProviderExtension`, **non-hijacking** (no forced active model), config via `OmlxConfig` (env/defaults) — mirror `UseMtplxExtensions`.
- [ ] 2.2 Add an `OmlxClient(this IServiceProvider sp, string model)` helper returning `ValueTask<IChatClient>` that composes `EnsureRunningAsync()` + `OmlxProviderFactory.CreateAsync(modelCfg)` for a specific model id.
- [ ] 2.3 Tests: `UseOmlx` registers the provider and is resolvable from DI; non-hijacking behavior; env config honored; two distinct per-model clients from one provider; bring-up paired with construction.

## 3. Router contracts and type renames

- [ ] 3.1 `RouteContracts.cs`: delete the `Tier` enum; change `RouteDecision` to `{ string Scope, bool Impersonal, float Confidence }`. Keep `TriageOptions.EgressThreshold`.
- [ ] 3.2 Rename `ReasonerClient` → `EscalationClient`; `AddReasoner` → `AddEscalation`. Rename the `e2b` vocabulary (`E2bRawClient`, `e2bWithTools`) to role-based names (first-line / local) across `daemon/Daemon.Routing`.
- [ ] 3.3 Change the three verbs in `TriageRegistrationExtensions.cs` to take `Func<IServiceProvider, ValueTask<IChatClient>>` (`AddEgress` keeps an eager `IChatClient` overload that wraps as `sp => ValueTask.FromResult(client)`).

## 4. Lazy backend resolution

- [ ] 4.1 `TriageRouterFactory.Create` captures the delegates + `IServiceProvider` and constructs the `TriageRouter` synchronously (no I/O — ADR-027 D1).
- [ ] 4.2 In `TriageRouter`, resolve each backend lazily on first use via `Lazy<Task<IChatClient>>` (concurrency-safe); classifier and first-line share the one first-line client (raw for classify, FIC-wrapped for handling).
- [ ] 4.3 Tests: `Create` performs no I/O; backends resolved once and cached; concurrent first turns resolve a single instance per backend.

## 5. Escalation flow

- [ ] 5.1 Define the `think_harder` `AIFunction`: delegate sets `FunctionInvokingChatClient.CurrentContext!.Terminate = true` and returns a sentinel; offered to the first-line manifest only, never to the escalation client.
- [ ] 5.2 First-line dispatch: build the effective-scope manifest (post privacy-gate) plus `think_harder`; wrap the first-line client with `UseFunctionInvocation`; run it.
- [ ] 5.3 Detect escalation: scan the first-line `response.Messages` for `FunctionCallContent.Name == "think_harder"`; if present, build the inherited message list (`input + response.Messages` minus the `think_harder` call+result pair) and dispatch to the escalation client (manifest without `think_harder`).
- [ ] 5.4 Non-streaming `GetResponseAsync`: classify → egress / first-line → escalation; return the committed backend's response.
- [ ] 5.5 Tests: think_harder terminates first-line and hands off; escalation manifest excludes think_harder; partial tool work carried forward; think_harder call+result stripped; first-line answer without escalation returned directly.

## 6. Streaming path

- [ ] 6.1 `GetStreamingResponseAsync`: emit an ack (not naming the final route); buffer the first-line generation; if escalated, emit a distinct escalation marker then stream the escalation client; else stream the (buffered) first-line answer.
- [ ] 6.2 Tests: ack precedes any backend output; no first-line draft tokens streamed when escalation occurs; escalation marker precedes the escalation stream.

## 7. Privacy invariants retained (regression)

- [ ] 7.1 Confirm/keep: effective-scope manifest from the post-gate scope; personal-bias override on low confidence; `dmon.triage.misclassify.personal_to_world` increment; no cross-turn caching. Add/keep tests asserting each still holds under the new flow (incl. world tools absent from personal manifests on every backend).

## 8. Daemon composition + config rewire

- [ ] 8.1 `Daemon.cs`: `builder.UseOmlx()`; `UseTriage(sp => sp.OmlxClient(firstLineModel))`; `AddEscalation(sp => sp.OmlxClient(escalationModel))`; `AddEgress(egress)`. Remove the reasoner wiring.
- [ ] 8.2 Config: drop `DMON_E2B_URL`, `DMON_REASONER_URL`, `DMON_REASONER_MODEL`; add `DMON_FIRSTLINE_MODEL`, `DMON_ESCALATION_MODEL` (over `OMLX_BASE_URL`); `DMON_EGRESS_MODEL` default → `gemini-3.1-flash-lite`.
- [ ] 8.3 `Daemon.cs` `#:project` refs: add `Dmon.Providers.Omlx`; remove `Dmon.Providers.OpenAI` (reasoner-only); remove `Dmon.Providers.Ollama` only if nothing else needs it after rewire (verify first).

## 9. Spec sync and gates

- [ ] 9.1 Run `openspec validate daemon-triage-escalation --strict`; fix any delta-format issues.
- [ ] 9.2 `make build` clean (TreatWarningsAsErrors); `env -u MEKO_API_KEY make test` green (new + existing).
- [ ] 9.3 On archive, fold the delta specs into `openspec/specs/triage-routing` and `openspec/specs/omlx-provider` (kept for the archive step, not this change).
