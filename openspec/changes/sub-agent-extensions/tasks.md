## 1. Scope ADR

- [x] 1.1 Write `docs/adrs/ADR-NNN-sub-agent-extensions.md` (next free number) recording: a tool extension running a scoped, single-turn in-process `IChatClient` is in scope; multi-agent orchestration (deferred in V1) = multiple `dmon-core` processes over stdio/RPC
- [x] 1.2 Get the ADR accepted; cross-reference it from the V1 "Out of scope" list in `CLAUDE.md` so the boundary is discoverable

## 2. Config-section convention (coordinate with extension-middleware-tier)

- [x] 2.1 Decide the command-extension config section path, harmonised with the middleware `middleware:<name>` convention (nested `extensions:commands:<name>` vs peer top-level `commands:<name>`); record the decision in design.md Open Questions
- [x] 2.2 Ensure a name-keyed command-extension section is parseable from the layered `IConfiguration` without the array-index collapse that affects the `extensions` list (`ExtensionsConfigReader`)
- [x] 2.3 Document the section convention alongside the existing extension config docs (ADR-009 area)

## 3. Guarantee the extension-facing IServiceProvider

- [x] 3.1 Confirm the `IServiceProvider` passed to extension constructors (`NuGetExtensionLoader`) resolves `IConfiguration` and `IEnumerable<IProviderFactory>`
- [x] 3.2 Add a loader test asserting a constructed extension can resolve both `IConfiguration` and the provider factories from its injected `IServiceProvider`

## 4. Verification

- [x] 4.1 `make build` clean (`TreatWarningsAsErrors`) and `make test` green
- [x] 4.2 `openspec validate sub-agent-extensions --strict`
- [x] 4.3 Confirm every scenario in the `sub-agent-extensions` spec is covered by a test or the accepted ADR
