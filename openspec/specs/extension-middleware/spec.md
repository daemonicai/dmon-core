# extension-middleware Specification

## Purpose
TBD - created by archiving change extension-middleware-tier. Update Purpose after archive.
## Requirements
### Requirement: IDmonMiddleware declares Wrap method
`IDmonMiddleware` SHALL be a public interface in `Dmon.Extensions` declaring a single method `IChatClient Wrap(IChatClient inner)`. Implementations return an `IChatClient` that wraps `inner`, intercepting requests and/or responses.

#### Scenario: Middleware wraps inner client
- **WHEN** `middleware.Wrap(innerClient)` is called
- **THEN** the returned `IChatClient` delegates to `innerClient` and may mutate the messages list before forwarding and/or the response before returning

#### Scenario: Null inner client is rejected
- **WHEN** `middleware.Wrap(null)` is called
- **THEN** an `ArgumentNullException` is thrown

### Requirement: DmonMiddlewareAttribute marks and configures middleware
`DmonMiddlewareAttribute` SHALL be a public sealed attribute in `Dmon.Extensions`, applicable to classes. It SHALL expose an `int Priority` property (default `0`). The attribute is a render/priority marker carried by the middleware type; it is no longer the gate the extension loader uses to *discover* middleware — middleware enters the pipeline only by an explicit `DmonHost` builder registration (see "Middleware is registered through the DmonHost builder").

#### Scenario: Attribute supplies the default priority
- **WHEN** a middleware annotated `[DmonMiddleware(Priority = 100)]` is registered with the builder and no priority override is supplied at registration
- **THEN** its effective priority is `100`

#### Scenario: Unregistered IDmonMiddleware is not in the pipeline
- **WHEN** a class implements `IDmonMiddleware` (annotated or not) but is never registered with the builder
- **THEN** it is not instantiated and not included in the pipeline

### Requirement: Pipeline is built by folding middlewares in priority order
At agent startup, after the composition is built, the host SHALL sort the **builder-registered** `IDmonMiddleware` instances by effective priority (ascending), then fold them over the base provider `IChatClient`: `middlewares.OrderBy(m => m.EffectivePriority).Aggregate(baseClient, (inner, m) => m.Wrap(inner))`. The resulting client is used for all turns in the session. There is no reflection-discovery pass; the set of middlewares is exactly what the builder registered.

#### Scenario: Lower priority middleware is innermost
- **WHEN** middleware A is registered with priority 100 and middleware B with priority 200
- **THEN** the pipeline is `B.Wrap(A.Wrap(baseClient))` — A is closer to the provider, B is closer to the caller

#### Scenario: Equal priority middlewares use stable registration order as tiebreaker
- **WHEN** two middlewares have the same effective priority
- **THEN** their relative order in the pipeline matches their order of registration on the builder

#### Scenario: No registered middleware leaves pipeline unchanged
- **WHEN** no middleware is registered on the builder
- **THEN** the pipeline is the bare base provider `IChatClient`

### Requirement: Middleware is registered through the DmonHost builder
Middleware SHALL be contributed to the pipeline by an explicit `DmonHost` builder call in the composition root (`Dmon.cs`), not by reflection discovery. The builder SHALL expose a registration surface that accepts an `IDmonMiddleware` (by type, e.g. `AddMiddleware<TMiddleware>()`, and/or by instance) with an optional priority that overrides the type's `[DmonMiddleware]` attribute value. Registration is compile-time composition: the middleware type is a compile-time dependency of `Dmon.cs` (a `#:package`/`#:project`/`#:ref`), consistent with the extension model. A registered middleware MAY read its own settings from the host `IConfiguration` (settings, not composition); there is no dedicated config-driven middleware activation or priority section.

#### Scenario: Builder registration adds middleware to the pipeline
- **WHEN** `Dmon.cs` calls `builder.AddMiddleware<LoggingMiddleware>()` and builds the host
- **THEN** a `LoggingMiddleware` instance is folded into the pipeline at its effective priority, with no runtime load or reflection scan

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

