# composition-root-hosting Specification

## MODIFIED Requirements

### Requirement: `dmoncore` exposes a hosting surface, not an entry point
`dmoncore` SHALL be a library exposing `DmonHost.CreateBuilder(args)` **and a parameterless `DmonHost.CreateBuilder()` overload (args default to `[]`)**, each returning an `IDmonHostBuilder` that configures the provider/model, tools, middleware, permission mode, and system prompt, and whose `.Build().RunAsync(cancellationToken)` runs the JSONL/stdio core loop. The wire contract (ADR-003), session storage (ADR-004), and the permission pipeline (ADR-002/006) reached through `RunAsync()` SHALL be unchanged from the prior in-package entry point — only the entry point lives in the composition root, not the library.

`IDmonHostBuilder` SHALL be a thin aggregate over `IServiceCollection Services` and `IConfigurationManager Configuration`, declared in `Dmon.Abstractions`, and SHALL implement the three registration facets `IProviderRegistration`, `IToolRegistration`, and `IMiddlewareRegistration` (all declared in `Dmon.Abstractions`):

```csharp
public interface IDmonHostBuilder
    : IProviderRegistration, IToolRegistration, IMiddlewareRegistration
{
    IServiceCollection Services { get; }
    IConfigurationManager Configuration { get; }
}
```

The concrete `DmonHostBuilder` and `DmonHost` SHALL stay in `Dmon.Core`. Host-level, non-pluggable verbs (e.g. `WithStdio`, `WithoutTelemetry`, `WithPermissionMode`, `UseSystemPrompt`, `AppendToSystemPrompt`, `UseAssets`, `UseMemory<TShort,TLong>`) SHALL be extension methods on `IDmonHostBuilder`.

#### Scenario: Hosting surface runs the core loop
- **WHEN** a program calls `await DmonHost.CreateBuilder(args).Build().RunAsync(ct)`
- **THEN** the process serves the same JSONL/stdio protocol (including `agentReady`) a stock core served before, over the same wire contract

#### Scenario: Parameterless builder overload
- **WHEN** a program calls `DmonHost.CreateBuilder()` with no arguments
- **THEN** a builder is returned with `args` defaulting to `[]`, behaving as if `CreateBuilder([])` had been called

#### Scenario: Builder is a thin facet aggregate
- **WHEN** the `IDmonHostBuilder` surface is inspected
- **THEN** it exposes `IServiceCollection Services` and `IConfigurationManager Configuration` and implements `IProviderRegistration`, `IToolRegistration`, and `IMiddlewareRegistration`, all declared in `Dmon.Abstractions`

#### Scenario: Library carries no entry point
- **WHEN** the `dmoncore` library package is inspected
- **THEN** it exposes the `DmonHost` hosting API and contains no `Main`/top-level-statement entry point of its own

## ADDED Requirements

### Requirement: Registration is split into role-specific facet interfaces
Registration SHALL be split into three role-specific facet interfaces — `IProviderRegistration`, `IToolRegistration`, and `IMiddlewareRegistration` — each declared in `Dmon.Abstractions`. `IProviderRegistration` SHALL carry provider verbs (`UseOpenAI`, `UseLlamaCpp`, `UseModel`, …); `IToolRegistration` SHALL carry tool verbs (`AddToolExtension`, `AddAgentWebSearch`, …); `IMiddlewareRegistration` SHALL carry middleware verbs (`AddMiddleware`, …). A provider, tool, or middleware package SHALL extend only the facet relevant to its kind; every author-facing contract SHALL live in the single `Dmon.Abstractions` package (the `Dmon.Extensions` package is deleted), so an extension author references exactly one contract package regardless of kind.

#### Scenario: Provider verb extends only the provider facet
- **WHEN** a provider package ships `UseLlamaCpp` as an extension method constrained `where T : IProviderRegistration`
- **THEN** the verb is callable on any `IProviderRegistration` (including the builder) and is not available on a bare `IToolRegistration` or `IMiddlewareRegistration`

#### Scenario: One contract package for all author kinds
- **WHEN** an extension author writes a provider, tool, or middleware package
- **THEN** they reference only `Dmon.Abstractions` for the facet and contract types, with no separate `Dmon.Extensions` package

### Requirement: All builder verbs are self-typed extension methods
Every builder verb SHALL be an extension method over a facet with a self-type generic of the form `static T Verb<T>(this T r, …) where T : IFacet`, so that a verb called on the concrete builder (which implements all three facets) returns the builder type and chaining continues across facets (flat chaining), while the same verb called on a bare facet returns that facet type (faceted reuse). Under the covers a verb SHALL only call `Services.Add…` / `Configuration[…]`. There SHALL be no "members vs. extension methods" distinction — all verbs are extension methods.

A canonical verb grammar SHALL be binding for blessed and third-party verbs alike: `Use<X>` replaces a single strategy (provider, model, system prompt, memory); `Add<X>` appends to a collection (tool extension, middleware); `With<X>`/`Append<X>` set a scalar or mutate a composed value. Blessed verbs SHALL ship in the `Dmon.Hosting` namespace (already imported by an authored `Dmon.cs` via `using Dmon.Hosting;`), so a blessed verb appears after a `#:package` line with no extra `using`; a third-party verb is one `using` away.

