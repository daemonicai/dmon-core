## ADDED Requirements

### Requirement: Dmon.Extensions exports IDmonMiddleware and DmonMiddlewareAttribute
The `Dmon.Extensions` assembly SHALL export `IDmonMiddleware` and `DmonMiddlewareAttribute` as public types. These SHALL be in the same root namespace as `IDmonExtension` and `IDmonAttribute`. No existing public types SHALL be removed or renamed by this change.

#### Scenario: Extension package references only Dmon.Extensions
- **WHEN** an extension NuGet package references only `Dmon.Extensions`
- **THEN** it has access to both `IDmonExtension` and `IDmonMiddleware` without additional references

#### Scenario: Existing extensions remain binary-compatible
- **WHEN** an extension compiled against the previous `Dmon.Extensions` (without middleware types) is loaded
- **THEN** it loads without error and its tools are available

### Requirement: Extension loader performs middleware discovery pass
The extension loader SHALL perform a middleware discovery pass after the existing tool discovery pass. The two passes are independent: a single extension assembly may expose both tools and middleware. Results of both passes are merged before the pipeline is constructed.

#### Scenario: Assembly with both tools and middleware contributes both
- **WHEN** an extension assembly contains an `IDmonExtension` tool implementation and an `IDmonMiddleware` implementation
- **THEN** the loader registers the tools and adds the middleware to the pipeline

#### Scenario: Tool-only assembly is unaffected by middleware pass
- **WHEN** an extension assembly contains only `IDmonExtension` implementations
- **THEN** the middleware discovery pass finds nothing and produces no error
