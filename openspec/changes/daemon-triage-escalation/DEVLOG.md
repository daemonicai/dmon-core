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
