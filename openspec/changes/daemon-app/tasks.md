## 1. ADR-028 — daemon/ + services/ buckets, dcal rename, Swift in monorepo

- [x] 1.1 Write `docs/adrs/ADR-028-daemon-bucket.md`: amends ADR-025 D2/D10/D11; adds `daemon/` (Daemon composition: `Daemon.cs` + `Daemon.Routing` + `Daemon.App`/dmonium) and a new `services/` bucket (standalone backing servers that pair with a `tools/` extension — `services/Dcal`, future `services/Dmail`); records services as independently-versioned app artifacts (not NuGet/protocol-keyed); places `dmonium` in `daemon/Daemon.App` (not `frontends/`); renames the calendar capability to `dcal` (server `services/Dcal`, tool `tools/Dmon.Tools.Dcal`, specs `dcal-lookup`/`dcal-sync`); records Swift as a supported in-repo language built outside `Everything.slnx` via `make daemon-app`; resolves ADR-025 Open Question B (dmonium + Swift + Dmail-server home); keeps `middleware/` empty (ADR-027); notes `dmon-swift` placement remains open
- [x] 1.2 Update the ADR index in `CLAUDE.md` with the ADR-028 entry

## 2. services/ bucket and the dcal rename

- [x] 2.1 Create the `services/` bucket: a root `services.slnx` (matching the existing root-level area-solution convention — `core.slnx`, `tools.slnx`, `daemon.slnx`, …; there are no in-bucket `.slnx` files); add `services/README.md` documenting that `services/` holds standalone backing servers (app artifacts, independently versioned, not on the protocol-lockstep train). No per-area `Directory.Build.props` — the single root one applies (ADR-025 D8: per-area props only when settings diverge from root, which `services/Dcal` does not need)
- [x] 2.2 Move and rename the calendar server: `daemon/Daemon.Calendar/` → `services/Dcal/`; project `Daemon.Calendar.csproj` → `Dcal.csproj` (`AssemblyName`/`RootNamespace` → `Dcal`); rename namespaces `Daemon.Calendar` → `Dcal` in all source; remove the project from `Everything.slnx`'s `/daemon/` folder and add it under a `services/` entry referencing `services.slnx`
- [x] 2.3 Move and rename the server test project: `test/Daemon.Calendar.Tests/` → `test/Dcal.Tests/` (`Dcal.Tests.csproj`, namespace `Dcal.Tests`, `ProjectReference` → `services/Dcal/Dcal.csproj`); update its entry in `Everything.slnx`
- [x] 2.4 Rename the tool package: `tools/Dmon.Tools.Calendar/` → `tools/Dmon.Tools.Dcal/` (`PackageId`/`AssemblyName`/`RootNamespace` → `Dmon.Tools.Dcal`); rename the registration verb `AddCalendarAbilities()` → `AddDcalAbilities()` and the types `CalendarExtension`/`CalendarAbilityProvider` → `DcalExtension`/`DcalAbilityProvider`; the model-facing tool names (`lookup_calendar`, `list_upcoming_events`) and the `DCAL_*` env prefix are unchanged; update `tools.slnx` and `Everything.slnx`
- [x] 2.5 Rename the tool test project: `test/Dmon.Tools.Calendar.Tests/` → `test/Dmon.Tools.Dcal.Tests/`; update its `ProjectReference` and its `Everything.slnx` entry
- [x] 2.6 Rename the standing specs `openspec/specs/calendar-lookup/` → `dcal-lookup/` and `openspec/specs/calendar-sync/` → `dcal-sync/`, updating titles and body identifiers (`Daemon.Calendar` → `Dcal`, `CalendarExtension`/`CalendarAbilityProvider` → `DcalExtension`/`DcalAbilityProvider`, `AddCalendarAbilities` → `AddDcalAbilities`) while keeping the domain wording and tool names
- [x] 2.7 Verify the rename is complete: `make build` clean, `make test` green, and no remaining `Daemon.Calendar` / `Dmon.Tools.Calendar` / `AddCalendarAbilities` identifiers in source or solutions

## 3. Monorepo wiring for daemon/

