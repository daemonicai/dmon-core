# Build-config hygiene (finding #9)

## Why

Four build-configuration hygiene defects (audit finding #9) weaken the guarantees the repo's shared build machinery is supposed to give:

- **#9a — `TreatWarningsAsErrors` / `Nullable` are not centralized.** The root `Directory.Build.props` centralizes Authors/versioning/MinVer/skew-guard but **not** `TreatWarningsAsErrors` or `Nullable`. Both are hand-copied into every one of the 46 project files. Duplication invites drift: a new project can silently omit one (as one already has — see #9b), quietly opting out of the project-wide warning-clean rule (CLAUDE.md: "All code must build without warnings — `TreatWarningsAsErrors` is on").
- **#9b — `samples/Dmon.ExtensionSmoke` lacks `TreatWarningsAsErrors`.** Its sibling `samples/Dmon.SampleExtension` has it; the smoke consumer does not. It compiles today only because warnings are not fatal for it — exactly the drift #9a enables.
- **#9c — `NoWarn;NU1903` is applied repo-wide.** `Directory.Build.props` suppresses the `NU1903` vulnerable-package advisory (GHSA-2m69-gcr7-jv3q, carried by `SQLitePCLRaw.lib.e_sqlite3` transitively via `Microsoft.Data.Sqlite`) across **all 46 projects**. Only 6 projects actually pull that dependency; the repo-wide blanket masks **any future** `NU1903` on **any** package in the other 40.
- **#9d — `Markdig` floats at `1.*`.** `Directory.Packages.props` pins `Markdig` to `1.*` (and opts the whole repo into CPM floating versions to allow it). A floating pin makes restores non-reproducible: two builds on different days can resolve different Markdig builds with no source change.

None of these four is fixed by a prior change (the archived `ci-hardening` change touched make targets, path-filtered CI, and a test-project rename — not `Directory.Build.props`, `Directory.Packages.props`, or these invariants). No ADR governs build-config hygiene, so none is contradicted.

## What Changes

- **#9a** Hoist `TreatWarningsAsErrors=true` and `Nullable=enable` into the root `Directory.Build.props` and delete the per-`.csproj` duplicates. Behaviour must stay net-unchanged: every project that is TWE today stays TWE. (There is no nested `Directory.Build.props` in the repo — the root is the single centralization target.)
- **#9b** Centralization (9a) brings `samples/Dmon.ExtensionSmoke` under TWE. **Decision: include it** (samples stay warning-clean like the rest of the repo). Any latent warning it surfaces is **fixed at source**, never suppressed.
- **#9c** Remove the repo-wide `NoWarn;NU1903` from the root and scope the suppression to exactly the 6 projects that reference `Microsoft.Data.Sqlite` (the vulnerable transitive's only consumers). The advisory stays suppressed where it genuinely applies; every other project regains its `NU1903` signal.
- **#9d** Pin `Markdig` to the exact version currently resolving (`1.3.2`) and remove the now-unneeded `CentralPackageFloatingVersionsEnabled` opt-in (it exists only to permit Markdig's float, per its own comment).

No feature code, RPC surface, persisted format, or public contract changes. This is a build-configuration-only change; the tree must stay green and warning-clean.

## Capabilities

### Modified Capabilities

- **monorepo-layout** — adds an invariant that warning-clean settings (`TreatWarningsAsErrors`, `Nullable`) are centralized in the root build props (no per-project duplication), that vulnerable-package advisory suppressions are scoped to their actual consumers rather than repo-wide, and that third-party package versions are exact-pinned (no floating versions).

## Impact

- **Affected files (implementation, out of scope for these artifacts):** root `Directory.Build.props`, root `Directory.Packages.props`, and all 46 `.csproj` (remove duplicated `TreatWarningsAsErrors`/`Nullable`; add scoped `NoWarn;NU1903` to the 6 Sqlite consumers).
- **Affected spec:** `openspec/specs/monorepo-layout/spec.md` (one ADDED requirement).
- **Risk:** hoisting TWE onto a project that only compiles because it *lacks* TWE (e.g. `Dmon.ExtensionSmoke`, or the `default-core/Dmon.cs` file-based program, which inherits the root props with no shadowing `Directory.Build.props`) can surface previously-latent warnings as errors. Those are **fixed**, not suppressed. See `design.md`.
- **No behaviour change** for every project already under TWE.
