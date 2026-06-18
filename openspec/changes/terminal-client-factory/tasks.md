## 1. Contracts in Dmon.Abstractions

- [x] 1.1 Define `interface ITerminalClientFactory` with `IChatClient Create(IServiceProvider services)` in `core/Dmon.Abstractions` — the hook `DmonHostBuilder.Build()` checks before falling back to the provider-registry active provider
- [x] 1.2 Define `interface IAbilityProvider` with `string Scope { get; }` and `IEnumerable<AITool> Tools { get; }` in `core/Dmon.Abstractions` — the DI-discovery contract for ability registration; no scope constants are defined in `Dmon.Abstractions`

## 2. AbilityRegistry

- [ ] 2.1 Implement `sealed class AbilityRegistry` in `core/Dmon.Core` that accepts `IEnumerable<IAbilityProvider>` via constructor injection
- [ ] 2.2 Implement `IList<AITool> ForScope(string scope)` — returns all tools from providers whose `Scope` matches `scope` using `OrdinalIgnoreCase`; never returns tools from another scope; built per-call (not cached)
- [ ] 2.3 Register `AbilityRegistry` as a singleton in `AddDmonCore()` so it is resolvable from DI whether or not any `IAbilityProvider` is registered
- [ ] 2.4 Implement `AddAbilities<T>()` extension on `IToolRegistration` (self-typed `where T2 : IToolRegistration` per the verb grammar) that registers `T` as an `IAbilityProvider` singleton via `Services.AddSingleton<IAbilityProvider, T>()` — same pattern as `AddToolExtension<T>()`

## 3. Build() hook

- [ ] 3.1 Add `ITerminalClientFactory` resolution to `DmonHostBuilder.Build()`: after provider/middleware DI-discovery, check `host.Services.GetService<ITerminalClientFactory>()`; if present, call `Create(host.Services)` for the terminal `IChatClient`; otherwise use the existing provider-registry path — do not change the fallback behaviour
- [ ] 3.2 Confirm the terminal client produced by the factory flows through the rest of `Build()` (permission gate, retry, etc.) identically to a provider-registry client, or document precisely where it diverges and why

## 4. Tests

- [ ] 4.1 Test `AbilityRegistry.ForScope`: a scope returns only its own tools; case-insensitive match; empty registry returns empty list for any scope; unknown scope returns empty list; multiple providers of the same scope are all discovered; `IToolExtension`-registered tools do NOT appear
- [ ] 4.2 Test the `Build()` hook: a builder with an `ITerminalClientFactory` registered produces that factory's output as the terminal client; a builder without one uses the provider-registry active provider as before
- [ ] 4.3 Confirm `make build` and `make test` are clean; `openspec validate terminal-client-factory --strict` passes
