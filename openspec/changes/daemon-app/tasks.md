## 1. ADR-028 — daemon/ bucket and Swift in monorepo

- [ ] 1.1 Write `docs/adrs/ADR-028-daemon-bucket.md`: supersedes ADR-025 D11's "keep separate" for `daemon`; folds `daemon/` into the monorepo as a named bucket for the Daemon personal assistant agent; establishes `daemon/Daemon.App` as the `dmonium` macOS app (resolving ADR-025 Open Question B for this component); records that Swift Packages in `daemon/` are built outside `Everything.slnx` via `make daemon-app`; records that the Daemon's triage router (`daemon/Daemon.Routing`) is application policy living in `daemon/`, NOT a first-party `middleware/` package (the `middleware/` bucket stays empty until a real `IDmonMiddleware` ships); notes `dmon-swift` placement remains a separate open question
- [ ] 1.2 Update the ADR index in `CLAUDE.md` with ADR-028 entry

## 2. Monorepo wiring for daemon/

- [ ] 2.1 Confirm `daemon/daemon.slnx` exists (created by Phase 2 calendar-tool) and references `Daemon.Calendar`; if Phase 2 has not landed yet, create `daemon/daemon.slnx` as a placeholder
- [ ] 2.2 Add `daemon/daemon.slnx` to `Everything.slnx` so C# projects in `daemon/` are part of the full solution build
- [ ] 2.3 Add a `Makefile` target `daemon-app` that runs `swift build -c release` from `daemon/Daemon.App/`; confirm `make build` (C# only) is unaffected
- [ ] 2.4 Add a `Directory.Build.props` stub under `daemon/` that sets `<RootNamespace>Daemon</RootNamespace>` and `<AssemblyName>Daemon.$(MSBuildProjectName)</AssemblyName>` defaults for C# projects in the bucket

## 3. Daemon.Routing — triage router policy

- [ ] 3.1 Create `daemon/Daemon.Routing/Daemon.Routing.csproj` targeting net10.0 with `TreatWarningsAsErrors`, `Nullable enable`, `ImplicitUsings enable`; `PackageReference` for `Microsoft.Extensions.AI`; `ProjectReference` to `core/Dmon.Abstractions` (`ITerminalClientFactory`, `IAbilityProvider`) and `core/Dmon.Core` (`AbilityRegistry`, `DaemonTelemetry`); add to `daemon/daemon.slnx` and `Everything.slnx`
- [ ] 3.2 Define `enum Tier { Direct, Reasoner }`, `record RouteDecision(string Scope, Tier Tier, bool Impersonal, float Confidence)` (the classifier structured-output contract), and `record TriageOptions { float EgressThreshold = 0.8f }`
- [ ] 3.3 Implement `sealed class TriageRouter : DelegatingChatClient` with ctor `(IChatClient classifier, IChatClient e2bWithTools, IChatClient reasoner, IChatClient egress, AbilityRegistry abilities, TriageOptions options)`
- [ ] 3.4 Implement `GetResponseAsync`: (a) structured-output classify pass `classifier.GetResponseAsync<RouteDecision>`; (b) override effective scope to `"personal"` when `Confidence < options.EgressThreshold`; (c) set `ChatOptions.Tools = abilities.ForScope(effectiveScope)`; (d) dispatch per the R5 switch (egress when `"world"`+`Impersonal`+confident; else reasoner when `Tier.Reasoner`; else e2bWithTools); (e) increment `dmon.triage.misclassify.personal_to_world` via `DaemonTelemetry` when the gate overrides `"world"`→`"personal"`
- [ ] 3.5 Implement `GetStreamingResponseAsync`: classify, yield one fixed route-dependent ack `ChatResponseUpdate`, then forward the target's streaming response with the same scope-gated manifest
- [ ] 3.6 Implement `UseTriage(this IDmonHostBuilder b, IChatClient e2bRaw, TriageOptions? options = null)` in the `Dmon.Hosting` namespace — builds `classifier = e2bRaw` and `e2bWithTools = e2bRaw.AsBuilder().UseFunctionInvocation().Build()`, registers a `TriageRouterFactory : ITerminalClientFactory` (and `TriageOptions`) in DI
- [ ] 3.7 Implement `AddReasoner(this IDmonHostBuilder b, IChatClient reasoner)` and `AddEgress(this IDmonHostBuilder b, IChatClient egress)` in `Dmon.Hosting` — register the reasoner / egress backends for `TriageRouterFactory` to resolve (egress is an explicit `IChatClient`; the router references no provider package — R6)
- [ ] 3.8 Implement `TriageRouterFactory : ITerminalClientFactory` — `Create(services)` resolves the e2b/classifier/reasoner/egress backends + `AbilityRegistry` + `TriageOptions` from DI and constructs `TriageRouter`

## 4. Daemon.cs composition root

- [ ] 4.1 Create `daemon/Daemon.cs` as an ADR-019 file-based program referencing `Daemon.Routing` (and Phase 2 calendar); wire `UseTriage(e2bClient)`, `AddReasoner(reasonerClient)`, `AddEgress(egressClient)`, `AddCalendarAbilities()`, `AddToolExtension<DmailExtension>()`, and `AddDmonMemory()`
- [ ] 4.2 Read e2b endpoint from config/env (`DCAL_E2B_URL`, default `http://localhost:11434`), reasoner endpoint (`DCAL_REASONER_URL`, default `http://localhost:8080/v1`), and the egress client (Gemini over its OpenAI-compatible endpoint, key from `GEMINI_API_KEY`); no credentials or endpoints hardcoded as literals
- [ ] 4.3 Verify `Daemon.cs` compiles cleanly against Phase 1 + Phase 2 + `Daemon.Routing` (`dotnet build daemon/daemon.slnx -c Release`); this is a compile-time integration check

## 5. Daemon.App Swift Package scaffold

- [ ] 5.1 Create `daemon/Daemon.App/Package.swift` as a Swift Package targeting macOS 14; product: executable `DaemonApp`; no external Swift dependencies for this milestone
- [ ] 5.2 Create `daemon/Daemon.App/Sources/DaemonApp/DaemonApp.swift` — `@main struct DaemonApp: App` with `MenuBarExtra` scene and `Settings` scene stubs
- [ ] 5.3 Confirm `swift build -c release` from `daemon/Daemon.App/` produces a binary; confirm `make daemon-app` wraps it correctly

## 6. GatewayManager — process lifecycle

- [ ] 6.1 Implement `GatewayManager` (Swift `ObservableObject`) with `start()`, `stop()`, and `restart()` methods using `Foundation.Process`; pass `--agent daemon/Daemon.cs` to the Gateway binary
- [ ] 6.2 Implement `terminationHandler` that reschedules `start()` with exponential back-off (2s initial, 2× each retry, 60s cap)
- [ ] 6.3 Implement Gateway re-adoption on app launch: check for a PID file at `~/.dmon/run/gateway.pid`; if the process is alive, adopt it instead of spawning a new one; write PID file on spawn
- [ ] 6.4 Resolve Gateway binary path: check the `DMON_GATEWAY_PATH` env var first; fall back to the .NET global tool path (`~/.dotnet/tools/Dmon.Gateway`); surface a settings field for manual override

## 7. TailscaleMonitor

- [ ] 7.1 Implement `TailscaleMonitor` (Swift `ObservableObject`) that runs `tailscale status --json` via `Foundation.Process` every 30 seconds on a background `DispatchQueue`
- [ ] 7.2 Parse the JSON output to determine: up (Self node present, BackendState == "Running"), degraded (running but no peers), down (error or binary not found)
- [ ] 7.3 Publish `TailscaleStatus` enum (`up`, `degraded`, `down`) as an `@Published` property consumed by `MenuBarView`

## 8. MenuBarView and status icon

- [ ] 8.1 Implement `MenuBarView` as a SwiftUI `View` for the `MenuBarExtra` content: show Gateway status, Tailscale status, last sync time (from `GET /health` on Daemon.Calendar); buttons: Start/Stop Gateway, Sync Calendar Now, Open Settings
- [ ] 8.2 Implement status icon: SF Symbol `brain` (or `circle.fill`) tinted green/amber/red based on combined Gateway + Tailscale status; use `MenuBarExtra(isInserted:)` to show/hide the indicator when the app is hidden
- [ ] 8.3 Wire `GatewayManager` and `TailscaleMonitor` as `@StateObject` into the App and pass as `@EnvironmentObject` to views

## 9. Settings panel

- [ ] 9.1 Implement `SettingsView` as a SwiftUI `Settings` scene with sections: Inference (e2b URL, reasoner URL, Gemini key), Calendar (iCal URL, API key, sync interval, recurrence horizon), Email (Dmail URL, API key), Advanced (confidence threshold, Gateway path)
- [ ] 9.2 Store API keys in macOS Keychain via `Security.framework` (`SecItemAdd`/`SecItemUpdate`/`SecItemCopyMatching`); store non-secret settings in `~/.dmon/config.yaml`; confirm unsigned dev builds fall back to config.yaml with a visible warning
- [ ] 9.3 On settings save: write `~/.dmon/config.yaml`; call `GatewayManager.restart()` after showing a confirmation alert ("Saving will restart the Daemon session. Continue?")
- [ ] 9.4 Implement login item toggle using `SMAppService.mainApp.register()` / `.unregister()`; default to registered on first launch; persist toggle state in `UserDefaults`

## 10. Routing tests

- [ ] 10.1 Add `test/Daemon.Routing.Tests/` xUnit project; add to solution files
- [ ] 10.2 Test `TriageRouter.GetResponseAsync` routing invariants with a fake `IChatClient` classifier: `"personal"`/`Direct` → e2bWithTools; `"world"`/`Impersonal`/high-confidence → egress; `Tier.Reasoner` → reasoner; low-confidence `"world"` overridden to `"personal"`
- [ ] 10.3 Test the privacy gate: `ChatOptions.Tools` passed to the dispatched client contains no `"world"`-scope tools on a `"personal"` turn, and no `"personal"`-scope tools on a `"world"` turn
- [ ] 10.4 Test the misclassification counter: `dmon.triage.misclassify.personal_to_world` increments exactly once when the gate overrides `"world"`→`"personal"`, and not on a confident `"personal"` classification
- [ ] 10.5 Test the terminal-client hook: a builder with `UseTriage(...)`/`AddReasoner(...)`/`AddEgress(...)` produces a `TriageRouter` as the terminal client
