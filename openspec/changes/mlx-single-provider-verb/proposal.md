## Why

MLX ships today only as the daemon's keyed triage pair (`AddMlxFirstline`/`AddMlxEscalation`), which register `MlxProviderExtension` as keyed router backends — deliberately **not** as active-provider candidates (per the ADR-027 note in `AddMlxExtensions.cs`). There is no way to select MLX as the single active terminal provider in a non-triage composition root, so a plain single-model agent (e.g. an Apple-Silicon coding agent) cannot run on MLX the way it can on llama.cpp via `UseLlamaCpp`. This change adds the symmetric verb.

## What Changes

- Add a `UseMlx` composition verb to `Dmon.Providers.Mlx` (new file `UseMlxExtensions.cs`, `namespace Dmon.Hosting`) that registers `MlxProviderExtension` as the **active** provider via `AddProvider` and sets `mlx/<modelId>` as the default active model via `UseModel` — mirroring `UseLlamaCpp`.
- Two overloads: `UseMlx(MlxRuntimeOptions options)` and a convenience `UseMlx(string modelId, int port = 8666)`.
- The convenience overload defaults the port to **8666**, deliberately distinct from the daemon's fixed firstline (8800) and escalation (8810) ports, so a standalone `UseMlx` runtime attaches/spawns its own server instead of silently colliding with the daemon's firstline E4B chat model (MLX is fixed-port attach-first).
- `UseMlx` supplies **no** silent model default — the caller passes an explicit model id. (The keyed runtimes' `Firstline()`/`Escalation()` chat defaults are wrong for a general active provider, and there is no MLX coding-model default worth baking in.)
- The existing keyed-runtime verbs, their router-backend role, and ADR-027's triage composition are unchanged.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `mlx-provider`: adds a requirement for a single-model **active-provider** composition verb (`UseMlx`) alongside the existing keyed-runtime verbs. The keyed runtimes remain router backends; this is an additive, non-triage registration path.

## Impact

- **Package:** `Dmon.Providers.Mlx` (`IsPackable=true`, protocol-lockstep per ADR-023/024). Adding a public verb is an additive, non-breaking API change.
- **New code:** `providers/Dmon.Providers.Mlx/UseMlxExtensions.cs` + unit tests in the MLX provider test project.
- **No change** to `MlxProviderExtension`, `MlxRuntimeOptions`, the keyed verbs, the daemon composition root, or any ADR (ADR-027 is honoured — see `design.md`).
- **Motivating consumer (out of scope):** a future `sandbox-code/Dmon.cs` coding-agent root using `.UseMlx(...).AddBuiltinTools()`. Not part of this change.
