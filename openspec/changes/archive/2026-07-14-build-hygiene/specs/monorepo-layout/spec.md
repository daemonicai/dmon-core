# monorepo-layout — build-config hygiene delta

## ADDED Requirements

### Requirement: Centralized warning-clean build settings and reproducible restores

Warning-clean and nullable-reference settings SHALL be defined once in the root `Directory.Build.props` and SHALL NOT be duplicated per-project: `TreatWarningsAsErrors=true` and `Nullable=enable` apply to every project in the repository via the root props (including the file-based `default-core` program, which has no shadowing `Directory.Build.props`). Vulnerable-package advisory suppressions (`NoWarn` for `NUxxxx` security advisories) SHALL be scoped to only the projects that reference the affected package, never applied repository-wide. Third-party package versions in the CPM catalogue SHALL be exact-pinned; floating version specifiers (e.g. `1.*`) SHALL NOT be used, and the repo SHALL NOT enable CPM floating-version support.

#### Scenario: Warning settings live only in the root props

- **WHEN** the project files are inspected for `TreatWarningsAsErrors` and `Nullable`
- **THEN** both settings are declared in the root `Directory.Build.props` and no `.csproj` redeclares either of them, and every project builds warning-clean under `TreatWarningsAsErrors`

#### Scenario: Vulnerable-package suppression is scoped to consumers

- **WHEN** a transitive package carries a security advisory (`NU1903` for `SQLitePCLRaw.lib.e_sqlite3` via `Microsoft.Data.Sqlite`) and the advisory is suppressed
- **THEN** the `NoWarn` suppression appears only on the projects that reference the affected package (the `Microsoft.Data.Sqlite` consumers), and the root `Directory.Build.props` does not suppress it repository-wide

#### Scenario: Package versions are exact-pinned

- **WHEN** the root `Directory.Packages.props` is inspected
- **THEN** every `PackageVersion` uses an exact version rather than a floating specifier, and CPM floating-version support is not enabled
