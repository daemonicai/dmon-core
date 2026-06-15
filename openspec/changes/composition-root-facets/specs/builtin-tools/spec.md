# builtin-tools Specification

## RENAMED Requirements

- FROM: `### Requirement: Daemon.BuiltinTools has no dependency on Daemon.Core`
- TO: `### Requirement: Dmon.Tools.Builtin has no dependency on Dmon.Core`

## MODIFIED Requirements

### Requirement: Built-in tool suite available at startup
The system SHALL provide six built-in tools — `read_file`, `write_file`, `edit_file`, `glob`, `fetch`, and `bash` — shipped as the single composable package `Dmon.Tools.Builtin` (ADR-023 D6). Each tool SHALL be implemented as an `IToolExtension` in that package. The tools SHALL be registered into `IToolRegistry` only when the composition root opts them in through the builder via `.AddBuiltinTools()` — they SHALL NOT be hard-wired into the engine. A `Dmon.cs` that calls `.AddBuiltinTools()` SHALL have all six tools available before its first turn.

#### Scenario: Tools available on first turn when composed
- **WHEN** a session starts from a composition root that called `.AddBuiltinTools()` with no other user extensions
- **THEN** `IToolRegistry.GetAll()` returns at least six `AIFunction` entries with names `read_file`, `write_file`, `edit_file`, `glob`, `fetch`, and `bash`

#### Scenario: Tool names use snake_case
- **WHEN** the LLM receives the tool list in `ChatOptions.Tools`
- **THEN** each built-in `AIFunction.Name` matches the snake_case convention in the design (`read_file`, `write_file`, `edit_file`, `glob`, `fetch`, `bash`)

### Requirement: Dmon.Tools.Builtin has no dependency on Dmon.Core
The `Dmon.Tools.Builtin` package SHALL reference only `Dmon.Abstractions` (which carries the `IToolExtension` contract and `Microsoft.Extensions.AI` types) and `Dmon.Protocol`. It SHALL NOT reference `Dmon.Core` (the engine) or any project that references `Dmon.Core`, so it is a granular implementation package structurally identical to any other provider/tool package (ADR-023 D2/D6).

#### Scenario: Project graph is acyclic
- **WHEN** the solution dependency graph is inspected
- **THEN** there is no path from `Dmon.Tools.Builtin` back to `Dmon.Core`

## ADDED Requirements

### Requirement: Builtin tools are scaffolded but genuinely removable
The scaffolded `Dmon.cs` produced by the tooling SHALL include the `Dmon.Tools.Builtin` `#:package` line and a `.AddBuiltinTools()` call so a fresh agent has the filesystem and bash tools by default. The package SHALL be genuinely opt-in: an author SHALL be able to remove the `#:package` line and the `.AddBuiltinTools()` call, producing a valid locked-down composition with no filesystem or bash tools at all (ADR-023 D6). Such a composition SHALL build and run; the absence of the builtin tools SHALL NOT be an error.

#### Scenario: Scaffold includes builtin tools by default
- **WHEN** a `Dmon.cs` is scaffolded by the tooling
- **THEN** it contains a `#:package Dmon.Tools.Builtin@<protocol>.*` line and a `.AddBuiltinTools()` call in the composition

#### Scenario: A locked-down agent omits builtin tools
- **WHEN** an author removes the `Dmon.Tools.Builtin` `#:package` line and the `.AddBuiltinTools()` call from their `Dmon.cs`
- **THEN** the composition builds and runs, `IToolRegistry.GetAll()` returns no filesystem or bash tools, and no error is raised for their absence
