## ADDED Requirements

### Requirement: IDmonMiddleware declares Wrap method
`IDmonMiddleware` SHALL be a public interface in `Dmon.Extensions` declaring a single method `IChatClient Wrap(IChatClient inner)`. Implementations return an `IChatClient` that wraps `inner`, intercepting requests and/or responses.

#### Scenario: Middleware wraps inner client
- **WHEN** `middleware.Wrap(innerClient)` is called
- **THEN** the returned `IChatClient` delegates to `innerClient` and may mutate the messages list before forwarding and/or the response before returning

#### Scenario: Null inner client is rejected
- **WHEN** `middleware.Wrap(null)` is called
- **THEN** an `ArgumentNullException` is thrown

### Requirement: DmonMiddlewareAttribute marks and configures middleware
`DmonMiddlewareAttribute` SHALL be a public sealed attribute in `Dmon.Extensions`, applicable to classes. It SHALL expose an `int Priority` property (default `0`). Only classes annotated with `[DmonMiddleware]` AND implementing `IDmonMiddleware` are loaded by the extension loader.

#### Scenario: Annotated class is discovered
- **WHEN** an extension assembly containing a class annotated with `[DmonMiddleware]` and implementing `IDmonMiddleware` is loaded
- **THEN** the loader instantiates that class and includes it in the middleware pipeline

#### Scenario: Unannotated IDmonMiddleware is ignored
- **WHEN** an extension assembly contains a class implementing `IDmonMiddleware` but NOT annotated with `[DmonMiddleware]`
- **THEN** the loader does not instantiate it and does not include it in the pipeline

### Requirement: Extension loader discovers and instantiates middleware
The extension loader SHALL scan loaded assemblies for types that both implement `IDmonMiddleware` and carry `[DmonMiddleware]`. For each discovered type it SHALL attempt to instantiate it, passing the host `IServiceProvider` as a constructor argument if the type has a matching constructor overload; otherwise using a no-arg constructor.

#### Scenario: Middleware with IServiceProvider constructor receives host services
- **WHEN** a middleware type has a constructor accepting `IServiceProvider`
- **THEN** the loader passes the host `IServiceProvider` to that constructor

#### Scenario: Middleware without IServiceProvider constructor uses no-arg constructor
- **WHEN** a middleware type has only a parameterless constructor
- **THEN** the loader calls the parameterless constructor

#### Scenario: Middleware constructor throws — startup continues
- **WHEN** a middleware constructor throws an exception during instantiation
- **THEN** the loader logs the error, skips that middleware, and continues loading remaining extensions

### Requirement: Pipeline is built by folding middlewares in priority order
At agent startup, after all extensions are loaded, the host SHALL sort discovered `IDmonMiddleware` instances by effective priority (ascending), then fold them over the base provider `IChatClient`: `middlewares.OrderBy(m => m.EffectivePriority).Aggregate(baseClient, (inner, m) => m.Wrap(inner))`. The resulting client is used for all turns in the session.

#### Scenario: Lower priority middleware is innermost
- **WHEN** middleware A has priority 100 and middleware B has priority 200
- **THEN** the pipeline is `B.Wrap(A.Wrap(baseClient))` — A is closer to the provider, B is closer to the caller

#### Scenario: Equal priority middlewares use stable registration order as tiebreaker
- **WHEN** two middlewares have the same effective priority
- **THEN** their relative order in the pipeline matches their order of registration

#### Scenario: No middleware leaves pipeline unchanged
- **WHEN** no middleware extensions are loaded
- **THEN** the pipeline is the bare base provider `IChatClient`

### Requirement: Middleware configuration via named YAML sections
The config file SHALL support a top-level `middleware` section containing per-middleware named subsections. Each subsection key is the middleware's class name (case-insensitive). Subsections may contain arbitrary fields consumed by the middleware, plus an optional `priority` field (int) that overrides the `[DmonMiddleware]` attribute value.

#### Scenario: Priority override in config takes precedence over attribute
- **WHEN** a middleware has `[DmonMiddleware(Priority = 100)]` and its config section contains `priority: 50`
- **THEN** the middleware's effective priority is `50`

#### Scenario: Arbitrary config fields are accessible via IConfigurationRoot
- **WHEN** a middleware's config section contains `maxTokens: 4096`
- **THEN** `configurationRoot.GetSection("middleware:<ClassName>")["maxTokens"]` returns `"4096"`

#### Scenario: Absent config section uses attribute priority
- **WHEN** a middleware has no corresponding config section
- **THEN** the middleware's effective priority equals the `Priority` property on its `[DmonMiddleware]` attribute

### Requirement: Middleware does not support hot-reload
`IDmonMiddleware` implementations SHALL NOT be reloaded while the agent process is running. File-system change events for middleware assemblies SHALL be ignored. A process restart is required to apply middleware changes.

#### Scenario: Middleware assembly change during session is ignored
- **WHEN** a middleware extension file is modified while the agent is running
- **THEN** the running pipeline is unchanged and no reload occurs
