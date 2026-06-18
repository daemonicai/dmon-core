## ADDED Requirements

### Requirement: ForScope returns only the tools registered for that scope
`AbilityRegistry.ForScope(string scope)` SHALL return an `IList<AITool>` containing exactly the tools whose `IAbilityProvider` declared the given scope label. Scope comparison SHALL be `OrdinalIgnoreCase`. Tools declared under any other scope SHALL never be included.

#### Scenario: Scope returns only its own tools
- **WHEN** `ForScope("personal")` is called
- **THEN** the result contains all tools from providers declaring `"personal"` and no tools from providers declaring any other scope (e.g. `"world"`)

#### Scenario: Scope comparison is case-insensitive
- **WHEN** a provider declares scope `"Personal"` and `ForScope("personal")` is called
- **THEN** that provider's tools are included in the result

#### Scenario: Empty registry returns empty list for any scope
- **WHEN** no `IAbilityProvider` instances are registered and `ForScope` is called with any scope
- **THEN** the result is an empty list

#### Scenario: Unknown scope returns empty list
- **WHEN** providers exist only for `"personal"` and `ForScope("world")` is called
- **THEN** the result is an empty list

---

### Requirement: IAbilityProvider declares a scope label and its tools
`IAbilityProvider` SHALL be a public interface in `Dmon.Abstractions` exposing `string Scope { get; }` and `IEnumerable<AITool> Tools { get; }`. The scope vocabulary is a convention agreed between ability authors and the consuming agent; `Dmon.Abstractions` SHALL define no scope constants.

#### Scenario: Provider declares its scope
- **WHEN** an `IAbilityProvider` with `Scope => "personal"` is registered
- **THEN** its tools appear in `ForScope("personal")` and not in `ForScope("world")`

---

### Requirement: IAbilityProvider registered via DI-discovery
`AddAbilities<T>()` SHALL be an extension method on `IToolRegistration` that registers `T` as an `IAbilityProvider` singleton (`Services.AddSingleton<IAbilityProvider, T>()`). `AbilityRegistry` SHALL discover all registered `IAbilityProvider` instances by build-time DI enumeration (`IEnumerable<IAbilityProvider>`), with no reflection-discovery pass and no post-build manual loop.

#### Scenario: AddAbilities registers provider tools in the registry
- **WHEN** `AddAbilities<MyAbilities>()` is called and the host is built
- **THEN** the tools declared by `MyAbilities` are available via `AbilityRegistry.ForScope` under its declared scope

#### Scenario: Multiple providers are all discovered
- **WHEN** two `IAbilityProvider` implementations declaring the same scope are registered and the host is built
- **THEN** `AbilityRegistry.ForScope` returns tools from both providers for that scope

---

### Requirement: Tools registered via IToolExtension are not in AbilityRegistry
Tools registered via `AddToolExtension<T>()` SHALL appear in the global tool pipeline but SHALL NOT be surfaced by `AbilityRegistry.ForScope`. The two registries are independent.

#### Scenario: IToolExtension tools do not appear in any ability scope manifest
- **WHEN** `AddToolExtension<T>()` is used (not `AddAbilities<T>()`) and no `IAbilityProvider` is registered
- **THEN** `AbilityRegistry.ForScope` returns an empty list for every scope

---

### Requirement: AbilityRegistry is resolvable from DI
`AbilityRegistry` SHALL be registered as a singleton by `AddDmonCore()` so that a terminal client (e.g. a routing `ITerminalClientFactory` output) can resolve it from the service provider. It SHALL be registered whether or not any `IAbilityProvider` is present.

#### Scenario: Registry resolves with no abilities registered
- **WHEN** the host is built with no `AddAbilities<T>()` calls
- **THEN** `AbilityRegistry` is resolvable from DI and `ForScope` returns an empty list for every scope
