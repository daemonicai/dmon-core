## 1. NuGet/Assembly Loader — Default Context

- [x] 1.1 Remove the `_activeContext` field and all `Unload()` calls from `NuGetExtensionLoader`; drop its `IDisposable` ALC teardown
- [x] 1.2 Load extension assemblies into `AssemblyLoadContext.Default` (replace `new AssemblyLoadContext(...).LoadFromAssemblyPath`)
- [x] 1.3 Verify reflection discovery still finds `IDmonExtension`/`IProviderExtension` types with shared contract-type identity
- [x] 1.4 Verify loading a second extension leaves the first extension's tools registered and callable

## 2. Transitive Dependency Resolution

- [ ] 2.1 Build an `AssemblyDependencyResolver` from each loaded extension assembly path
- [ ] 2.2 Register a `AssemblyLoadContext.Default.Resolving` handler that consults the resolver, falling back to probing the extension assembly's directory
- [ ] 2.3 De-duplicate resolvers by extension path so repeated loads do not accumulate handlers
- [ ] 2.4 Verify an extension with a sibling dependency and one with a `.deps.json`-described dependency both load

## 3. Script Loader Cleanup

- [ ] 3.1 Remove the `ScriptAssemblyLoadContext` field, its assignment, and the `Dispose` unload from `CsxScriptLoader`
- [ ] 3.2 Verify `.csx` script loading is unchanged (script returning `AIFunction`(s) still registers tools)

## 4. Unload Semantics

- [ ] 4.1 Clarify `ExtensionService.Unload` doc comment: deregister-from-registry only; assemblies resident until restart
- [ ] 4.2 Update the `extension.unload` protocol/UX wording (and terminal rendering note) to state code is not reclaimed until restart
- [ ] 4.3 Verify unloaded tools are no longer offered and re-loading the same assembly in-process succeeds

## 5. Tests

- [ ] 5.1 Unit test: loader loads a local assembly into `AssemblyLoadContext.Default` and creates no collectible context
- [ ] 5.2 Unit test: loading extension B does not unload or disturb extension A's registered tools
- [ ] 5.3 Unit test: extension with a sibling/`.deps.json` dependency resolves and loads
- [ ] 5.4 Unit test: `Unload` removes tools and emits `ExtensionUnloadedEvent` without reclaiming the assembly
- [ ] 5.5 Unit test: `CsxScriptLoader` holds no `AssemblyLoadContext` reference after loading a script

## 6. Docs & ADR Cross-Reference

- [ ] 6.1 Note Default-context loading and the deregister-only unload semantics in the config/usage docs; reference ADR-008
- [ ] 6.2 Confirm ADR-008 is `Accepted` and ADR-002's loading-mechanism supersession pointer is in place
