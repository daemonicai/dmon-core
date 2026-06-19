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

## NEXT

- **Up next:** Group 2 — core version stamping: read `AssemblyInformationalVersionAttribute` for `agentReady.coreVersion` in `RpcHostedService.cs` + core-side test.
- **Open questions:** None.
- **Nits / deferred:** Group 3.6 decision (fate of startup/`[Reload]` scrollback lines) to be made during Group 3.
- **Carry-forward:** Build stays red until 3.7 stubs `FakeTerminal`. Exact dcli signatures recorded above for Group 3.
