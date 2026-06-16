# DEVLOG — graft-dmail

Phase 2 monorepo satellite graft: import the dmon Dmail **tool extension** into `tools/Dmon.Tools.Dmail`, following the Phase 1 (`graft-llamacpp-provider`) recipe plus an ADR-022 API port. The standalone Dmail server stays in its own repo (extension-only scope).

## Status

- [x] Group 1 — History-preserving import
- [x] Group 2 — Rename to the tool family (Dmon.Tools.Dmail)
- [x] Group 3 — API port to IToolExtension / Dmon.Abstractions
- [x] Group 4 — Re-wire to monorepo conventions (ProjectReference, CPM, fresh test csproj)
- [x] Group 5 — Solutions
- [x] Group 6 — Verification gates

---

## Group 1 — History-preserving import

**Source:** local-only `dmail` repo, branch `feat/dmon-tool-dmail` @ `b1af562` (the extension is *not* on dmail's `main` — it lives only on this branch, 2 commits ahead). The extension itself was added upstream in a single commit (`7556790 feat: add Dmon.Extensions.Dmail dmon extension package`), so that one commit is its complete history.

**What landed:** a two-parent merge commit importing exactly 7 files — `tools/Dmon.Tools.Dmail/{DmailApiException,DmailClient,DmailExtension,DmailModels}.cs`, `Dmon.Extensions.Dmail.csproj` (filename renamed in Group 2), `README.md`, and `test/Dmon.Tools.Dmail.Tests/DmailExtensionTests.cs`. No server code, server tests, satellite `openspec/`, `nuget.config`, vendored nupkgs, or `Directory.*` plumbing.

**Decisions / lessons:**
- **`git clone` for filter-repo needs `--no-local -b <branch>`.** The design's recipe omitted these. A plain local clone is hardlinked (not "freshly packed") and a post-clone `git checkout` adds a second HEAD reflog entry — filter-repo aborts on both ("not a fresh clone" / "expected at most one entry in the reflog for HEAD"). Cloning the target branch directly with `--no-local -b feat/dmon-tool-dmail` satisfies both safety checks. (Recorded so the next tool/extension graft skips the two false starts.)
- The grafted files are **inert** after import: not referenced by any `*.slnx`, so `dotnet build Everything.slnx -c Release` stays green (0 warnings, 0 errors). Rename/port/CPM/slnx wiring are Groups 2–5.
- The imported test (`DmailExtensionTests.cs`) is genuinely server-independent — references only the extension namespace, `Microsoft.Extensions.AI`, and `Dmon.Protocol.Enums`; it constructs `DmailExtension` against an unreachable port. Its `.csproj` is authored fresh in Group 4 (the satellite's test project referenced `src/Dmail`, which is not grafted).

**Gates:** `Everything.slnx` build green; history verified via `git log --follow tools/Dmon.Tools.Dmail/DmailExtension.cs`; reviewer audit PASS (no blockers, no nits). Stale names/references (`Dmon.Extensions.Dmail`, `IDmonExtension`, `using Dmon.Extensions`, the `Dmon.Extensions` PackageReference, standalone `<Version>`, `Daemonic.Dmail.*` namespaces) are deferred-by-design to Groups 2–4.

---

## Group 2 — Rename to the tool family (Dmon.Tools.Dmail)

Name-only rename, mirroring the Phase 0/1 Omlx/LlamaCpp rename. `git mv`'d the extension `.csproj` to `Dmon.Tools.Dmail.csproj` (git recorded it as a rename, ~87% similarity) and set `AssemblyName`/`RootNamespace`/`PackageId` = `Dmon.Tools.Dmail`. Rewrote `namespace Daemonic.Dmail.Extension` → `Dmon.Tools.Dmail` across the four src files and `Daemonic.Dmail.Tests` → `Dmon.Tools.Dmail.Tests` + the extension `using` in the test.

**Review loop (1 iteration):** the worker's first pass missed the package `README.md` — its grep didn't cover the `# Dmon.Extensions.Dmail` title, the `#:package Dmon.Extensions.Dmail@0.2.*` pin, and the `using Daemonic.Dmail.Extension;` line, so gate 2.4 (clean repo-wide grep, which design Risk-list explicitly extends to the README) failed. Fixed the three rename tokens, leaving the `.AddExtension<>` verb for Group 3 (the README's verb update is task 3.2). Reviewer's other checks all passed first time, including confirming **no scope creep** — `using Dmon.Extensions;`, `IDmonExtension`, the `Dmon.Extensions` PackageReference, and `<Version>0.2.0</Version>` were correctly left untouched for Groups 3/4.

**Lesson:** the rename gate's grep must include `*.md` (README), not just `*.cs`/`*.csproj`. The Group 2/3 README boundary is subtle: rename tokens (title, package pin, namespace `using`) are Group 2; the registration **verb** (`AddExtension` → `AddToolExtension`) and doc-comment are Group 3.

**Gates:** repo-wide grep for `Dmon.Extensions.Dmail` / `Daemonic.Dmail` returns zero code-artifact matches (only the graft-dmail change docs + ADR-025 remain, which describe the rename); `make build` clean (0/0); `make test` green (565 + 51 pass, 1 skip — grafted Dmail tests not yet in a solution, run after Groups 4–5); `openspec validate --strict` passes. Project still intentionally not independently buildable (Group 3/4 fix the package reference).

---

## Group 3 — API port to IToolExtension / Dmon.Abstractions (ADR-022)

The grafted extension targeted the deleted `Dmon.Extensions` package / `IDmonExtension`. ADR-022 collapsed that into `Dmon.Abstractions` and renamed the interface to `IToolExtension`. Port:
- `DmailExtension.cs`: `using Dmon.Extensions;` → `using Dmon.Abstractions.Extensions;`, `: IDmonExtension` → `: IToolExtension`. **No body changes** — the contract surface (`Name`, `Description`, `IEnumerable<AIFunction> Tools`, `Evaluate(FunctionCallContent, IPermissionSettings, IPermissionSettings?) → PermissionResult`) is identical, and `DmonAIFunctionFactory` lives in the same `Dmon.Abstractions.Extensions` namespace, so the single `using` swap covers both the interface and the factory.
- Registration verb updated `AddExtension` → `AddToolExtension` in the class doc-comment (3 spots) and `README.md` (2 spots); verb name confirmed against core `DmonRegistrationExtensions` + the `IToolExtension` doc-comment.

**Verification:** `Everything.slnx` build clean (0/0); grep for `IDmonExtension` / `using Dmon.Extensions;` in `tools/Dmon.Tools.Dmail/**` returns nothing; `openspec validate --strict` passes. The orchestrator verified the diff directly (small, mechanical, contract independently confirmed by the worker against core) in lieu of a separate reviewer pass.

**Still not independently buildable:** the csproj continues to reference the deleted `Dmon.Extensions` package — Group 4 swaps that for a `ProjectReference` to `core/Dmon.Abstractions`, at which point the ported source first compiles.

---

## Group 4 — Re-wire to monorepo conventions (ProjectReference, CPM, fresh test csproj)

The group where the project first compiles. Swapped the deleted-package `<PackageReference Include="Dmon.Extensions" Version="0.2.*" />` for `ProjectReference`s to `core/Dmon.Abstractions` **and** `core/Dmon.Protocol`; removed the standalone `<Version>0.2.0</Version>`/`<Authors>` (MinVer + root props drive versioning) and stripped inline `Version=` (CPM). Authored a fresh `test/Dmon.Tools.Dmail.Tests/Dmon.Tools.Dmail.Tests.csproj` (CPM-bare, references only the tool — the satellite's server-coupled test project was not imported). No `Directory.Packages.props` change needed (all pins already central from Phase 0).

**Review loop (1 iteration) — two reviewer findings, both fixed:**
- **B1 (blocker): false package metadata.** The worker pattern-copied the sibling `Dmon.Tools.Builtin` `Description` ("Exposes AddBuiltinTools()") into "Exposes AddDmailTools()" — but **no such verb exists**. Dmail is a single `IToolExtension` registered via the generic `builder.AddToolExtension<DmailExtension>()` (core's `Dmon.Hosting` verb), which already satisfies ADR-023's "ships its fluent verb" at the family level — a bespoke `AddDmailTools()` is not required. Corrected the `Description` to name the real verb (no new code). Confirmed the regenerated `obj/Release/*.nuspec` no longer contains `AddDmailTools`.
- **N1 (strong nit): transitive Protocol reference.** The source directly `using`s `Dmon.Protocol.Enums`/`Dmon.Protocol.Permissions`, but the csproj referenced only `Dmon.Abstractions` (relying on transitive Protocol flow-through). Added the explicit `core/Dmon.Protocol` `ProjectReference` to match the sibling and avoid depending on transitive re-exposure of directly-used types.

**Lesson:** when scaffolding a grafted package's csproj from a sibling, the `Description`/verb claims are *content*, not boilerplate — verify them against the actual code, and reference assemblies whose types you use directly rather than leaning on transitive flow.

**Gates:** `dotnet build` of the tool clean (0/0, TreatWarningsAsErrors); `dotnet test` of the new test project green (4/4); `Everything.slnx` build clean; `dotnet pack` → `Dmon.Tools.Dmail.0.2.0-alpha.0.39.nupkg`, `Major.Minor=0.2` matches `core/Dmon.Protocol/ProtocolVersion.cs` (skew-guard passes); `openspec validate --strict` passes. The two projects are still absent from all `.slnx` — Group 5 wires them in.

---

## Group 5 — Solutions

Added `tools/Dmon.Tools.Dmail/Dmon.Tools.Dmail.csproj` (under `/tools/`) and `test/Dmon.Tools.Dmail.Tests/Dmon.Tools.Dmail.Tests.csproj` (under `/test/`) to both `tools.slnx` and `Everything.slnx`, matching the existing append convention (Dmail after Builtin/LlamaCpp). Done directly by the orchestrator (solution-file plumbing, two `<Project>` lines per file).

## Group 6 — Verification gates (all green)

- **6.1** `dotnet build Everything.slnx -c Release` — 0 warnings / 0 errors (TreatWarningsAsErrors).
- **6.2** `make build` clean; `make test` green — the **4 `DmailExtensionTests` now run as part of the solution** (Dmon.Tools.Dmail.Tests 4/4) alongside the full existing suite (all projects pass, 0 failures).
- **6.3** `dotnet pack` → `Dmon.Tools.Dmail.0.2.0-alpha.0.39.nupkg`; `Major.Minor=0.2` matches `core/Dmon.Protocol/ProtocolVersion.cs` (protocol skew-guard passes).
- **6.4** `git log --follow tools/Dmon.Tools.Dmail/DmailExtension.cs` → `7556790 feat: add Dmon.Extensions.Dmail dmon extension package` (pre-graft history preserved through both path-renames).
- **6.5** `openspec validate graft-dmail --strict` passes.

**All 6 groups complete.** The Dmail tool extension is grafted into `tools/Dmon.Tools.Dmail`, ported to ADR-022 (`IToolExtension`), wired to monorepo conventions, in both solutions, building/testing/packing green with history preserved. The Dmail **server** remains a separate first-party repo (extension-only scope); `dmail` is left intact and untouched (absorbed-but-live — only the extension was grafted; the source branch `feat/dmon-tool-dmail` can optionally carry a local `extension-absorbed-into-dmon-core` tag).
