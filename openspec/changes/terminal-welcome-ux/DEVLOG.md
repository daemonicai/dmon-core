# DEVLOG: terminal-welcome-ux

<!-- Symmetric input-zone frame (option C) over dcli 0.3.0-rc.0 + agentReady.coreVersion fix. -->

## 1. Bump the dcli dependency

- Bumped `Dcli` and `Dcli.Testing` `0.2.0-rc.4` → `0.3.0-rc.0` in `Directory.Packages.props` (CPM — only the two `PackageVersion` entries).
- Confirmed the new API surface from the resolved package's `Dcli.xml`:
  - `ITerminal.InputPreamble { get; }` → `IInputPreamble`
  - `IInputPreamble.SetRows(Line[])` and `SetRows(IReadOnlyList<Line>)`
  - `IInput.SetPrompt(Line)` and `IInput.SetPrompt(string)`
- **Known sequencing fact:** the whole-solution build is RED from here until Group 3, because `Fakes/FakeTerminal.cs` (+ nested `FakeInput`) don't yet implement the three new interface members (CS0535). Task 3.7 fixes this. The per-group `make build clean` gate is therefore only literally satisfiable from Group 3 onward; Groups 1–2 commits intentionally leave the test project non-compiling on a feature branch, and Group 4 is the real whole-solution green gate.
- **Review:** version-pin-only diff (2 lines), API resolution independently verified by the worker → audited inline by the orchestrator rather than a full reviewer pass.

## 2. Core version stamping (`agent-core`)

- Extracted `internal static string ResolveCoreVersion(Assembly assembly)` in `RpcHostedService.cs`; call site (~line 38) passes `Assembly.GetExecutingAssembly()`. Chain: `AssemblyInformationalVersionAttribute.InformationalVersion` → `GetName().Version?.ToString()` → `"0.0.0"`.
- **Decision:** keep MinVer's `+<sha>` build-metadata suffix — `coreVersion` is a display string, not parsed as SemVer downstream. Spec says "the stamped informational/package version", so no trim.
- **Decision:** test via an `Assembly` parameter seam (not reflection-mocking the executing assembly). Friend access is the **pre-existing** `[assembly: InternalsVisibleTo("Dmon.Core.Tests")]` in `core/Dmon.Core/Properties/AssemblyInfo.cs` — reused, not newly added.
- New `test/Dmon.Core.Tests/Rpc/RpcHostedServiceVersionTests.cs` (4 tests): stamped-present → exact string incl. `+sha`; attribute-absent → numeric; never-empty (×2). Note: the `"0.0.0"` literal sentinel is unreachable via `AssemblyBuilder` (dynamic assemblies always yield `0.0.0.0`, never null) — reviewer judged this an acceptable gap since the spec's actual guarantee (never-empty) is asserted from two angles.
- MinVer 6.0.0 `GlobalPackageReference` stamps `Dmon.Core` (has `<MinVerTagPrefix>core-</MinVerTagPrefix>`) — no csproj change needed.
- `agentReady` wire shape `{protocolVersion, coreVersion}` unchanged — only the value derivation.
- **Review:** reviewer SIGN-OFF. One non-blocking nit (smoke-test comment says "Dmon.Core" but `GetExecutingAssembly()` there resolves to the test assembly) — left as-is, harmless.
- **Gate caveat:** built/tested core project + core test project only (whole-solution still red until Group 3). Core build clean, 4/4 tests pass.

## NEXT

- **Up next:** Group 3 — terminal symmetric-frame redesign (`terminal-host`): banner/MOTD, `InputPreamble` rule, `❯ ` prompt, readiness status row, `FakeTerminal` stub, unit + harness tests. This group brings the whole-solution build back to green.
- **Open questions:** None.
- **Nits / deferred:** Group 3.6 decision (fate of startup/`[Reload]` scrollback lines) to be made during Group 3.
- **Carry-forward:** dcli 0.3.0-rc.0 signatures (Group 1 section). Renderer gets a `_coreVersion` field fed from `Program.cs` `coreSession.AgentReady.CoreVersion`.