#### Scenario: Flat chaining across facets on the builder
- **WHEN** `Dmon.cs` writes `await DmonHost.CreateBuilder().UseOpenAI("gpt5.5").AddAgentWebSearch("gemini-3.5-flash-lite").Build().RunAsync()`
- **THEN** each verb returns the builder type so a provider verb and a tool verb chain together in one flat expression

#### Scenario: Faceted reuse on a bare facet
- **WHEN** a provider verb is called on a bare `IProviderRegistration` (e.g. inside a sub-agent's `Action<IProviderRegistration>`)
- **THEN** the verb returns that `IProviderRegistration` and continues chaining on the facet

#### Scenario: Blessed verb is visible after #:package
- **WHEN** `Dmon.cs` adds `#:package Dmon.Providers.OpenAI@<protocol>.*` and already has `using Dmon.Hosting;`
- **THEN** the `UseOpenAI` verb is visible with no additional `using` directive

### Requirement: Registration moves onto build-time DI-discovery
Registration of tools, middleware, and providers SHALL be performed by DI enumeration at `Build()` rather than by post-build manual registry loops. `AddDmonCore` (and the provider/extension registration helpers) SHALL enumerate `IEnumerable<IToolExtension>`, `IEnumerable<IDmonMiddleware>`, and `IEnumerable<IProviderExtension>` from the container at build time and route each into `IToolRegistry`, `IMiddlewareRegistry`, and `IProviderRegistry` (via the existing `IProviderRegistry.RegisterExtensionAsync`, gated by `IsApplicable`). `AddToolExtension<T>`, `AddMiddleware<T>`, and `AddProvider<T>` SHALL each be a thin `Services.AddSingleton<IFacetContract, T>()` call. The builder's post-build manual registry loops SHALL be removed.

#### Scenario: A registered tool is discovered via DI at build
- **WHEN** `Dmon.cs` calls `.AddToolExtension<FooTool>()` and then `.Build()`
- **THEN** `FooTool` is registered as a singleton `IToolExtension` and routed into `IToolRegistry` by build-time enumeration, with no post-build manual loop

#### Scenario: A registered provider extension is routed at build
- **WHEN** a provider verb registers an `IProviderExtension` singleton and the host is built
- **THEN** the extension is enumerated from the container and routed into `IProviderRegistry` via `RegisterExtensionAsync`, gated by `IsApplicable`

### Requirement: An agent is its `.cs` composition root
An agent SHALL be its `.cs` composition root; there SHALL be no profile, persona, or `.md` definition. The **default agent** SHALL be the root `Dmon.cs`. **Named agents** SHALL be `.dmon/agents/<name>.cs` composition roots. Per-session selection SHALL be preserved: a session selects an agent *name*, and the launcher (`ICoreLauncher`) SHALL build and run that `.cs`; a gateway session SHALL resolve the name under its configured workspace root, never a client-supplied path. Selecting a different agent runs a different composition root (single-core; these are not RPC-peer processes). Every capability the former profile/definition bundle carried SHALL become a builder verb in the `.cs` (system prompt → `UseSystemPrompt`/config; permission mode → `WithPermissionMode`; session assets → `UseAssets`). The `WithProfile` verb and the `IAgentProfileResolver`/profile-config machinery SHALL be removed.

#### Scenario: Default agent is the root Dmon.cs
- **WHEN** a session is created with no agent name
- **THEN** the launcher builds and runs the root `Dmon.cs`

#### Scenario: Named agent resolves to a .cs under the agents directory
- **WHEN** a session selects agent name `reviewer`
- **THEN** the launcher builds and runs `.dmon/agents/reviewer.cs`, with no accompanying `.md` persona

#### Scenario: Gateway resolves the agent name under the workspace root
- **WHEN** a gateway session selects an agent name
- **THEN** the name is resolved to a `.cs` under the gateway's configured workspace root, never a client-supplied path

## REMOVED Requirements

### Requirement: `config.yaml` is a settings source the composition may override
**Reason**: The `config.yaml`-versus-extension-list distinction is subsumed by ADR-022's plain-string-from-`IConfiguration` model and the agent-is-its-`.cs` decision (D11/D14). System prompt and other settings resolve from `IConfiguration` (config < `UseSystemPrompt`/code), and composition is entirely the `.cs` — there is no longer an "extension list" the requirement existed to fence off. The retained behaviour (config declares, C# overrides) is captured by the system-prompt resolution in the `system-prompt` capability and the verb model above.
**Migration**: No data migration. `config.yaml` remains a settings source exposed via `IConfigurationManager Configuration` on the builder; any composition (provider, tool, middleware, system prompt) that was config-ish moves into the `.cs` via verbs or is read from `IConfiguration` by the verb implementation.

### Requirement: `Dmon.cs` is the composition root and extensions are compile-time
**Reason**: Superseded by the new "An agent is its `.cs` composition root" requirement and the faceted/DI-discovery requirements above. The compile-time-`#:package`, single-identity-graph, and Default-`AssemblyLoadContext` guarantees are retained by ADR-019 (unchanged by this change) and the granular-package topology (ADR-023); the specific `AddExtension<FooExtension>()`/`IDmonExtension` wording is now wrong (the verb is `AddToolExtension`, the contract is `IToolExtension`).
**Migration**: Replace `AddExtension<T>()` with `AddToolExtension<T>()` and `IDmonExtension` with `IToolExtension` in any `Dmon.cs`. The compile-time composition model (a `#:package`/`#:project`/`#:ref` plus a builder registration, one identity graph, restart-to-reclaim) is unchanged.
