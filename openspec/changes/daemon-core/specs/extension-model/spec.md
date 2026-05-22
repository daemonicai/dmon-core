## ADDED Requirements

### Requirement: IDaemonExtension contract
The system SHALL define a `IDaemonExtension` interface in the `Daemon.Extensions` NuGet package. NuGet extensions implement this interface to expose tools to the agent.

```csharp
public interface IDaemonExtension
{
    string Name { get; }
    string Description { get; }
    IEnumerable<AIFunction> Tools { get; }
}
```

#### Scenario: Extension discovered by reflection
- **WHEN** a NuGet extension assembly is loaded
- **THEN** the core discovers all types implementing `IDaemonExtension` via reflection, instantiates them, and registers their `Tools`

#### Scenario: Extension with no tools
- **WHEN** an `IDaemonExtension` implementation returns an empty `Tools` collection
- **THEN** the core registers the extension without error and emits `extensionLoaded` with an empty `tools` array

### Requirement: .csx script loading via Dotnet.Script.Core
The system SHALL load `.csx` scripts using `Dotnet.Script.Core`, supporting the `#r "nuget:..."` directive for runtime NuGet package resolution.

#### Scenario: Script returns AIFunction
- **WHEN** a `.csx` script is loaded and its final expression is one or more `AIFunction` instances
- **THEN** the core registers those functions into the session tool registry

#### Scenario: Script with NuGet dependency
- **WHEN** a `.csx` script contains `#r "nuget: PackageName, Version"`
- **THEN** the core resolves and loads the package before executing the script, subject to the permission gate (`risk: high`)

#### Scenario: Script compilation error
- **WHEN** a `.csx` script fails to compile
- **THEN** the core emits `extensionError` with the compilation diagnostics and does not partially register any tools

### Requirement: NuGet extension loading via AssemblyLoadContext
NuGet extensions SHALL be loaded into a collectible `AssemblyLoadContext` to allow unloading without restarting the agent process.

#### Scenario: Extension load is permission-gated
- **WHEN** the host sends `extension.load {source}` with a NuGet package ID or local path
- **THEN** the permission gate emits `tool.confirmRequest` with `risk: high` showing the source string (and, for NuGet sources, the resolved package id and version) before any network call or assembly load

#### Scenario: Extension loaded after approval
- **WHEN** the user approves an `extension.load` request
- **THEN** the core resolves the package (if a NuGet id) to `~/.daemon/extensions/<package>/<version>/`, loads the assembly in a new `AssemblyLoadContext`, discovers `IDaemonExtension` types, and emits `extensionLoaded`

#### Scenario: Extension load failure surfaces diagnostics
- **WHEN** loading an extension fails (resolution, assembly load, or reflection error)
- **THEN** the core emits `extensionError {source, phase, diagnostics[]}` and does not partially register any tools

#### Scenario: Extension unloaded
- **WHEN** the host sends `extension.unload {name}`
- **THEN** the core removes the extension's tools from the registry, releases the `AssemblyLoadContext`, and emits `extensionUnloaded`

### Requirement: Tool registry is per-session and per-call
The tool registry SHALL be scoped to the current session. `ChatOptions.Tools` SHALL be built from the current registry on each LLM call so that extensions loaded or unloaded mid-session are reflected immediately.

#### Scenario: Tool registered mid-session is available in next turn
- **WHEN** an extension is loaded during a session and a new turn begins
- **THEN** the new extension's tools appear in `ChatOptions.Tools` for that turn

#### Scenario: Tool unregistered mid-session is absent in next turn
- **WHEN** an extension is unloaded during a session and a new turn begins
- **THEN** the unloaded extension's tools do not appear in `ChatOptions.Tools`

### Requirement: promote command
The system SHALL provide a `promote` command that scaffolds a working `.csx` script into a NuGet extension project.

#### Scenario: Promote extracts NuGet references
- **WHEN** the host sends `extension.promote {name}` for a loaded `.csx` script
- **THEN** the core generates a `.csproj` with `<PackageReference>` elements derived from the script's `#r "nuget:..."` directives

#### Scenario: Promote wraps script body in IDaemonExtension
- **WHEN** `extension.promote` is executed
- **THEN** the generated project contains a class implementing `IDaemonExtension` whose `Tools` property returns the `AIFunction` instances from the original script
