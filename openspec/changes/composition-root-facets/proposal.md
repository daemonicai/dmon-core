## Why

The `Dmon.cs` composition root (ADR-019) is the place a user assembles an agent, but its builder is a sealed, closed class in `Dmon.Core`: extension packages cannot contribute fluent verbs, there is no provider verb at all (so ADR-019 orphaned `IProviderExtension`), tools cannot take dependencies, and a sub-agent tool has no way to build a non-active model client. ADR-022 and ADR-023 (both Accepted) turn the composition root into a headline feature — an open, faceted, DI-backed builder where any package ships its own `UseX`/`AddX` verb, providers are composed (not baked), and an agent simply *is* its `.cs`.

## What Changes

- **Registration facets.** New `IProviderRegistration` / `IToolRegistration` / `IMiddlewareRegistration` interfaces, aggregated by a thin `IDmonHostBuilder` over `Services`/`Configuration`. All verbs are extension methods with a self-type generic so flat and faceted chaining both work; verb grammar `Use`/`Add`/`With`/`Append`. **BREAKING**: `WithModel`→`UseModel`, `AddExtension`→`AddToolExtension`; `CreateBuilder()` gains a parameterless overload.
- **DI-discovery.** `AddDmonCore` enumerates `IEnumerable<IToolExtension>` / `IDmonMiddleware` / `IProviderExtension` from the container at build time and routes them into the registries (via the existing `IProviderRegistry.RegisterExtensionAsync`). The builder's post-build manual registry loops are deleted. Closes ADR-019's orphaned `IProviderExtension`.
- **Option-B provider symmetry.** Cloud (`IProviderFactory`) and local (`IProviderExtension`) providers are both composed via `Use<Provider>` verbs; nothing baked.
- **Sub-agent isolation.** A tool takes `Action<IProviderRegistration>`, materialized to a captured `IChatClientFactory` (new contract) isolated from `IProviderRegistry` — structurally enforcing ADR-010 D3. **BREAKING**: reverses ADR-010's "no new contract type".
- **DI-constructed tools.** `AddToolExtension<T>` drops the `new()` constraint, instantiating via `ActivatorUtilities`.
- **Rename** `IDmonExtension` → `IToolExtension` (shape unchanged). **BREAKING** SDK surface.
- **Contracts collapse.** **BREAKING**: `Dmon.Extensions` is deleted; all author-facing contracts move into `Dmon.Abstractions`. SDK base = `Dmon.Protocol` + `Dmon.Abstractions` + `dmoncore`.
- **No persona; system prompt is a plain string.** Resolved from a `systemPrompt` `IConfiguration` value, overridden by `UseSystemPrompt`/`AppendToSystemPrompt`. **BREAKING**: removes the persona/`.md` concept.
- **Agent = its `.cs` composition root.** The profile/definition bundle is dissolved (persona→`UseSystemPrompt`/config, permission mode→`WithPermissionMode`, assets→`UseAssets`); per-session selection picks which `.cs`. **BREAKING**: gateway `createSession` `profile`→`agent`. Removes `IAgentProfileResolver`/`AgentProfile` and the profile-config machinery.
- **Granular implementation packages (ADR-023).** `dmoncore` becomes vendor-SDK-free; each provider/tool/middleware ships as its own package (`Dmon.Providers.<Name>`, `Dmon.Tools.<Name>`, …) carrying its SDK + verb (in the `Dmon.Hosting` namespace); contracts + first-party packages version lockstep on the protocol line; `Dmon.Tools.Builtin` is scaffolded-but-removable. **BREAKING**: `AddDmonProviders` deleted; vendor SDKs leave `Dmon.Core`.

## Capabilities

### New Capabilities
<!-- None — the open builder is an evolution of the existing composition-root-hosting capability. -->

### Modified Capabilities
- `composition-root-hosting`: the open faceted builder — `IDmonHostBuilder` over `Services`/`Configuration`, the three registration facets, the `Use`/`Add`/`With`/`Append` verb grammar with self-type chaining, the DI-discovery substrate; `CreateBuilder()` parameterless overload; an agent is its `.cs` (no `.md`).
- `extension-model`: `IDmonExtension`→`IToolExtension`; DI-constructed tools; `AddToolExtension`; contract moves to `Dmon.Abstractions`.
- `extension-middleware`: `IMiddlewareRegistration`/`AddMiddleware` facet; DI-discovery.
- `provider-extension`: composition entry via `AddProvider`/`Use*`; build-time DI-discovery routing.
- `provider-factories`: Option-B symmetry; granular per-provider packages; vendor-free `dmoncore`.
- `provider-registry`: build-time enumeration/routing of registered provider extensions.
- `system-prompt`: persona removed; plain config string + `UseSystemPrompt`/`AppendToSystemPrompt`.
- `agent-profiles`: **removed** — the profile/persona bundle is dissolved into builder verbs and `.cs` selection.
- `sub-agent-extensions`: `IChatClientFactory` + `Action<IProviderRegistration>` isolation mechanism.
- `package-publishing`: granular implementation packages; `Dmon.Hosting` verb namespace; lockstep protocol-keyed versioning.
- `builtin-tools`: composable `Dmon.Tools.Builtin` package via `AddBuiltinTools`, scaffolded-but-removable.
- `remote-session-gateway`: `createSession` `profile`→`agent` selector.
- `protocol-schema`: the `agent` selector replaces the `profile` field on the session-create command, the `create`/`created` control frames, and persisted `SessionMeta`, reflected in the exported wire schema.

## Impact

- **Affected ADRs:** implements ADR-022 + ADR-023; supersedes ADR-013 (full) and ADR-020 (persona/bundle half); amends ADR-019/002/010/006/021/012/011.
- **Affected packages:** `Dmon.Core` (engine, vendor-free), `Dmon.Abstractions` (absorbs all author contracts), `Dmon.Extensions` (deleted), new `Dmon.Providers.<Name>` / `Dmon.Tools.Builtin` packages, `Dmon.Terminal`/gateway (`profile`→`agent`), sample extension, `default-core/Dmon.cs` + `samples/Dmon.ComposedCore/Dmon.cs`.
- **Affected code:** the hosting builder, DI registration extensions, provider registry/factories, tool & middleware registries, the profile subsystem (deleted), system-prompt builder, sub-agent path, RPC `createSession` contract.
- **Scale:** large, cross-cutting, breaking. No back-compat required (no production deployments). Tasks must be grouped so each group builds and tests green independently.
