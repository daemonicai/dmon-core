# DEVLOG: composition-root-facets

<!-- Implements ADR-022 + ADR-023: the Dmon.cs composition root becomes an open, faceted, DI-backed builder; an agent is its .cs; granular implementation packages. -->

## 1. Discovery & audit

- Read-only audit (Explore agent) produced the four worklists; full reference lists are in the agent output. Highlights and refinements below.
- **`IDmonExtension` is implemented by builtin tools too.** `Dmon.BuiltinTools/Tools/*` (ReadFile, Bash, Write, Glob, Edit, Fetch) and internal Core types (`ExtensionSearchTool`, `ExtensionReadmeTool`, `AnonymousExtension`) implement it — the Group 2 rename touches these, not just the SDK surface.
- **Confirmed: `Dmon.Core` has ZERO direct vendor-SDK imports.** The four vendor SDKs are `.csproj` PackageReferences only; all usage lives in `Dmon.Providers*` (factories). The split is clean (Worklist C). `AddDmonProviders` (`DaemonServiceExtensions.cs:32-50`) registers the four factories + `IProviderRegistry`; called from `DmonHostBuilder.Build()` ~line 208.
- **Confirmed: provider consumption is abstraction-only** (Worklist D) — setup wizard, `ModelListHandler`, `ProviderRegistry`, `ProviderSetupHandler` all go through `IProviderFactory`/`IProviderRegistry`. The package split won't break them.
- **The post-build manual loop** is `DmonHostBuilder.cs:255-265` (tool registry) + the middleware equivalent — these are what DI-discovery replaces (tasks 3.4).
- **Decision:** No deprecated shim for the deleted `Dmon.Extensions` package / renamed contract — clean break is sanctioned (no production deployments; no back-compat). Resolves the audit's Group-2 "published package" risk.
- **Profile is deeper than 'a builder verb'** (Worklist B): it threads through the **protocol/persisted surface** — `SessionMeta`, `SessionCommands`, `ControlFrames` (all carry `profile`), `TurnHandler` resolves it at runtime (~lines 37-38, 89-90, 121, 127), and the gateway pre-spawn-validates it (`GatewayConnectionEndpoint.cs:336-356`). Two consequences:
  - **`ISessionAssetProvisioner.Provision(AgentProfile, sessionId)` takes an `AgentProfile`** (being deleted) — its signature must change to take a path/flag from the `UseAssets` verb (Group 7).
  - **Likely spec gap:** the `profile`→`agent` rename touches `Dmon.Protocol` DTOs, which are governed by the `protocol-schema` standing spec — not currently in the change's capability set. A `protocol-schema` delta probably needs adding before Group 7 (the `remote-session-gateway` delta covers the control frame but not `SessionMeta`/`SessionCommands`).

## NEXT

- **Up next:** Group 2 — contracts collapse & rename (atomic, build-green). Brief the `worker` with Worklist A (incl. builtin-tool implementors) and the clean-break decision.
- **Open questions:**
  - Add a `protocol-schema` capability delta for `profile`→`agent` (SessionMeta/SessionCommands/ControlFrames)? Recommend yes — confirm with user.
  - `ISessionAssetProvisioner` new signature shape (path vs bool flag) — decide in Group 7.
- **Nits / deferred:** `test/Dmon.Extensions.Tests` retargets to `Dmon.Abstractions` (Group 2). `Dmon.ExtensionSmoke` + `Dmon.SampleExtension` samples update in Group 2/3.
- **Carry-forward:** Group 7 is the high-risk group (protocol + runtime + asset provisioner). Group 1 produced no code; its commit carries the DEVLOG + ticked boxes only.
