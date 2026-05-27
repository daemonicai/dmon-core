## ADDED Requirements

### Requirement: Extensions load into the Default AssemblyLoadContext
The extension loader SHALL load NuGet and local-assembly extensions into `AssemblyLoadContext.Default`. It SHALL NOT create per-load collectible `AssemblyLoadContext` instances for extensions, and SHALL NOT hold or `Unload()` an extension load context.

#### Scenario: Local assembly loads into the Default context
- **WHEN** an extension is loaded from a local `.dll` path
- **THEN** the assembly is loaded into `AssemblyLoadContext.Default`
- **AND** no collectible `AssemblyLoadContext` is created for it

#### Scenario: Extension contract types share host identity
- **WHEN** an extension assembly implementing `IDmonExtension` is loaded
- **THEN** the loaded type's `IDmonExtension` resolves to the same `Type` as the host's
- **AND** reflection discovery registers the extension's tools

#### Scenario: Loading a second extension does not disturb the first
- **WHEN** extension A is loaded and then extension B is loaded
- **THEN** extension A's tools remain registered and callable
- **AND** no `Unload()` is invoked on any context as a result of loading B

### Requirement: Extension transitive dependencies are resolved
The extension loader SHALL resolve an extension's transitive dependencies. For each loaded extension assembly it SHALL consult an `AssemblyDependencyResolver` built from the extension assembly's path (reading its `.deps.json`), and SHALL fall back to probing the extension assembly's own directory when no `.deps.json` entry matches.

#### Scenario: Extension with a sibling dependency loads
- **WHEN** an extension assembly depends on another assembly present in its own directory
- **THEN** the dependency resolves and the extension loads without a missing-assembly error

#### Scenario: Extension with a deps.json-described dependency loads
- **WHEN** an extension assembly ships a `.deps.json` describing a dependency
- **THEN** the loader resolves that dependency via the dependency resolver

### Requirement: Conflicting dependency versions are not supported
With all extensions sharing the Default context, the loader SHALL use a first-writer-wins policy for dependency versions: once a version of an assembly is loaded, subsequent extensions requiring a different version receive the loaded version or fail to load.

#### Scenario: Second extension requesting a different version gets the loaded one
- **WHEN** extension A has loaded version X of a dependency and extension B requires version Y of the same dependency
- **THEN** extension B resolves against version X (no second version is loaded)

### Requirement: Unload deregisters tools without reclaiming assemblies
`extension.unload <name>` SHALL remove the named extension's tools from the tool registry and emit `ExtensionUnloadedEvent`. It SHALL NOT unload or reclaim the extension's assemblies; those remain resident in the process until the process exits. Reclaiming extension code is achieved by restarting the core process.

#### Scenario: Unloaded tools stop being offered
- **WHEN** `extension.unload <name>` is invoked for a registered extension
- **THEN** that extension's tools are removed from the registry and no longer offered to the LLM
- **AND** an `ExtensionUnloadedEvent` is emitted

#### Scenario: Re-loading after unload does not require new type identity
- **WHEN** an extension is unloaded and then the same assembly is loaded again in the same process
- **THEN** loading succeeds using the already-resident assembly

### Requirement: Script loading uses no dedicated load context
The `.csx` script loader SHALL NOT create or hold an `AssemblyLoadContext` of its own. Script compilation and assembly loading remain delegated to Dotnet.Script.

#### Scenario: Script extension loads and returns tools
- **WHEN** a `.csx` script that returns one or more `AIFunction` instances is loaded
- **THEN** its tools are registered
- **AND** the loader holds no `AssemblyLoadContext` reference for the script
