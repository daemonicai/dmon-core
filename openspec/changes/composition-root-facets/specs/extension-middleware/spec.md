# extension-middleware Specification

## MODIFIED Requirements

### Requirement: IDmonMiddleware declares Wrap method
`IDmonMiddleware` SHALL be a public interface in `Dmon.Abstractions` declaring a single method `IChatClient Wrap(IChatClient inner)`. Implementations return an `IChatClient` that wraps `inner`, intercepting requests and/or responses.

#### Scenario: Middleware wraps inner client
- **WHEN** `middleware.Wrap(innerClient)` is called
- **THEN** the returned `IChatClient` delegates to `innerClient` and may mutate the messages list before forwarding and/or the response before returning

#### Scenario: Null inner client is rejected
- **WHEN** `middleware.Wrap(null)` is called
- **THEN** an `ArgumentNullException` is thrown

### Requirement: DmonMiddlewareAttribute marks and configures middleware
`DmonMiddlewareAttribute` SHALL be a public sealed attribute in `Dmon.Abstractions`, applicable to classes. It SHALL expose an `int Priority` property (default `0`). The attribute is a render/priority marker carried by the middleware type; it is not the gate that discovers middleware — middleware enters the pipeline only by an explicit `IMiddlewareRegistration` registration (see "Middleware is registered through the IMiddlewareRegistration facet").

#### Scenario: Attribute supplies the default priority
- **WHEN** a middleware annotated `[DmonMiddleware(Priority = 100)]` is registered via the facet and no priority override is supplied at registration
- **THEN** its effective priority is `100`

#### Scenario: Unregistered IDmonMiddleware is not in the pipeline
- **WHEN** a class implements `IDmonMiddleware` (annotated or not) but is never registered via the facet
- **THEN** it is not instantiated and not included in the pipeline

### Requirement: Pipeline is built by folding middlewares in priority order
At agent startup, the host SHALL discover the registered `IDmonMiddleware` instances by build-time DI enumeration (`IEnumerable<IDmonMiddleware>` from the container), route them into `IMiddlewareRegistry`, sort them by effective priority (ascending), then fold them over the base provider `IChatClient`: `middlewares.OrderBy(m => m.EffectivePriority).Aggregate(baseClient, (inner, m) => m.Wrap(inner))`. The resulting client is used for all turns in the session. There SHALL be no reflection-discovery pass and no post-build manual registration loop; the set of middlewares is exactly what the `IMiddlewareRegistration` facet registered, discovered via DI.

#### Scenario: Lower priority middleware is innermost
- **WHEN** middleware A is registered with priority 100 and middleware B with priority 200
- **THEN** the pipeline is `B.Wrap(A.Wrap(baseClient))` — A is closer to the provider, B is closer to the caller

#### Scenario: Equal priority middlewares use stable registration order as tiebreaker
- **WHEN** two middlewares have the same effective priority
- **THEN** their relative order in the pipeline matches their order of registration on the facet

#### Scenario: No registered middleware leaves pipeline unchanged
- **WHEN** no middleware is registered via the facet
- **THEN** the pipeline is the bare base provider `IChatClient`

#### Scenario: Middleware is routed via DI enumeration, not a post-build loop
- **WHEN** middleware is registered with `AddMiddleware<T>()` and the host is built
- **THEN** the registered `IDmonMiddleware` instances are enumerated from the container and routed into `IMiddlewareRegistry` at build time, with no post-build manual registration loop

### Requirement: Middleware is registered through the IMiddlewareRegistration facet
Middleware SHALL be contributed to the pipeline by an `IMiddlewareRegistration` facet verb in the composition root (`Dmon.cs`), not by reflection discovery. The facet SHALL expose `AddMiddleware<TMiddleware>(int? priority = null)` (by type) and an instance overload (by instance), where an explicit priority overrides the type's `[DmonMiddleware]` attribute value. `AddMiddleware<T>` SHALL be a thin `Services.AddSingleton<IDmonMiddleware, T>()` call so the middleware is discovered by build-time DI-enumeration. Registration is compile-time composition: the middleware type is a compile-time dependency of `Dmon.cs` (a `#:package`/`#:project`/`#:ref`), consistent with the extension model. A registered middleware MAY read its own settings from the host `IConfiguration` (settings, not composition); there is no dedicated config-driven middleware activation or priority section.

#### Scenario: Facet registration adds middleware to the pipeline
- **WHEN** `Dmon.cs` calls `builder.AddMiddleware<LoggingMiddleware>()` and builds the host
- **THEN** a `LoggingMiddleware` instance is registered as a singleton `IDmonMiddleware`, discovered via DI, and folded into the pipeline at its effective priority, with no runtime load or reflection scan

#### Scenario: Registration priority overrides the attribute
- **WHEN** a middleware annotated `[DmonMiddleware(Priority = 100)]` is registered with an explicit priority of `50`
- **THEN** its effective priority is `50`

#### Scenario: Middleware reads its own settings from configuration
- **WHEN** a registered middleware reads `configuration.GetSection("middleware:LoggingMiddleware")["maxTokens"]`
- **THEN** it observes whatever `config.yaml` provides for that section as plain settings, and the value does not affect whether or at what priority the middleware is in the pipeline

### Requirement: Middleware does not support hot-reload
`IDmonMiddleware` implementations SHALL NOT be reloaded while the agent process is running. File-system change events for middleware assemblies SHALL be ignored. A process restart is required to apply middleware changes.

#### Scenario: Middleware assembly change during session is ignored
- **WHEN** a middleware extension file is modified while the agent is running
- **THEN** the running pipeline is unchanged and no reload occurs
