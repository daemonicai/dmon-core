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

## 2. Contracts collapse & rename

- Renamed `IDmonExtension`→`IToolExtension` (shape byte-for-byte identical, both default methods preserved) and moved it, `IDmonMiddleware`, `DmonMiddlewareAttribute`, `DmonAIFunctionFactory` into `Dmon.Abstractions` (namespaces `Dmon.Abstractions.Extensions` and `Dmon.Abstractions.Hosting`). `Dmon.Extensions` project deleted, removed from `Dmon.slnx`, all refs repointed.
- Declared the three facets + `IDmonHostBuilder` (`{ Services, Configuration }`, aggregating them) + `IChatClientFactory` as **contracts only** — facets are empty markers this group; verbs/wiring are Group 3.
- **Decision:** `Dmon.Abstractions` references the **full** `Microsoft.Extensions.Configuration` (not just `.Abstractions`) because `IConfigurationManager` (the type `IDmonHostBuilder.Configuration` exposes, per ADR-022 D2) lives there — the `.Abstractions` package only has `IConfiguration`/`IConfigurationBuilder`. Added a **direct** `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.8 ref (was resolving only transitively via M.E.AI) so the contract package declares its own public-surface deps. Both pinned 10.0.8 to match repo convention.
- **Scope held:** `DmonHostBuilder` verbs (`AddExtension`/`AddMiddleware`/`WithModel`/`WithProfile`) and post-build registry loops left UNCHANGED (only the param *type* swapped) — verb renames + DI-discovery deferred to Group 3.
- **Env gotcha (not a defect):** the `ComposedCoreFeedFixture` composition tests failed locally because a pre-change `dmon.* 0.2.0` sat in `~/.nuget/packages` and shadowed the freshly-packed feed (same version, stale content). Cleared `~/.nuget/packages/dmon.*`; passes. On clean CI this can't happen (no prior 0.2.0). Worth a durable fix later (isolate the global-packages folder during pack/compose) — see NEXT.
- **Reviewer:** CHANGES REQUESTED → fixed. Blocker was `scripts/smoke-sdk.sh` still packing the deleted `Dmon.Extensions` (gates don't run it, so it stayed green) — removed; smoke script re-run PASS. Plus stale-comment nits.

## 3. Faceted builder, verb grammar & DI-discovery

- `DmonHostBuilder` implements `IDmonHostBuilder` (exposes `Services`/`Configuration` via `new`-shadowed members resolving the three facets' `Services`); parameterless `DmonHost.CreateBuilder()` added.
- **Two-tier verb design** (intentional, documented): concrete single-type-arg methods ON `DmonHostBuilder` (Core, `namespace Dmon.Hosting`) return `DmonHostBuilder` for unbroken flat chaining in `Dmon.cs`; self-type generic + **instance** extension methods on the facets (Abstractions, `namespace Dmon.Hosting`, `DmonRegistrationExtensions.cs`) serve package authors / bare facets. C# can't make a single self-type method return the concrete type when called on the builder, hence two tiers. `UseModel<T>`, `AddProvider<T>(this T, IProviderExtension)` etc. are self-type and chainable.
- **DI-discovery replaces post-build loops:** `Add*` verbs register DI singletons (`IToolExtension`, `IProviderExtension`, and a `MiddlewareRegistration` record carrying instance+priority); `Build()` enumerates them into `IToolRegistry`/`IMiddlewareRegistry`/`IProviderRegistry.RegisterExtensionAsync` (gated by `IsApplicable`). The `_extensions`/`_middlewares` lists and their post-build loops are gone. `TryAdd` stdio ordering + permission-mode/profile decorators preserved.
- **Builtins:** still registered by `BuiltinToolsInitializer` (`IHostedService`) at `StartAsync` — a separate path from DI-discovery, into the same singleton `IToolRegistry`. Interim until Group 8's `AddBuiltinTools()`. Verified present by test.
- Renames `AddExtension`→`AddToolExtension`, `WithModel`→`UseModel` (clean break; callers/samples/tests updated). 8 new verb tests (flat + bare-facet chaining, DI-constructed tool, parameterless CreateBuilder).
- **Reviewer:** APPROVE with nits → removed three uncallable two-type-param facet overloads (C# partial-inference dead-ends); added a `<remarks>` note steering DI-constructed registration to the concrete builder methods / `Services`.
- **Provider Use-verb path CONFIRMED for Group 4:** `static T UseGemini<T>(this T r, string m) where T : IProviderRegistration => r.AddProvider(new GeminiProvider()).UseModel("gemini", m);` compiles against the Abstractions-only surface.

## NEXT

- **Up next:** Group 4 — provider split & Option-B symmetry. Extract `Dmon.Providers.<Name>` packages (vendor SDK + `Use<Provider>` verb in `Dmon.Hosting`), strip vendor refs + delete `AddDmonProviders` from `Dmon.Core`, update `default-core/Dmon.cs` to compose providers explicitly.
- **Open questions:**
  - **Sync-over-async at `DmonHostBuilder.Build():~292`** — `RegisterExtensionAsync(...).GetAwaiter().GetResult()`. A no-op for the stock core today, but Group 4 ships real `IProviderExtension`s; if any does real I/O in `RegisterExtensionAsync`, reconsider an `async BuildAsync` (and thread a `CancellationToken`). Decide in Group 4.
  - `protocol-schema` delta for `profile`→`agent` — author before Group 7.
  - `ISessionAssetProvisioner` new signature (path vs flag) — Group 7.
  - Durable NuGet stale-cache fixture fix — consider Group 8.
- **Carry-forward:** Pacing = run Groups 4–6, pause before Group 7 and before Group 8. Clear `~/.nuget/packages/dmon.*` before composition tests on this machine.
