# Tasks: Extension Ecosystem

## Group 1 — Spike: source availability detection

**Goal:** Establish which NuGet API fields indicate source availability without a per-package secondary request, and validate the `.snupkg` → embedded source extraction path.

- [ ] Spike: query NuGet search API for a known source-available package; document which response fields indicate source availability (`<repository>`, `packageTypes`, or other)
- [ ] Spike: download a `.snupkg`, extract a `.pdb`, confirm embedded source extraction is feasible with available .NET APIs
- [ ] Spike: confirm Source Link fallback path via `gh api` for a known package
- [ ] Document findings in `openspec/changes/extension-ecosystem/spike-source-availability.md`
- [ ] If `.snupkg` extraction is not feasible in V1: document the Source Link-only path and update design.md accordingly

## Group 2 — gh capability probe

**Goal:** Implement the lazy `gh` availability check used by all three tools.

- [ ] Add `IGhCliService` to `Daemon.Core` with `Task<bool> IsAvailableAsync(CancellationToken)`
- [ ] Implement `GhCliService`: runs `gh auth status`, caches result for session lifetime
- [ ] Register `IGhCliService` in DI
- [ ] Unit tests: available, not installed, not authenticated

## Group 3 — extension.search

**Goal:** Implement the search tool with NuGet + optional GitHub enrichment.

- [ ] Implement `NuGetSearchService`: queries nuget.org search API, filters by `dmon-extension` tag and source availability
- [ ] Implement ranking formula (downloads, stars, recency) with graceful degradation when gh unavailable
- [ ] Implement GitHub enrichment via `IGhCliService`: `gh api /repos/{owner}/{repo}`
- [ ] Implement `ExtensionSearchTool` (`IDaemonExtension` / `AIFunction`), returning curated formatted output (≤5 results, `readme_available` flag)
- [ ] Register as built-in tool
- [ ] Unit tests: ranking, graceful degradation (no gh), archived repo exclusion, tag filter

## Group 4 — extension.readme

**Goal:** Implement the README fetch tool.

- [ ] Implement `ExtensionReadmeTool`: fetches README via `gh api /repos/{owner}/{repo}/readme`, strips badges, returns ~500 char excerpt
- [ ] Error handling: gh unavailable, no repository URL, 404
- [ ] Register as built-in tool
- [ ] Unit tests: badge stripping, truncation, error paths

## Group 5 — extension.load security pipeline

**Goal:** Wrap existing extension loading with the source-fetch → analysis → report → confirm pipeline.

- [ ] Implement `IExtensionSourceFetcher`: tries `.snupkg` embedded source, falls back to Source Link via gh
- [ ] Implement `ExtensionSecurityAnalyser`: calls LLM with security-analysis prompt, returns structured `SecurityAnalysisReport`
- [ ] Implement report formatter: renders findings to user-facing text (✅ / ⚠️ / 🚨 levels)
- [ ] Update `ExtensionLoadTool` to run the full pipeline before the ADR-006 confirmation prompt
- [ ] Ensure "allow for project" / "allow globally" stores `package@version` key; version bump resets approval
- [ ] Integration test: full pipeline against a real (test) extension package
- [ ] Unit tests: source not available → hard block; analysis findings → correct risk levels

## Group 6 — README design principle

**Goal:** Document the "smart tools save tokens" principle.

- [ ] Add a `## Tool design principles` section to `README.md` (or `docs/contributing-extensions.md` if that exists) documenting: tools should return curated output, not raw API data; this reduces agentic reasoning cost
- [ ] Add a note to the extension authoring guide (when written) about providing good `Description` metadata
