# Tasks: Extension Ecosystem

## Group 1 — Spike: source availability detection

**Goal:** Establish which NuGet API fields indicate source availability and validate the source-fetch mechanism. Spike result: secondary nuspec request is required; `.snupkg` path is infeasible for V1; nuspec `<repository>` + commit-SHA fetch is the V1 mechanism.

- [x] Spike: query NuGet search API for a known source-available package; document which response fields indicate source availability (`<repository>`, `packageTypes`, or other)
- [x] Spike: download a `.snupkg`, extract a `.pdb`, confirm embedded source extraction is feasible with available .NET APIs
- [x] Spike: confirm Source Link fallback path via `gh api` for a known package
- [x] Document findings in `openspec/changes/extension-ecosystem/spike-source-availability.md`
- [x] If `.snupkg` extraction is not feasible in V1: document the Source Link-only path and update design.md accordingly

## Group 2 — gh capability probe

**Goal:** Implement the lazy `gh` availability check used by all three tools.

- [x] Add `IGhCliService` to `Daemon.Core` with `Task<bool> IsAvailableAsync(CancellationToken)`
- [x] Implement `GhCliService`: runs `gh auth status`, caches result for session lifetime
- [x] Register `IGhCliService` in DI
- [x] Unit tests: available, not installed, not authenticated

## Group 3 — extension.search

**Goal:** Implement the search tool with NuGet + optional GitHub enrichment.

- [x] Implement `NuGetSearchService`: queries nuget.org search API, filters by `dmon-extension` tag and source availability
- [x] Implement ranking formula (downloads, stars, recency) with graceful degradation when gh unavailable
- [x] Implement GitHub enrichment via `IGhCliService`: `gh api /repos/{owner}/{repo}`
- [x] Implement `ExtensionSearchTool` (`IDaemonExtension` / `AIFunction`), returning curated formatted output (≤5 results, `readme_available` flag)
- [x] Register as built-in tool
- [x] Unit tests: ranking, graceful degradation (no gh), archived repo exclusion, tag filter

## Group 4 — extension.readme

**Goal:** Implement the README fetch tool.

- [x] Implement `ExtensionReadmeTool`: fetches README via `gh api /repos/{owner}/{repo}/readme`, strips badges, returns ~500 char excerpt
- [x] Error handling: gh unavailable, no repository URL, 404
- [x] Register as built-in tool
- [x] Unit tests: badge stripping, truncation, error paths

## Group 5 — extension.load security pipeline

**Goal:** Wrap existing extension loading with the source-fetch → analysis → report → confirm pipeline.

- [ ] Implement `IExtensionSourceFetcher`: downloads `.nupkg`, parses nuspec `<repository url commit>`, fetches `.cs` source files at recorded commit SHA (raw.githubusercontent.com for public repos; `gh` CLI for private repos); hard-blocks if `<repository>` is absent or fetch fails
- [ ] Implement `ExtensionSecurityAnalyser`: calls LLM with security-analysis prompt, returns structured `SecurityAnalysisReport`
- [ ] Implement report formatter: renders findings to user-facing text (✅ / ⚠️ / 🚨 levels)
- [ ] Update `ExtensionLoadTool` to run the full pipeline before the ADR-006 confirmation prompt
- [ ] Ensure "allow for project" / "allow globally" stores `package@version` key; version bump resets approval
- [ ] Define and enforce a source-fetch volume bound (cap candidates before nuspec fan-out in search; cap file count / total bytes fed to the security analyser)
- [ ] Clarify HTTP-tier permissioning: state explicitly whether pipeline fetches to `api.nuget.org`, `api.github.com`, and `raw.githubusercontent.com` are exempt from ADR-006 per-domain approval or implicitly approved by the extension-loading gate
- [ ] Integration test: full pipeline against a real (test) extension package
- [ ] Unit tests: source not available → hard block; analysis findings → correct risk levels

## Group 6 — README design principle

**Goal:** Document the "smart tools save tokens" principle.

- [ ] Add a `## Tool design principles` section to `README.md` (or `docs/contributing-extensions.md` if that exists) documenting: tools should return curated output, not raw API data; this reduces agentic reasoning cost
- [ ] Add a note to the extension authoring guide (when written) about providing good `Description` metadata
