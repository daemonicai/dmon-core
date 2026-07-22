## Why

The `UseMlx` active-provider composition verb (mlx-provider spec, shipped in PR #104) registers MLX as the active provider, but the terminal `IChatClient` it produces never starts the `mlx_lm` runtime. A standalone `.UseMlx(model, port)` agent throws `Connection refused (127.0.0.1:8666)` on its very first turn, and — because the tool-calling probe never runs — would report `SupportsToolCalling == false` even if a server were started by hand. The verb is effectively non-functional for its only intended consumer (a single-model, non-triage composition root).

## What Changes

- Make the MLX active-provider terminal client **self-heal**: the client produced by `MlxProviderFactory.CreateAsync` SHALL ensure the runtime is running (attach-first) before each request, and SHALL have run the tool-calling probe before its capabilities are snapshotted, so tools are correctly advertised.
- Move the self-heal wrapper (`EnsureRunningChatClient`) into the factory as the **single source of truth**. Today it is wired only in the keyed daemon helper `MlxClient(key)`; the factory — which builds the terminal client on the active-provider path — omits it. `EnsureRunningChatClient`'s own doc comment already states it "sits outermost over the decorator stack built by `MlxProviderFactory`", i.e. this restores the intended design.
- Simplify the keyed helper `MlxClient(key)` to return the factory's now-self-healing client, dropping its duplicate up-front `EnsureRunningAsync` and its own `EnsureRunningChatClient` wrap. Daemon behaviour is unchanged; self-heal is single-sourced.
- **BREAKING** (internal only, no published consumers): `MlxProviderFactory`'s constructor gains an `MlxProviderExtension` parameter and `MlxProviderExtension.CreateFactory()` supplies it; `CreateAsync` becomes genuinely async (awaits `EnsureRunningAsync`). Both types are first-party; no back-compat shim is needed (no production deployments).

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `mlx-provider`: The active-provider terminal client (the one produced for a `UseMlx` registration) SHALL start and self-heal the runtime and reflect the verified tool-calling capability. The keyed router-backend path SHALL obtain its self-heal from the same single source (the factory), not a duplicate wrapper.

## Impact

- **Code:** `providers/Dmon.Providers.Mlx/` only — `MlxProviderFactory` (ctor + `CreateAsync` ordering/async + outermost wrap), `MlxProviderExtension.CreateFactory()`, `MlxClientExtensions.MlxClient` (drop duplicate self-heal), and the provider tests (`test/Dmon.Providers.Mlx.Tests` — several internal ctors/seams construct `MlxProviderFactory` directly).
- **No core changes.** `TurnHandler`/`ProviderRegistry` are untouched; the fix rides the existing `factory.CreateAsync` path.
- **No ADR changes.** ADR-027 (terminal-client selection) and ADR-034 (MLX runtime lifecycle) are honoured — this restores the intended self-heal on the active-provider terminal client and preserves keyed router-backend semantics (`AddMlxFirstline`/`AddMlxEscalation`, `EscalationWarmingService` untouched).
- **Consumer:** unblocks the throwaway `sandbox-code/Agent.cs` local MLX coding-agent harness (`.UseMlx("mlx-community/gemma-4-26B-A4B-it-qat-nvfp4", port: 8666)`).
