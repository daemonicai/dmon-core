## 1. Contract Assembly — New Types

- [x] 1.1 Add `IDmonMiddleware` interface to `Dmon.Extensions` with `IChatClient Wrap(IChatClient inner)` method
- [x] 1.2 Add `DmonMiddlewareAttribute` sealed class to `Dmon.Extensions` with `int Priority { get; init; }` property (default `0`)
- [x] 1.3 Verify `Dmon.Extensions` builds without warnings and existing `IDmonExtension` types are unaffected

## 2. Extension Loader — Middleware Discovery

- [ ] 2.1 Add middleware discovery pass to the extension loader: reflect over loaded types for `IDmonMiddleware` + `[DmonMiddleware]`
- [ ] 2.2 Implement two-overload instantiation: try `(IServiceProvider)` constructor first, fall back to parameterless
- [ ] 2.3 Catch and log construction exceptions per failing middleware; continue loading remaining extensions
- [ ] 2.4 Verify tool-only assemblies pass the middleware discovery pass with no errors

## 3. Pipeline Construction

- [ ] 3.1 Add `EffectivePriority` resolution: read `middleware:<ClassName>:priority` from `IConfigurationRoot`; fall back to attribute value
- [ ] 3.2 Implement pipeline fold: `middlewares.OrderBy(m => m.EffectivePriority).Aggregate(baseClient, (inner, m) => m.Wrap(inner))`
- [ ] 3.3 Wire constructed pipeline into the agent's turn loop (replace bare provider client reference)
- [ ] 3.4 Verify no-middleware case produces the bare provider client unchanged

## 4. Configuration Support

- [ ] 4.1 Add `middleware` top-level section to the config YAML schema (arbitrary per-middleware subsections, optional `priority` field)
- [ ] 4.2 Ensure `IConfigurationRoot` is registered in the host `IServiceProvider` so middleware can call `GetRequiredService<IConfigurationRoot>()`
- [ ] 4.3 Document the config schema in `docs/config.md` (or equivalent) with a worked example

## 5. Hot-Reload Guard

- [ ] 5.1 Confirm the file-system watcher (if present) does not trigger reloads for middleware assemblies; add explicit exclusion or documentation if needed

## 6. Tests

- [ ] 6.1 Unit test: `DmonMiddlewareAttribute` default and custom priority
- [ ] 6.2 Unit test: loader discovers annotated middleware, ignores unannotated `IDmonMiddleware`
- [ ] 6.3 Unit test: loader skips faulting middleware constructor, logs error, continues
- [ ] 6.4 Unit test: pipeline fold order — lower priority is innermost
- [ ] 6.5 Unit test: config `priority` override takes precedence over attribute value
- [ ] 6.6 Integration test: extension assembly with both tools and middleware — both are loaded
