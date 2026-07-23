# DEVLOG — mlx-active-provider-self-heal

## NEXT
- **All 12 tasks done.** 4.3 live-verified by the user (2026-07-23): rebuilt `sandbox-code`, ran a `.UseMlx(..., port: 8666)` turn — the model responded, created and ran a hello-world in C, and prompted for tool permissions. Confirms BOTH halves of the fix: runtime self-heal (no `Connection refused`) AND tool-calling advertised/used. Change is complete → propose `/opsx:archive` (awaiting user confirmation).

## Block 2 — Single-source the keyed path (tasks 2.1, 2.2, 3.4, 4.1, 4.2) — DONE
Commit scope: `providers/Dmon.Providers.Mlx/MlxClientExtensions.cs` + new `test/Dmon.Providers.Mlx.Tests/MlxClientExtensionsTests.cs`. Reviewer (Opus) signed off; gates green (build 0 warnings, full suite EXIT=0, Mlx 88/88, validate strict OK). Folded gate tasks 4.1/4.2 into this block (they *are* the standard block gates).

### What changed
- `MlxClient(key)` reduced to: resolve the keyed `MlxProviderExtension`, build `ProviderConfig { BaseUrl = null, DefaultModelId = ext.Options.ModelId, ... }`, and `return ((MlxProviderFactory)ext.CreateFactory()).CreateAsync(modelCfg, apiKey: null, ct)` — dropped `async`, returns the `ValueTask` directly. Removed the helper's own up-front `EnsureRunningAsync` **and** its own outer `EnsureRunningChatClient` wrap; both now come solely from the factory (Block 1). XML doc updated to say self-heal is single-sourced in the factory.
- New test `MlxClientExtensionsTests`: builds a keyed extension via the attach-first lifecycle seam (`isRunningProbe → true`, no spawn), registers it under `MlxRuntimeKeys.Firstline`, calls `sp.MlxClient(...)`, asserts `IsType<EnsureRunningChatClient>` + `ensureRunningCalls == 1` (the pre-D4 double-probe would be 2). Does not dispatch → no port dialled.

### Decisions / why
- **Keyed port fidelity holds (the critical check):** `CreateFactory() => new MlxProviderFactory(_options, _runtimeState, this)` carries the *keyed* extension's own options/state, and `EnsureRunningAsync` seeds `_runtimeState.BaseUrl = http://{Host}:{Port}/v1` before `CreateAsync` reads it, so `BaseUrl = null` falls back to the correct keyed URL (firstline :8800 / escalation :8810). No misrouting. Architect verified this before briefing; reviewer re-verified end-to-end.
- **2.2 was confirmation-only:** `AddMlxFirstline`/`AddMlxEscalation`, `EscalationWarmingService`, `MlxRuntimeKeys`, core, daemon all untouched. Keyed runtimes still resolved as router backends (`GetRequiredKeyedService`), NOT registered as active-provider `IProviderExtension` (ADR-027 honoured).

### Reviewer note carried forward (non-blocking, not fixed)
- `MlxClientExtensionsTests.cs:50` comment over-claims that the `IsType<EnsureRunningChatClient>` assertion guards against a nested double-wrap — `IsType` only checks the outermost type. The real double-wrap/double-probe guard is `Assert.Equal(1, ensureRunningCalls.Value)` (line 55), which genuinely fails on pre-D4 code. Test is sound; only the comment is imprecise. Fold into a later touch if convenient.

## Block 1 — Factory owns the self-heal (tasks 1.1, 1.2, 1.3, 3.1, 3.2, 3.3) — DONE
Commit scope: `providers/Dmon.Providers.Mlx/` + `test/Dmon.Providers.Mlx.Tests/`. Reviewer (Opus) signed off; gates green (build 0 warnings, full suite EXIT=0, Mlx 87/87, validate strict OK).

### What changed
- `MlxProviderFactory` — both ctors (public + internal HTTP-stub) gained an `MlxProviderExtension _extension` parameter/field. `CreateAsync` is now genuinely `async ValueTask<IChatClient>`: it `await`s `_extension.EnsureRunningAsync(ct)` **first**, then `GetCapabilities()`, then builds `ChatClient → MlxMaxTokensDefaulter → CapabilitiesDecorator`, then returns `new EnsureRunningChatClient(decorated, _extension)` (outermost). No more `ValueTask.FromResult`.
- `MlxProviderExtension.CreateFactory()` — one-line change: passes `this` alongside the shared `_options`/`_runtimeState`.
- Tests — `MakeFactory` builds the extension via the attach-first **seam** (`isRunningProbe: _ => Task.FromResult(true)`, throwaway provision delegate, fake `probeClientFactory`) so nothing spawns `mlx_lm.server` or dials a port. Capability tests now drive `SupportsToolCalling` **through the probe** (not by pre-seeding `state`). New: `ProbeFakeChatClient` fake, `CreateAsync_InvokesEnsureRunning_AndReturnsSelfHealingClient` (3.2), `CreateAsync_ActiveProviderClient_AdvertisesToolCalling_WhenProbeVerified` (3.3).

### Decisions / why
- **Ordering invariant is the crux (design D2):** `CapabilitiesDecorator` snapshots `caps` **by value** at construction, so the probe (inside `EnsureRunningAsync` → `RunToolCallingProbeAsync`, which writes `_runtimeState.ToolCallingVerified`) MUST run before `GetCapabilities()`. Factory + extension share the **same** `_runtimeState` instance, so the fresh probe result surfaces. Verified end-to-end by the reviewer.
- **3.1 rode with group 1** because the ctor-signature break stops the test project compiling, and awaiting `EnsureRunningAsync` changes every existing factory test's runtime behaviour — that rework *is* 3.2/3.3, so all landed as one green commit.
- **Clean break, no shim** (design D5, "no production deployments"): all construction sites updated directly.

### Deferred out of this block
- Keyed path (2.1/2.2/3.4) and gate tasks (4.x) — untouched. Core, `MlxClient`, `EscalationWarmingService`, `MlxRuntimeKeys` unchanged.

### Reviewer notes carried forward (non-blocking, not fixed)
- Two cosmetic comment-wording nits in `MlxProviderFactoryTests.cs` (~:178 "during construction" → "during CreateAsync"; the `ensureRunningCalls` counter name over-claims — it increments in the `isRunningProbe` seam). Left as-is; fold into a later touch if convenient.
- **Architectural (team-level, not this change):** `EnsureRunningAsync` re-runs the full ~2048-token tool-calling probe on **every** call, so via the outer `EnsureRunningChatClient` every user turn incurs a liveness + tool-probe completion before the real turn. Pre-existing (identical to the shipped keyed `MlxClient` path); design.md D2 accepts "per-request self-heal thereafter." Future idea: gate the probe on `ToolCallingVerified is null`. Out of scope here.
