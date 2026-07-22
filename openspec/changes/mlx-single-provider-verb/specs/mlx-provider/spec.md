## ADDED Requirements

### Requirement: Single-model active-provider composition verb

The package SHALL expose `UseMlx<T>(this T registration, MlxRuntimeOptions options) where T : IProviderRegistration` (and a convenience overload `UseMlx<T>(this T registration, string modelId, int port = 8666)`) in `namespace Dmon.Hosting`. Calling the verb SHALL register an `MlxProviderExtension` as an **active** provider via `AddProvider` and set `mlx/<modelId>` as the default active model via `UseModel` — mirroring `UseLlamaCpp`. This registration path is distinct from the keyed-runtime verbs (`AddMlxFirstline`/`AddMlxEscalation`), which register router backends and are NOT active-provider candidates; `UseMlx` targets non-triage single-model composition roots.

The convenience overload SHALL default the port to `8666`, deliberately distinct from the keyed runtimes' fixed firstline (8800) and escalation (8810) ports, so a standalone `UseMlx` runtime attaches to or spawns its own server rather than colliding with a running daemon's firstline runtime (MLX is fixed-port attach-first). `UseMlx` SHALL NOT supply a silent model default: the caller provides an explicit model id via the `modelId` argument or `MlxRuntimeOptions.ModelId`.

#### Scenario: One-line composition registers MLX as the active provider

- **WHEN** a composition root calls `builder.UseMlx("mlx-community/some-model-4bit")` on any `IProviderRegistration` (including `DmonHost.CreateBuilder(args)`)
- **THEN** an `MlxProviderExtension` is registered as an active provider and `mlx/mlx-community/some-model-4bit` is the default active model at startup

#### Scenario: Convenience overload defaults the port to 8666

- **WHEN** `UseMlx(modelId)` is called without an explicit port
- **THEN** the registered runtime's `MlxRuntimeOptions.Port` is `8666`, distinct from the keyed firstline (8800) and escalation (8810) ports

#### Scenario: Explicit options flow through unchanged

- **WHEN** `UseMlx(options)` is called with a pre-built `MlxRuntimeOptions` carrying an explicit `ModelId` and `Port`
- **THEN** the registered runtime uses that exact model id and port, and the default active model is `mlx/<options.ModelId>`

#### Scenario: Keyed-runtime verbs are unaffected

- **WHEN** a daemon composition root calls `AddMlxFirstline`/`AddMlxEscalation` (with or without also using `UseMlx` in a separate composition)
- **THEN** those runtimes remain keyed router backends and are NOT registered as active-provider candidates
