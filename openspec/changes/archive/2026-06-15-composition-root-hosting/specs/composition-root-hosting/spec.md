## ADDED Requirements

### Requirement: `dmoncore` exposes a hosting surface, not an entry point
`dmoncore` SHALL be a library exposing `DmonHost.CreateBuilder(args)`, returning a builder that configures the provider/model, extensions, permission mode, and profile, and whose `.Build().RunAsync(cancellationToken)` runs the JSONL/stdio core loop. The wire contract (ADR-003), session storage (ADR-004), and the permission pipeline (ADR-002/006) reached through `RunAsync()` SHALL be unchanged from the prior in-package entry point — only the entry point moves out of the library.

#### Scenario: Hosting surface runs the core loop
- **WHEN** a program calls `await DmonHost.CreateBuilder(args).Build().RunAsync(ct)`
- **THEN** the process serves the same JSONL/stdio protocol (including `agentReady`) a stock core served before, over the same wire contract

#### Scenario: Library carries no entry point
- **WHEN** the `dmoncore` library package is inspected
- **THEN** it exposes the `DmonHost` hosting API and contains no `Main`/top-level-statement entry point of its own

### Requirement: `Dmon.cs` is the composition root and extensions are compile-time
A `Dmon.cs` file-based program (`.NET 10`) in the working directory SHALL be the composition root: it declares the core and its extensions via `#:package` directives and wires them via builder calls, and compiles to the core executable. Extensions SHALL be **compile-time dependencies** (a `#:package`/`#:project`/`#:ref` plus a builder registration), not runtime loads. The compiled core SHALL be a single SDK-resolved identity graph in the Default `AssemblyLoadContext` (retaining ADR-008's one-context principle), and reclaiming extension code SHALL be achieved by restarting the process, not an unload command.

#### Scenario: An extension is composed at build time
- **WHEN** `Dmon.cs` declares `#:package Acme.DmonExt.Foo@2.1` and calls `.AddExtension<FooExtension>()`
- **THEN** building `Dmon.cs` restores and compiles the extension into the core, and its tools are registered at startup with no runtime load step

#### Scenario: Contract types share one identity
- **WHEN** the composed core runs
- **THEN** every extension's `IDmonExtension`/contract types resolve to the same `Type` as the host's (one compiled identity graph), with no per-extension load context

### Requirement: `config.yaml` is a settings source the composition may override
`config.yaml` (user and project scope) SHALL be retained for **settings**, not for an extension list, and SHALL be exposed to `Dmon.cs` through the builder's configuration. The composition code SHALL be able to read those settings or override them outright (e.g. hard-code a provider/model in `Dmon.cs`). Markdown/YAML declares; C# overrides.

#### Scenario: Code overrides config
- **WHEN** `config.yaml` selects one provider but `Dmon.cs` calls a builder method pinning a different provider/model
- **THEN** the composed core uses the value set in `Dmon.cs` (code wins over config)

#### Scenario: config.yaml no longer declares extensions
- **WHEN** a `config.yaml` contains a legacy `extensions:` list
- **THEN** it has no effect on the composed extension set (extensions come only from `Dmon.cs`); the list is ignored for composition

### Requirement: `dmon init` scaffolds an editable composition root
`dmon init` SHALL scaffold an editable `Dmon.cs` in the working directory that references the protocol-pinned `dmoncore` library and builds a working core, as the opt-in starting point for customisation. An empty directory with no `Dmon.cs` SHALL still run via the prebuilt default core (`core-runtime-acquisition`), so authoring is opt-in.

#### Scenario: init produces a buildable composition root
- **WHEN** `dmon init` is run in an empty directory
- **THEN** a `Dmon.cs` is written that declares `#:package dmoncore@<protocol>.*` and builds into a runnable core

### Requirement: `/reload` rebuilds and re-runs the composition root
`/reload` SHALL rebuild `Dmon.cs` (the SDK incremental up-to-date check restores only if the `#:package` set changed and recompiles only if any `.cs` changed) and re-run it, restarting the core process. This is the established restart-between-turns boundary; there is no in-process reload of composition.

#### Scenario: Reload picks up a composition change without a separate process model
- **WHEN** `Dmon.cs` is edited (a new `#:package` or builder call) and `/reload` is issued
- **THEN** the core is rebuilt incrementally and restarted, and the changed composition is in effect on the next turn

#### Scenario: Unchanged composition reloads cheaply
- **WHEN** `/reload` is issued with no change to `Dmon.cs` or its `#:package` set
- **THEN** the incremental build is a near-no-op and the core restarts without a restore or recompile
