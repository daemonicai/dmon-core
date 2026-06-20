# DEVLOG — daemon-triage-escalation

Narrative companion to `tasks.md`. Cross-block memory for the (fresh-each-block) architect.

## Pinned decisions (read before planning any block)

- **ADR-032 is written and Accepted** (block 1) — it amends ADR-027 D5 and closes ADR-027 OQ-A. Later code blocks may now contradict the *old* ADR-027 D5 routing-policy shape; they must stay consistent with ADR-032.
- **Escalation mechanism (spike-confirmed, M.E.AI 10.5.2):** `think_harder` is a *registered* no-op `AIFunction` whose delegate sets `FunctionInvokingChatClient.CurrentContext!.Terminate = true`. Detect via `FunctionCallContent.Name == "think_harder"` in the first-line `response.Messages`; on continue, **strip the think_harder call+result pair** before handing to the escalation client. Chosen over `TerminateOnUnknownCalls`. `think_harder` is offered to first-line only, never to the escalation (top-rung) client, and must be specified as call-alone.
- **Lazy resolution:** the three verbs (`UseTriage`/`AddEscalation`/`AddEgress`) take `Func<IServiceProvider, ValueTask<IChatClient>>` (`AddEgress` also keeps an eager `IChatClient` overload). `TriageRouterFactory.Create` stays **synchronous, no I/O** (ADR-027 D1) — it only captures delegates + `IServiceProvider`. Backends resolve **lazily on first turn** inside `TriageRouter.GetResponseAsync`, cached per-backend via `Lazy<Task<IChatClient>>` (concurrency-safe). oMLX `EnsureRunningAsync` bring-up lives **inside the factory delegate** (ADR-007: `Build()` only gates the active provider, and the router is the terminal client, so oMLX is never active).
- **ADR-027 core seams D1–D4 are untouched** by this change — only D5 (routing-policy shape) is amended.
- **Classifier & first-line share one e4b client** (mirroring today's reuse of `e2bRaw`): raw for the structured classify call, FIC-wrapped + `think_harder` for first-line handling.
- **Streaming:** buffer the first-line (tokens can't be un-sent on escalation); only the committed backend streams; the ack no longer names the final route upfront; emit a distinct escalation marker before the escalation stream.
- **Privacy invariants are RETAINED unchanged:** effective-scope manifest from the post-gate scope; personal-bias override on low confidence; `dmon.triage.misclassify.personal_to_world` metric; no cross-turn caching. (triage-routing spec keeps these requirements.)
- **Sample to mirror:** `providers/Dmon.Providers.Mtplx/UseMtplxExtensions.cs` for the `UseOmlx` verb (non-hijacking — don't force the active model).
- **ADR numbering:** ADR-029 is reserved by the active `daemon-scheduler` change — do not use it. We took **032**.
- **Test gotcha:** run tests as `env -u MEKO_API_KEY make test` (a set MEKO_API_KEY makes the Meko live-smoke test hang ~90s).
- **oMLX backends are caller-owned `IDisposable`s** (block-2 reviewer note): `OmlxProviderFactory.CreateAsync` mints a fresh `HttpClient` per call wrapped in the returned `IChatClient`. So `sp.OmlxClient(model)` hands back something the caller must dispose. **The routing block (group 3–7) must dispose the cached router backends on `TriageRouter` teardown** (the `Lazy<Task<IChatClient>>` cache holds them). This is the established `IProviderFactory` contract — no new leak class — but the router is now the owner.
- **`OmlxClient` / `UseOmlx` are ready to consume** (block 2): `sp.OmlxClient(model, ct)` (`ValueTask<IChatClient>`, `Dmon.Hosting` namespace) does `EnsureRunningAsync` + per-model `CreateAsync`; `UseOmlx()` is non-hijacking. `Daemon.cs` (task 8.1) calls `builder.UseOmlx()` then `UseTriage(sp => sp.OmlxClient(firstLineModel))` / `AddEscalation(sp => sp.OmlxClient(escalationModel))`. Helper `ProviderConfig.Auth` is inert (factory resolves key via `apiKey ?? _config.ApiKey`); `.First()` picks the single registered oMLX extension.

## Blocks

### Block 1 — tasks 1.1–1.2 — ADR-032 + CLAUDE.md row (docs-only) ✅
- Wrote `docs/adrs/ADR-032-handler-initiated-escalation.md` (Status: Accepted; Amends ADR-027 D5; closes OQ-A) following the ADR-027 file format; transcribes design D1–D6; explicitly affirms D1–D4 unchanged + location/`middleware/`-empty rules upheld.
- Added the ADR-032 row to the `CLAUDE.md` ADR table (after ADR-028, terse pipe style).
- Reviewer: **Approve**, no blockers; two non-blocking nits (header vs table phrasing of "Amends"; "Builds on: ADR-007" — both defensible, left as-is).
- Gates: `make build` clean (0 warn); `env -u MEKO_API_KEY make test` green (only pre-existing skips); `openspec validate --strict` valid.
- Note for a future code block: when implementing change §2/§5, confirm the actual M.E.AI 10.5.2 surface names match `FunctionInvokingChatClient.CurrentContext` (accessor) + `FunctionInvocationContext.Terminate` (property).

### Block 2 — tasks 2.1–2.3 — UseOmlx verb + OmlxClient helper + tests ✅
- Added `providers/Dmon.Providers.Omlx/UseOmlxExtensions.cs` (`UseOmlx<T>()` env-sourced + `UseOmlx<T>(OmlxConfig)`, both non-hijacking — never call `UseModel`) and `OmlxClientExtensions.cs` (`OmlxClient(this IServiceProvider, string model, CancellationToken)`), both in `Dmon.Hosting`.
- Added `test/Dmon.Providers.Omlx.Tests/UseOmlxTests.cs` (~10 tests; `FakeProviderRegistration` scaffold mirrors UseMtplxTests; bring-up via internal `isRunningProbe` ctor — no `open -a oMLX`). Added `Microsoft.Extensions.Configuration` + `.DependencyInjection` refs to the test csproj.
- Reviewer: **Approve**, no blockers. Nits (all left): inert `ProviderConfig.Auth` (could add a clarifying comment); a couple of fully-qualified `IChatClient` in tests; the env-config test asserts via a constructed config rather than a real env-var round-trip (env path is pre-existing tested code).
- Gates: `make build` 0-warn; `env -u MEKO_API_KEY make test` 0 failed (51/51 Omlx.Tests); `openspec validate --strict` valid.

### Block 3 — tasks 3.1–3.3, 4.1–4.3, 5.1–5.5, 6.1–6.2 — TriageRouter rewrite ✅
Full router rewrite (contracts + lazy resolution + escalation + streaming). Group 7 (privacy regression) and group 8 (Daemon.cs rewire) deferred.
- **Contracts:** `Tier` enum deleted; `RouteDecision(string Scope, bool Impersonal, float Confidence)`; `TriageOptions.EgressThreshold` kept. Wrapper records renamed `E2bRawClient`→`FirstLineRawClientFactory`, `ReasonerClient`→`EscalationClientFactory` (+`EgressClientFactory`); all hold `Func<IServiceProvider, ValueTask<IChatClient>>`. `AddReasoner`→`AddEscalation`. All three verbs have eager (`IChatClient`) + lazy (`Func<ISP, VT<IChatClient>>`) overloads.
- **Lazy:** `TriageRouterFactory.Create` captures delegates+ISP synchronously (no I/O — ADR-027 D1). Backends via `Lazy<Task<IChatClient>>` (ExecutionAndPublication). Classifier+first-line share `_lazyFirstLine` (raw for classify, FIC-wrapped for handling).
- **Escalation:** `think_harder` registered `AIFunction` sets `FunctionInvokingChatClient.CurrentContext!.Terminate=true`, returns sentinel `"escalating"`. Detect via `FunctionCallContent.Name=="think_harder"`; **strip by the FULL SET of think_harder CallIds** (calls by name + results by CallId-in-set — robust to call-alone violations, no dangling result). Escalation manifest excludes think_harder.
- **Streaming:** `[triage]` ack (route-agnostic); first-line BUFFERED via non-streaming `GetResponseAsync` (no draft-token leak on escalation); `[escalating]` marker before escalation stream; egress streams directly. Replay is **text-only by design** (commented).
- **Disposal:** `Dispose(bool)` disposes only resolved+`IsCompletedSuccessfully` lazies; inner is a `NopChatClient` so `base.Dispose` can't double-dispose a real backend.
- **Daemon.cs left untouched** (group 8) — it's not in `Everything.slnx`, so `make build`/`make test` don't compile it; it still references old `AddReasoner`/signatures and WILL be rewired in group 8. (Architect confirmed no other in-solution consumer of the renamed symbols.)
- **M.E.AI surface verified** present in 10.5.2. NOTE for group-8 live verification: the unit tests assert the router's detect/strip/handoff given a response that already contains the think_harder pair — they do NOT exercise the real FIC `Terminate` loop-stop. Group 8's live run against oMLX should confirm `Terminate` actually leaves the well-formed call+result pair.
- Reviewer: **Approve with nits**; two substantive nits applied (set-based CallId strip + text-only-replay comment; +1 multi-call test), cosmetic test-helper-naming nit left.
- Gates: `make build` 0-warn; `env -u MEKO_API_KEY make test` all 20 suites green, Daemon.Routing.Tests **32/32**; `openspec validate --strict` valid.
