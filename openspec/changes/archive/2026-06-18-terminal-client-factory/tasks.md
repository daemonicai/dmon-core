## 1. Contracts in Dmon.Abstractions

- [x] 1.1 Define `interface ITerminalClientFactory` with `IChatClient Create(IServiceProvider services)` in `core/Dmon.Abstractions` — the hook `DmonHostBuilder.Build()` checks before falling back to the provider-registry active provider
- [x] 1.2 Define `interface IAbilityProvider` with `string Scope { get; }` and `IEnumerable<AITool> Tools { get; }` in `core/Dmon.Abstractions` — the DI-discovery contract for ability registration; no scope constants are defined in `Dmon.Abstractions`

## 2. AbilityRegistry

- [x] 2.1 Implement `sealed class AbilityRegistry` in `core/Dmon.Core` that accepts `IEnumerable<IAbilityProvider>` via constructor injection
- [x] 2.2 Implement `IList<AITool> ForScope(string scope)` — returns all tools from providers whose `Scope` matches `scope` using `OrdinalIgnoreCase`; never returns tools from another scope; built per-call (not cached)
- [x] 2.3 Register `AbilityRegistry` as a singleton in `AddDmonCore()` so it is resolvable from DI whether or not any `IAbilityProvider` is registered
- [x] 2.4 Implement `AddAbilities<T>()` extension on `IToolRegistration` (self-typed `where T2 : IToolRegistration` per the verb grammar) that registers `T` as an `IAbilityProvider` singleton via `Services.AddSingleton<IAbilityProvider, T>()` — same pattern as `AddToolExtension<T>()`

## 3. Terminal-client materialization hook

- [x] 3.1 At the terminal-client materialization point (`TurnHandler.RunTurnAsync`, where `_providers.GetCurrentAsync(...)` supplies the base client), resolve an optional `ITerminalClientFactory` from DI; if present, use `factory.Create(services)` as the base terminal `IChatClient` instead of the provider-registry active provider; if absent, use the existing provider-registry path **byte-for-byte unchanged**. (`DmonHostBuilder.Build()` does not construct the terminal client, so the hook lives here, not in `Build()`.)
- [x] 3.2 Confirm the factory's output flows through the existing wrappers (middleware fold, retry, function-invocation, permission gate) identically to a provider-registry client; keep the no-factory path unchanged. Note the provider-switch interaction (a factory-supplied router is not a switchable provider) as a Phase-3/daemon-app concern — do not attempt to reconcile switching here.

## 4. Tests

- [x] 4.1 Test `AbilityRegistry.ForScope`: a scope returns only its own tools; case-insensitive match; empty registry returns empty list for any scope; unknown scope returns empty list; multiple providers of the same scope are all discovered; `IToolExtension`-registered tools do NOT appear
- [x] 4.2 Test the `Build()` hook: a builder with an `ITerminalClientFactory` registered produces that factory's output as the terminal client; a builder without one uses the provider-registry active provider as before
- [x] 4.3 Confirm `make build` and `make test` are clean; `openspec validate terminal-client-factory --strict` passes
