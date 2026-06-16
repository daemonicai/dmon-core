## Why

ADR-025 makes `dmon-core` the monorepo seed and sequences satellite imports as phases. Phase 1 (`graft-llamacpp-provider`) established the repeatable, history-preserving graft recipe. This is the **next satellite**: grafting the dmon Dmail **tool extension** into `tools/`. Unlike the llamacpp provider — which was already on the current API — the Dmail extension was written against the **pre-ADR-022 SDK** (`Dmon.Extensions` package, `IDmonExtension`), so it additionally needs the API port deferred to graft time. This change exercises the graft recipe for the `tools/` bucket and proves the port path that the remaining tool/extension satellites will reuse.

## What Changes

- **History-preserving import** of the Dmail tool extension from the local-only `dmail` repo (consolidated on `feat/dmon-tool-dmail` @ `b1af562`) via `uvx git-filter-repo` path-renames + `git merge --allow-unrelated-histories`. Keep **only** `src/Dmon.Extensions.Dmail/` → `tools/Dmon.Tools.Dmail/` and the single self-contained `test/Dmail.Tests/DmailExtensionTests.cs` → `test/Dmon.Tools.Dmail.Tests/`. Drop the standalone Dmail server, server-coupled tests, the satellite's `openspec/`, `nuget.config`, vendored nupkgs, and `Directory.*` plumbing.
- **Scope — extension only.** The standalone Dmail ASP.NET Core server (`src/Dmail/`) is **not** grafted; it stays in its own repo as the deployable service the extension targets over HTTP (`DMAIL_BASE_URL`/`DMAIL_API_KEY`). The monorepo buckets (`core/providers/tools/middleware/frontends`) have no home for a standalone service.
- **BREAKING (names only):** rename `Dmon.Extensions.Dmail` → `Dmon.Tools.Dmail` (assembly, `RootNamespace`, `PackageId`, `.csproj` filename, directories) per ADR-023 D3; rewrite the C# namespace `Daemonic.Dmail.Extension` → `Dmon.Tools.Dmail` and the test namespace `Daemonic.Dmail.Tests` → `Dmon.Tools.Dmail.Tests`.
- **BREAKING — API port (ADR-022):** the extension targets the deleted `Dmon.Extensions` package. Change `using Dmon.Extensions;` → `using Dmon.Abstractions.Extensions;` and `IDmonExtension` → `IToolExtension` (the contract surface — `Name`/`Description`/`Tools`/`Evaluate` — is identical, so the method bodies are unchanged); `DmonAIFunctionFactory` still lives in `Dmon.Abstractions`. Update README/doc-comment registration references from `AddExtension` to the current `builder.AddToolExtension<T>()` verb.
- **Intra-repo references (ADR-025 D4):** replace `<PackageReference Include="Dmon.Extensions" Version="0.2.*" />` with a `ProjectReference` to `core/Dmon.Abstractions/Dmon.Abstractions.csproj`. Author a **fresh** test `.csproj` for `test/Dmon.Tools.Dmail.Tests/` that `ProjectReference`s only the grafted tool (the satellite's test project also referenced `src/Dmail`, which is not grafted).
- **Central Package Management:** remove the satellite's standalone `<Version>0.2.0</Version>` (MinVer drives versioning), strip inline `Version=` from `PackageReference`s, reuse the root `Directory.Packages.props` pins (xunit/coverlet/Test.Sdk/runner already centrally pinned since Phase 0). Keep the tool `IsPackable=true` with `MinVerTagPrefix` and a `Description` consistent with `Dmon.Tools.Builtin`.
- **Solutions:** add `tools/Dmon.Tools.Dmail/Dmon.Tools.Dmail.csproj` (under `/tools/`) and its test (under `/test/`) to `tools.slnx` and `Everything.slnx`.
- Record `dmail` as **absorbed-but-live**: only the extension is grafted; the Dmail server remains a separate first-party repo. The `dmail` repo is left intact and untouched until the graft merges and verifies.

## Capabilities

### New Capabilities
- `dmail-tool`: the behavioural contract of the Dmail tool extension — three agent tools (`search_email`, `check_new_messages`, `get_email`) over the Dmail HTTP API; env-var configuration (`DMAIL_BASE_URL`/`DMAIL_API_KEY`); a permission policy that allows the metadata-only tools and prompts for `get_email` (which returns full private message bodies); and graceful degradation to a friendly message when Dmail is unreachable. Authored as this change's spec delta (all `ADDED`), synced to `openspec/specs/dmail-tool/` on archive — the same provenance pattern Phase 1 used for `llamacpp-provider`. The satellite's own `openspec/` (its 7 server-side capabilities) describes the Dmail **server** and is deliberately not imported.

### Modified Capabilities
<!-- None. The graft conforms to the existing `monorepo-layout` standing spec (role buckets, per-area `.slnx` + `Everything.slnx`, intra-repo `ProjectReference`, CPM, ADR-023 D3 naming) without changing any of its requirements. -->

## Impact

- **New projects:** `tools/Dmon.Tools.Dmail/` (packable tool extension, imported with history) and `test/Dmon.Tools.Dmail.Tests/` (one grafted test file with history + a fresh `.csproj`).
- **Build plumbing:** `tools.slnx` + `Everything.slnx` gain two projects; `Directory.Packages.props` gains any third-party pin the extension needs that isn't already central (expected: none beyond the test deps already pinned in Phase 0).
- **Git history:** `dmon-core` gains the grafted Dmail-extension commit history under the new paths (one-time `--allow-unrelated-histories` merge).
- **No runtime, RPC, protocol, or session-storage behaviour change** to existing code; the tool is additive and only active when its extension is composed into an agent's `Dmon.cs`.
- **Tooling prerequisite:** `git-filter-repo` (via `uvx git-filter-repo`; fallback `brew install git-filter-repo`). CI unaffected (no workflow changes here).
- **Out of scope** (later/fast-follow per ADR-025): the Dmail **server** graft, dependency-aware path-filtered CI, the two-family release matrix, and any behaviour change to the extension or existing dmon-core code.
