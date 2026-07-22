# DEVLOG — mlx-active-provider-self-heal

## NEXT
- **Block 2 = tasks 2.1, 2.2, 3.4** (single-source the keyed path). Simplify `MlxClientExtensions.MlxClient(key)` to resolve the keyed `MlxProviderExtension`, build the `ProviderConfig`, and return `((MlxProviderFactory)ext.CreateFactory()).CreateAsync(...)` — dropping its own up-front `EnsureRunningAsync` and its own `EnsureRunningChatClient` wrap (both now supplied by the factory as of Block 1). Confirm `AddMlxFirstline`/`AddMlxEscalation`, `EscalationWarmingService`, `MlxRuntimeKeys` untouched (2.2). Add test 3.4: `MlxClient(key)` returns a self-healing client sourced from the factory, no double-invoke/double-wrap.
  - Hazard: `MlxClient(key)` currently does its own `EnsureRunningAsync` + wrap. After Block 1 the factory does both — so a naive keep-both would double-wrap (`EnsureRunningChatClient` inside `EnsureRunningChatClient`) and double-probe. The block's whole point is to remove the duplication. Verify the keyed daemon warm/stop semantics (via `EscalationWarmingService`) are behaviourally unchanged.
- **Then gates block = 4.1, 4.2** (full `make build` + `env -u MEKO_API_KEY make test` + `openspec validate --strict`) and **4.3** (human-in-the-loop `sandbox-code` `.UseMlx(...)` verification — needs a copy-pasteable recipe and the user's confirmation before ticking).

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
