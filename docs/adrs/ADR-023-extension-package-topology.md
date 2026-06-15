# ADR-023: Granular Implementation Packages and Extension Distribution Topology

**Date:** 2026-06-15
**Status:** Accepted
**Builds on:** ADR-011 (granular contract packages D1, protocol-keyed versioning D5, `agentReady` skew gate D6, cadence-independence D8/D9), ADR-019 (file-based-program composition, `#:package` restore), ADR-022 (registration facets, Option-B provider symmetry, contracts collapsed into `Dmon.Abstractions`).
**Amends:** ADR-011 — extends its "granular contract packages" principle (D1) to *implementation* packages, and completes the picture begun when ADR-019 superseded ADR-011 D2–4: with the runtime downloader gone, distribution is `dotnet restore` over a `#:package` set, and the set is now granular per provider/tool/middleware.

## Context

ADR-022 made the composition root open and chose **Option B** — every provider, tool, and middleware is *composed* by the user, nothing is baked. But today's package split assumes the opposite: `Dmon.Core` (`dmoncore`) directly references the vendor SDKs (`Anthropic`, `GeminiDotnet`, `Microsoft.Extensions.AI.OpenAI`, `OllamaSharp`) and pulls `Dmon.Providers`, `Dmon.Providers.Ollama`, and `Dmon.BuiltinTools` as `PrivateAssets="all"` — sealed inside the engine. "Nothing is baked" only delivers its promise (a minimal agent restores minimal dependencies) if those concrete pieces become *separately referenceable packages*. This ADR fixes the package topology, naming, versioning, and the authoring norms that follow.

## Decision

1. **`dmoncore` is a vendor-SDK-free engine.** The concrete provider factories and their vendor SDKs leave `Dmon.Core`; `AddDmonProviders()` is deleted (replaced by per-package `Use<Provider>` verbs + DI-discovery, ADR-022 D5/D7). `dmoncore` retains only the engine — turn loop, RPC, session storage, permission/middleware pipelines, registries, the hosting builder — referencing only the contracts (`Dmon.Abstractions`, `Dmon.Protocol`). It compiles against no vendor SDK; the setup wizard, `IProviderRegistry`, and model listing already operate on `IProviderFactory`/`IProviderExtension` abstractly.

2. **Each provider, tool, and middleware ships as its own granular implementation package.** `Dmon.Providers.Anthropic`, `Dmon.Providers.OpenAI`, `Dmon.Providers.Gemini`, `Dmon.Providers.Ollama`, `Dmon.Tools.Builtin`, `Dmon.Memory`, … — each references `Dmon.Abstractions` plus whatever heavy SDK it needs, and ships its own fluent verb (Decision 4). A llama-only agent never restores the OpenAI SDK; first-party and third-party packages are structurally identical (a `Dmon.Providers.Anthropic` is the same shape as someone's `Acme.Dmon.Llama`).

3. **Naming convention.** First-party families: `Dmon.Providers.<Name>`, `Dmon.Tools.<Name>`, `Dmon.Middleware.<Name>`. Third-party is *recommended* (not enforced) as `<Owner>.Dmon.<Name>`. With the skill marketplace out of scope (V1 brief), this convention is the only discoverability lever — it serves nuget.org search and human legibility, nothing more.

4. **Fluent verbs ship in one well-known namespace.** Every package's `Use*`/`Add*` verb lives in the `Dmon.Hosting` namespace (the analogue of ASP.NET's `Microsoft.Extensions.DependencyInjection` convention), which an authored `Dmon.cs` already imports via `using Dmon.Hosting;`. Adding a capability is then exactly: a `#:package` line + the verb appears — no extra `using`. This is a binding authoring convention for first-party packages and the documented recommendation for third-party ones.

5. **Versioning is lockstep on the protocol line.** The contract packages (`Dmon.Protocol`, `Dmon.Abstractions`) and all first-party implementation packages move on `dmoncore`'s protocol-keyed `Major.Minor` line (ADR-011 D5), so an authored `Dmon.cs` pins `@<protocol>.*` across the board and NuGet resolves one coherent graph. The file-based-program model adds a property runtime acquisition lacked: an **incompatible combination fails at `dotnet restore`/build**, before any process starts — the `agentReady` `protocolVersion` gate (ADR-011 D6) survives as a backstop for the *prebuilt stock* path (Decision 8). Third-party packages pin `Dmon.Abstractions@X.Y.*` and version on their own cadence.

6. **Builtin tools are one package, scaffolded-but-removable.** `Dmon.Tools.Builtin` exposes `.AddBuiltinTools()` and is included in the scaffolded `Dmon.cs`, but it is genuinely opt-in: an agent with no filesystem/bash tools is a legitimate locked-down composition — itself a capability the split unlocks. Splitting further (fs / bash / web as separate packages) is deferred (Open Question A).

