## Why

ADR-025 makes `dmon-core` the monorepo seed and sequences the work in phases: Phase 0 (done — buckets, per-area `.slnx`, `Everything.slnx`, CPM, ghost-dir cleanup) established the skeleton; Phases 1..N each graft one satellite repo. This is **Phase 1** — the first satellite import — grafting the now-graft-ready `dmon-llama-cpp` provider into `providers/`. It is also the change that **resolves ADR-025's open "import mechanics" question** by establishing the repeatable, history-preserving graft recipe that the remaining satellites (dmail, dmon-meko, …) will follow.

`dmon-llama-cpp` is already consolidated on its own `main` (the `UseLlamaCpp` verb was finished against the current `IProviderRegistration` API; build/test/validate green), so its own work is complete. What remains is purely the **integration** into the monorepo: import with history, rename to the ADR-023 D3 provider family, swap its published-SDK `PackageReference` for an intra-repo `ProjectReference`, fold its pins into central package management, and wire it into the solutions — exactly the integration shape Phase 0 applied to the in-tree Omlx provider.

## What Changes

- **History-preserving import** of the local-only `dmon-llama-cpp` repo (consolidated `main` @ `f9c6c4d`) via `git filter-repo` path-renames + `git merge --allow-unrelated-histories`, landing source under `providers/Dmon.Providers.LlamaCpp/` and tests under `test/Dmon.Providers.LlamaCpp.Tests/`. (`git filter-repo` is not yet installed — see design for acquisition.)
- **BREAKING (names only):** rename `Dmon.Extensions.LlamaCpp` → `Dmon.Providers.LlamaCpp` (assembly, `RootNamespace`, `PackageId`, directories, and `namespace`/`using` in src + tests), per ADR-023 D3 — identical to the Phase 0 Omlx rename.
- **Intra-repo references (ADR-025 D4):** convert the extension's `PackageReference Include="Dmon.Abstractions"` to a `ProjectReference` to `core/Dmon.Abstractions`; repair the test project's references.
- **Central Package Management:** move the provider's and test project's third-party pins (`Microsoft.Extensions.AI.OpenAI`, `Microsoft.Extensions.Configuration`, `Microsoft.Extensions.DependencyInjection`, xunit, etc.) into the root `Directory.Packages.props`; strip inline `Version=`; remove the satellite's standalone `<Version>0.2.0</Version>` and its vendored-nupkg `nuget.config` / `Directory.Packages.props` / local-feed plumbing in favour of the monorepo's MinVer + protocol skew-guard from the root `Directory.Build.props`; keep the provider packable, consistent with sibling providers.
- **Solutions:** add the provider + its test project to `providers.slnx` and `Everything.slnx`.
- The already-current `UseLlamaCpp` composition verb (namespace `Dmon.Hosting`, generic `IProviderRegistration`) travels unchanged.
- Record `dmon-llama-cpp` as absorbed/read-only (it is local-only — a note, no GitHub archival).

## Capabilities

### New Capabilities
- `llamacpp-provider`: the behavioural contract of the managed local llama.cpp provider — PATH-based applicability (never bundles a binary), managed `llama-server` lifecycle (free-port/readiness/kill-on-dispose), Hugging Face model acquisition by `-hf` delegation, an `IChatClient` over the OpenAI-compatible endpoint, probe-verified tool-calling, and the `UseLlamaCpp` builder verb that registers the provider and sets an overridable default model. Authored here as a spec delta (content lifted from the satellite's standing spec) and synced into `openspec/specs/llamacpp-provider/` on archive — the same pattern Phase 0 used for `monorepo-layout`. The satellite's own `openspec/` (its archived change + standing spec) is bookkeeping and is **not** carried into the monorepo; the capability enters `dmon-core` through this change.

### Modified Capabilities
<!-- None. The graft conforms to the existing `monorepo-layout` standing spec (role buckets, per-area `.slnx` + `Everything.slnx`, intra-repo `ProjectReference`, CPM, ADR-023 D3 naming) without changing any of its requirements. -->

## Impact

- **New projects:** `providers/Dmon.Providers.LlamaCpp/` (packable provider) and `test/Dmon.Providers.LlamaCpp.Tests/`, both carrying imported git history (blame/log preserved through the rename via `git mv` + filter-repo path-rename).
- **Build plumbing:** `Directory.Packages.props` gains the provider's third-party `PackageVersion` entries; `providers.slnx` + `Everything.slnx` gain two projects. No change to the root `Directory.Build.props`, skew-guard, or any other project.
- **Git history:** `dmon-core` gains the `dmon-llama-cpp` commit history under the new paths (one-time `--allow-unrelated-histories` merge).
- **No runtime, RPC, protocol, or session-storage behaviour change** to existing code; the provider is additive and applicable only on machines with a `llama-server` binary on `PATH`.
- **Tooling prerequisite:** the apply step requires `git-filter-repo` (resolvable via `uvx git-filter-repo` or `brew install git-filter-repo`); CI is unaffected (no workflow changes here).
- **Out of scope** (later/fast-follow per ADR-025 + Phase 0): dependency-aware path-filtered CI, the two-family release matrix, ADR-024 per-package tag prefixes / skew-guard patch-relax, and the **dmail graft** (which additionally needs the `IDmonExtension`→`IToolExtension` / `Dmon.Extensions`→`Dmon.Abstractions` API port).
