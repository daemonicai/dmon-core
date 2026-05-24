# Proposal: Extension Ecosystem

**Slug:** extension-ecosystem  
**Status:** Draft  
**Date:** 2026-05-24  

## Summary

Add built-in tools for discovering, inspecting, and securely loading NuGet-based dmon extensions. The extension ecosystem uses NuGet as its registry — no marketplace infrastructure required. Extensions are discoverable via the `dmon-extension` tag. The install gate enforces source availability and runs an LLM-assisted security analysis before loading any extension into the session.

## Motivation

ADR-002 defines the extension model (`.csx` scripts and NuGet packages via `IDaemonExtension`) but leaves discovery and secure installation unspecified. Users and the agent have no built-in way to find extensions, and the permission gate (ADR-006) treats extension loading as a high-risk prompt with no security analysis. This change closes both gaps.

## Decisions made

### NuGet as the registry

Extensions are published to nuget.org (or a configured private feed) and tagged `dmon-extension`. This tag is the opt-in signal that a package implements `IDaemonExtension` correctly. No separate marketplace is built or maintained.

### Source availability is a hard requirement

To be loadable, a package must have source available via either embedded source in a `.snupkg` symbol package or Source Link pointing to a reachable repository. Packages without source cannot be analysed and cannot be loaded. This is not configurable.

### Three built-in tools

`extension.search`, `extension.readme`, and `extension.load` form the extension lifecycle surface. They are composable: search returns a ranked shortlist; readme provides a drill-down when descriptions are ambiguous; load runs the full security pipeline.

### Smart tools save tokens

Built-in tools should do filtering, ranking, and summarisation internally — returning curated output rather than raw API data. This reduces the token cost of agentic reasoning over tool results. `extension.search` embodies this principle: it returns a ranked shortlist of ≤5 results, not a raw NuGet API dump.

### GitHub signals via `gh` CLI

If the `gh` CLI is installed and authenticated (`gh auth status` succeeds), GitHub signals (stars, last push, archived status) enrich search results and unlock `extension.readme`. If `gh` is not available, the tools degrade gracefully to NuGet signals only (downloads, updated timestamp). No `GITHUB_TOKEN` management is required — `gh` handles auth.

### Security analysis before load

The install gate in ADR-006 has been extended with a mandatory pipeline: source fetch → LLM security analysis → report → user confirmation → install. The confirmation prompt is never shown before the analysis report.

## Out of scope

- Private feed configuration (deferred — default is nuget.org)
- Transitive dependency analysis (report notes this limitation explicitly)
- A `extension.unload` command (session-scoped; extensions are unloaded on session end)
- README rendering / markdown display (tool returns raw text excerpt)
- Automated extension updates