7. **Sub-agent tools are provider-agnostic by default.** The base form of a sub-agent tool takes `Action<IProviderRegistration>` (ADR-022 D6), so the **user's** `Dmon.cs` supplies the provider and carries that provider package's reference — the base tool package drags no provider SDK. A package **may** additionally ship a convenience verb that bundles a specific provider (`AddAgentWebSearch("gemini-…")`), which then takes a dependency on that provider package (`Dmon.Providers.Gemini`). The norm: agnostic base + optional batteries-included convenience, never a base tool that forces an unwanted provider SDK.

8. **The stock default core is a batteries-included prebuilt `Dmon.cs`.** ADR-019 Decision 9's no-SDK stock default is a prebuilt framework-dependent publish of a canonical `Dmon.cs` that `#:package`s `dmoncore` + the cloud provider packages + `Dmon.Tools.Builtin`. It bundles the common set so an empty directory "just works"; a hand-authored `Dmon.cs` pulls only what it names. One mechanism, two build-times (ADR-019 D9), now over a granular package set.

## Consequences

- **The feature's promise is delivered: minimal agents pull minimal deps.** Dependency weight tracks composition. This is the whole point of Option B and is impossible under a monolithic `dmoncore`.
- **`dmoncore` gets materially lighter and vendor-free.** Its `.csproj` sheds four vendor SDK references and three `PrivateAssets="all"` project refs; the engine no longer transitively imposes any provider's dependency graph.
- **More packages to publish and version.** ~4 cloud providers + builtin tools + memory + future locals, all on the protocol-keyed line. The lockstep rule (Decision 5) keeps this mechanical: one version bump moves the first-party set together.
- **Restore-time failure replaces a class of runtime failure.** A protocol-incompatible `#:package` set won't build, which is strictly better than discovering it at `agentReady`. The runtime gate remains only for the prebuilt stock path.
- **An explicit, honest dependency graph for sub-agent tools.** A tool that bundles a provider declares it; an agnostic tool declares nothing — the package manifest tells the truth about what each tool drags in.

## Alternatives

- **(i) Composed-but-in-package** — keep the factories and `Use*` verbs *inside* `dmoncore`; "not baked" means "the default `Dmon.cs` must call them." Rejected: one `#:package dmoncore` still restores every vendor SDK, so a minimal agent stays heavy — the feature's core promise is unmet. (This was the explicit fork; (ii)/this ADR won.)
- **Per-facet contract packages** (`Dmon.Providers.Abstractions`, `Dmon.Tools.Abstractions`, …). Rejected in ADR-022 D12: with the weight in implementation packages, fragmenting *contracts* buys only weightless interface separation while costing package/versioning ceremony, and the tool→provider boundary is already crossed by sub-agent tools.
- **Independent (non-lockstep) versioning for first-party packages.** Rejected for the first-party set: it multiplies the compatibility matrix with no benefit when they ship together; third-party packages, which *do* have independent cadence, pin `@X.Y.*` instead.

## Open Questions

- **A. Builtin-tools granularity.** Whether `Dmon.Tools.Builtin` later splits into `fs` / `bash` / `web` packages for finer lock-down. Deferred — one package now; splitting is additive.
- **B. A shared provider-infrastructure package.** If the per-provider factories accrue common code beyond what `Dmon.Abstractions` (`CapabilitiesDecorator`, `ProviderConfig`) already supplies, a small `Dmon.Providers.Core` shared base may be warranted. Defer until duplication is real.
- **C. Memory as composition.** `Dmon.Memory` becomes an implementation package behind `UseMemory<…>`/`AddDmonMemory`; confirm whether the short-term store is always-on (like builtin tools) or fully opt-in in the scaffold.

## Relationship to other ADRs

- **ADR-011** — its granular-contract-package principle (D1) and protocol-keyed versioning (D5/D6) are extended to implementation packages; D2–4 (runtime acquisition) were already superseded by ADR-019, and this ADR finishes that arc with a granular `#:package` distribution model.
- **ADR-019** — the file-based-program composition and stock-default packaging (D9) are unchanged; this ADR makes the package *set* granular and moves vendor weight out of `dmoncore`.
- **ADR-022** — supplies the verbs (`Use*`/`Add*`), the facets, and the contracts collapse; this ADR is the distribution side of the same feature. Decision 7 here pins the sub-agent-coupling norm for ADR-022 D6.
- **ADR-005 / ADR-007** — provider credentials and lifecycle are unchanged; provider *packaging* changes only.