- [ ] 3.1 The root `daemon.slnx` already exists; after 2.2 it no longer references the calendar server (moved to `services/`) — add `Daemon.Routing` to it in §4
- [ ] 3.2 Ensure the root `daemon.slnx` / `services.slnx` and `Everything.slnx` include all C# projects in both buckets
- [x] 3.3 Add a `Makefile` target `daemon-app` that runs `swift build -c release` from `daemon/Daemon.App/`; confirm `make build` (C# only) is unaffected
- [ ] 3.4 Add a `Directory.Build.props` stub under `daemon/` that sets `<RootNamespace>Daemon</RootNamespace>` and `<AssemblyName>Daemon.$(MSBuildProjectName)</AssemblyName>` defaults for C# projects in the bucket, chain-importing the root

## 4. Daemon.Routing — triage router policy

- [x] 4.1 Create `daemon/Daemon.Routing/Daemon.Routing.csproj` targeting net10.0 with `TreatWarningsAsErrors`, `Nullable enable`, `ImplicitUsings enable`; `PackageReference` for `Microsoft.Extensions.AI`; `ProjectReference` to `core/Dmon.Abstractions` (`ITerminalClientFactory`, `IAbilityProvider`) and `core/Dmon.Core` (`AbilityRegistry` only — `DaemonTelemetry` is defined in this project, see 4.4); add to the root `daemon.slnx` and `Everything.slnx`
- [x] 4.2 Define `enum Tier { Direct, Reasoner }`, `record RouteDecision(string Scope, Tier Tier, bool Impersonal, float Confidence)` (the classifier structured-output contract), and `record TriageOptions { float EgressThreshold = 0.8f }`
- [x] 4.3 Implement `sealed class TriageRouter : DelegatingChatClient` with ctor `(IChatClient classifier, IChatClient e2bWithTools, IChatClient reasoner, IChatClient egress, AbilityRegistry abilities, TriageOptions options)`
- [x] 4.4 Implement `GetResponseAsync`: (a) structured-output classify pass `classifier.GetResponseAsync<RouteDecision>`; (b) override effective scope to `"personal"` when `Confidence < options.EgressThreshold`; (c) set `ChatOptions.Tools = abilities.ForScope(effectiveScope)`; (d) dispatch per the R5 switch (egress when `"world"`+`Impersonal`+confident; else reasoner when `Tier.Reasoner`; else e2bWithTools); (e) increment `dmon.triage.misclassify.personal_to_world` via a `Daemon.Routing`-owned `DaemonTelemetry` static class (its own `Meter`+`Counter<long>`; core's `DmonTelemetry` is unrelated and unchanged) when the gate overrides `"world"`→`"personal"`
- [x] 4.5 Implement `GetStreamingResponseAsync`: classify, yield one fixed route-dependent ack `ChatResponseUpdate`, then forward the target's streaming response with the same scope-gated manifest
- [x] 4.6 Implement `UseTriage(this IDmonHostBuilder b, IChatClient e2bRaw, TriageOptions? options = null)` in the `Dmon.Hosting` namespace — builds `classifier = e2bRaw` and `e2bWithTools = e2bRaw.AsBuilder().UseFunctionInvocation().Build()`, registers a `TriageRouterFactory : ITerminalClientFactory` (and `TriageOptions`) in DI
- [x] 4.7 Implement `AddReasoner(this IDmonHostBuilder b, IChatClient reasoner)` and `AddEgress(this IDmonHostBuilder b, IChatClient egress)` in `Dmon.Hosting` — register the reasoner / egress backends for `TriageRouterFactory` to resolve (egress is an explicit `IChatClient`; the router references no provider package — R6)
- [x] 4.8 Implement `TriageRouterFactory : ITerminalClientFactory` — `Create(services)` resolves the e2b/classifier/reasoner/egress backends + `AbilityRegistry` + `TriageOptions` from DI and constructs `TriageRouter`

## 5. Daemon.cs composition root

- [x] 5.1 Create `daemon/Daemon.cs` as an ADR-019 file-based program referencing `Daemon.Routing` and `tools/Dmon.Tools.Dcal`; wire `UseTriage(e2bClient)`, `AddReasoner(reasonerClient)`, `AddEgress(egressClient)`, `AddDcalAbilities()`, `AddToolExtension<DmailExtension>()`, and `AddDmonMemory()`
- [x] 5.2 Read e2b endpoint from config/env (`DCAL_E2B_URL`, default `http://localhost:11434`), reasoner endpoint (`DCAL_REASONER_URL`, default `http://localhost:8080/v1`), and the egress client (Gemini over its OpenAI-compatible endpoint, key from `GEMINI_API_KEY`); no credentials or endpoints hardcoded as literals
- [x] 5.3 Verify `Daemon.cs` compiles cleanly against Phase 1 + `tools/Dmon.Tools.Dcal` + `Daemon.Routing` via `dotnet build daemon/Daemon.cs -c Release` (a file-based program is NOT a `.csproj` and is NOT added to any `.slnx`; build the file directly per ADR-019 — run `make build` first if the `0.2.*` package feed needs populating); this is a compile-time integration check

## 6. Daemon.App Swift Package scaffold

- [x] 6.1 Create `daemon/Daemon.App/Package.swift` as a Swift Package targeting macOS 14; product: executable `DaemonApp`; no external Swift dependencies for this milestone
- [x] 6.2 Create `daemon/Daemon.App/Sources/DaemonApp/DaemonApp.swift` — `@main struct DaemonApp: App` with `MenuBarExtra` scene and `Settings` scene stubs
- [x] 6.3 Confirm `swift build -c release` from `daemon/Daemon.App/` produces a binary; confirm `make daemon-app` wraps it correctly

## 7. GatewayManager — process lifecycle

- [x] 7.1 Implement `GatewayManager` (Swift `ObservableObject`) with `start()`, `stop()`, and `restart()` methods using `Foundation.Process`; pass `--agent daemon/Daemon.cs` to the Gateway binary
- [x] 7.2 Implement `terminationHandler` that reschedules `start()` with exponential back-off (2s initial, 2× each retry, 60s cap)
- [x] 7.3 Implement Gateway re-adoption on app launch: check for a PID file at `~/.dmon/run/gateway.pid`; if the process is alive, adopt it instead of spawning a new one; write PID file on spawn
- [x] 7.4 Resolve Gateway binary path: check the `DMON_GATEWAY_PATH` env var first; fall back to the .NET global tool path (`~/.dotnet/tools/Dmon.Gateway`); surface a settings field for manual override

## 8. TailscaleMonitor

- [x] 8.1 Implement `TailscaleMonitor` (Swift `ObservableObject`) that runs `tailscale status --json` via `Foundation.Process` every 30 seconds on a background `DispatchQueue`
- [x] 8.2 Parse the JSON output to determine: up (Self node present, BackendState == "Running"), degraded (running but no peers), down (error or binary not found)
- [x] 8.3 Publish `TailscaleStatus` enum (`up`, `degraded`, `down`) as an `@Published` property consumed by `MenuBarView`

## 9. MenuBarView and status icon

- [ ] 9.1 Implement `MenuBarView` as a SwiftUI `View` for the `MenuBarExtra` content: show Gateway status, Tailscale status, last sync time (from `GET /health` on the Dcal server); buttons: Start/Stop Gateway, Sync Calendar Now, Open Settings
- [ ] 9.2 Implement status icon: SF Symbol `brain` (or `circle.fill`) tinted green/amber/red based on combined Gateway + Tailscale status; use `MenuBarExtra(isInserted:)` to show/hide the indicator when the app is hidden
- [ ] 9.3 Wire `GatewayManager` and `TailscaleMonitor` as `@StateObject` into the App and pass as `@EnvironmentObject` to views

## 10. Settings panel

- [ ] 10.1 Implement `SettingsView` as a SwiftUI `Settings` scene with sections: Inference (e2b URL, reasoner URL, Gemini key), Calendar (iCal URL, API key, sync interval, recurrence horizon), Email (Dmail URL, API key), Advanced (confidence threshold, Gateway path)
- [ ] 10.2 Store API keys in macOS Keychain via `Security.framework` (`SecItemAdd`/`SecItemUpdate`/`SecItemCopyMatching`); store non-secret settings in `~/.dmon/config.yaml`; confirm unsigned dev builds fall back to config.yaml with a visible warning
- [ ] 10.3 On settings save: write `~/.dmon/config.yaml`; call `GatewayManager.restart()` after showing a confirmation alert ("Saving will restart the Daemon session. Continue?")
- [ ] 10.4 Implement login item toggle using `SMAppService.mainApp.register()` / `.unregister()`; default to registered on first launch; persist toggle state in `UserDefaults`

## 11. Routing tests

- [x] 11.1 Add `test/Daemon.Routing.Tests/` xUnit project; add to solution files
- [x] 11.2 Test `TriageRouter.GetResponseAsync` routing invariants with a fake `IChatClient` classifier: `"personal"`/`Direct` → e2bWithTools; `"world"`/`Impersonal`/high-confidence → egress; `Tier.Reasoner` → reasoner; low-confidence `"world"` overridden to `"personal"`
- [x] 11.3 Test the privacy gate: `ChatOptions.Tools` passed to the dispatched client contains no `"world"`-scope tools on a `"personal"` turn, and no `"personal"`-scope tools on a `"world"` turn
- [x] 11.4 Test the misclassification counter: `dmon.triage.misclassify.personal_to_world` increments exactly once when the gate overrides `"world"`→`"personal"`, and not on a confident `"personal"` classification
- [x] 11.5 Test the terminal-client hook: a builder with `UseTriage(...)`/`AddReasoner(...)`/`AddEgress(...)` produces a `TriageRouter` as the terminal client
