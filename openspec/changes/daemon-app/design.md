## Context

ADR-025 named `daemon` as "keep separate (empty)" and left `dmonium` placement as Open Question B: "dmonium is a macOS frontend (`frontends/`, or its own app repo?); `dmon-swift` is a Swift frontend — does `frontends/` become polyglot (Swift outside `Everything.slnx`) or does Swift stay separate?" This change resolves that question via the accepted **ADR-028**, which folds `daemon/` into the monorepo (Daemon *composition*: `Daemon.cs` + `Daemon.Routing` + `Daemon.App`/dmonium) and adds a new **`services/`** bucket for standalone backing servers that pair with a `tools/` extension. `Daemon.App` (the macOS menu bar app — dmonium) lands in `daemon/Daemon.App/` as a Swift Package. `dmon-swift` (a likely client SDK for the Swift AR client) remains a separate open question and is not addressed here.

The calendar capability is renamed **`dcal`** (ADR-028 D4), consistent with its shipped `DCAL_*` config: the iCal-sync server moves `daemon/Daemon.Calendar` → `services/Dcal`, the tool package `tools/Dmon.Tools.Calendar` → `tools/Dmon.Tools.Dcal` (verb `AddDcalAbilities()`), and the standing specs become `dcal-lookup`/`dcal-sync`.

The two other Phase changes (`terminal-client-factory`, `calendar-tool`) are prerequisites: the Phase 1 seams `IAbilityProvider`, `ITerminalClientFactory`, and `AbilityRegistry`, plus the calendar tool/server (renamed to `dcal` here), must exist before `Daemon.Routing` and `Daemon.cs` can compile. This change adds the `TriageRouter` policy itself (in `daemon/Daemon.Routing`) on top of those seams.

## Goals / Non-Goals

**Goals:**
- ADR-028 (accepted): adds `daemon/` and a new `services/` bucket to the monorepo; establishes `daemon/Daemon.App` as the `dmonium` macOS app artifact; renames the calendar capability to `dcal`; settles the Swift-in-repo question.
- New `services/` bucket: move + rename the iCal-sync server `daemon/Daemon.Calendar` → `services/Dcal`, and rename the tool `tools/Dmon.Tools.Calendar` → `tools/Dmon.Tools.Dcal` and the standing specs to `dcal-lookup`/`dcal-sync`.
- `daemon/Daemon.App/` — Swift Package, SwiftUI macOS menu bar app (dmonium): Gateway process lifecycle, Tailscale monitoring, settings panel, login item.
- `daemon/Daemon.cs` — C# composition root wiring `UseTriage`, `AddReasoner`, `AddEgress`, `AddDcalAbilities`, `AddToolExtension<DmailExtension>`, and Meko memory.
- `daemon/daemon.slnx` (C# composition projects) and `services/services.slnx` (the Dcal server) incorporated into `Everything.slnx`; the Swift Package is outside `Everything.slnx` (not .NET) but noted in the bucket's README.
- CI: app-artifact job for `daemon/Daemon.App` producing a signed `.app`/`.dmg` (dmonium) as per ADR-025 D10 release matrix.

**Non-Goals:**
- The AR body client (separate project/repo).
- `dmon-swift` SDK placement (separate Open Question B follow-on).
- Multi-session / multi-core tabbing in the menu bar app.
- Automated Tailscale installation or configuration (assumes Tailscale is already installed).
- Code-signing / notarisation pipeline (noted but deferred to a follow-on CI change).

## Decisions

### D1: daemon/ folds into the monorepo; Daemon.App lands in daemon/, not frontends/

ADR-025 D2 lists `dmonium` under `frontends/`. This design instead places `Daemon.App` in `daemon/Daemon.App/` so the Daemon *composition* (composition root, triage-routing policy, menu bar app) lives in one cohesive bucket. The backing servers it consumes (the calendar/`dcal` server, the future Dmail server) do **not** live in `daemon/`; they live in `services/` (D1b). The `dmonium` name from ADR-025 is preserved as the product name and `.app` bundle identifier (`ai.daemonic.dmonium`). The `frontends/` bucket retains host apps that are directly upstream of the dmon protocol surface (Terminal, Gateway, Desktop); the menu bar management app is not a protocol-surface host — it manages processes rather than acting as one.

ADR-028 records this departure from ADR-025 D2/D11 and resolves Open Question B.

### D1b: services/ bucket for backing servers; the dcal rename

A standalone server that backs an agent capability whose agent-facing surface is a paired `tools/` extension is neither a `tools/` package, a protocol-surface host, nor part of the Daemon composition. ADR-028 D3 adds a **`services/`** bucket for these: `services/Dcal` (the iCal-sync HTTP server, moved and renamed from `daemon/Daemon.Calendar`) and the future `services/Dmail` (the Dmail server, grafted from its own repo by a later change). Services are app artifacts, independently versioned, not on ADR-024's protocol-lockstep train.

The calendar capability is renamed `dcal` across server (`services/Dcal` / `Dcal.csproj`), tool (`tools/Dmon.Tools.Dcal`, verb `AddDcalAbilities()`, types `DcalExtension`/`DcalAbilityProvider`), and standing specs (`dcal-lookup`/`dcal-sync`). The model-facing tool names (`lookup_calendar`, `list_upcoming_events`) and the `DCAL_*` config prefix are unchanged — `dcal` already matches that prefix, so there is no config migration. Because the `calendar-tool` change is already merged/archived, this rename is carried out here, in `daemon-app`.

### D2: Swift Package lives in daemon/Daemon.App/, outside Everything.slnx

`daemon/Daemon.App/` is a standard Swift Package Manager project (`Package.swift`). It is not referenced by `Everything.slnx` (which is .NET-only) and is not part of `daemon/daemon.slnx`. It has its own build path: `swift build -c release` from `daemon/Daemon.App/`. A `Makefile` target at the repo root (`make daemon-app`) encapsulates this. CI adds a separate macOS job that runs `make daemon-app` and produces the `.app` artifact.

### D3: Daemon.cs wiring

The composition root wires the full Daemon agent. All three backends are supplied as `IChatClient` instances — the router is provider-agnostic (see R6), so egress is an explicit `AddEgress(IChatClient)`, not a provider-name lookup:

```csharp
DmonHost.CreateBuilder()
    .UseTriage(new OllamaApiClient(new Uri(e2bUrl), "gemma4:e2b-it-qat"))
    .AddReasoner(new OpenAIClient(
            new ApiKeyCredential("not-needed"),
            new OpenAIClientOptions { Endpoint = new Uri(reasonerUrl) })
        .GetChatClient("gemma4-26b-a4b").AsIChatClient())
    .AddEgress(new OpenAIClient(
            new ApiKeyCredential(geminiKey),
            new OpenAIClientOptions { Endpoint = new Uri(geminiOpenAiCompatUrl) })
        .GetChatClient("gemini-3.5-flash").AsIChatClient())
    .AddDcalAbilities()
    .AddToolExtension<DmailExtension>()
    .AddDmonMemory()          // Dmon.Memory.Meko long-term tier
    .Build()
    .Run();
```

Model endpoints and the Gemini key are read from env vars / `.dmon/config.yaml` per ADR-005; the composition root does not hard-code credentials. The Daemon.App settings panel writes to `.dmon/config.yaml` so changes take effect on Gateway restart.

### Triage routing decisions

These decisions cover `daemon/Daemon.Routing` — the `TriageRouter` policy and its verbs. They build on the Phase 1 `terminal-client-factory` seams (`ITerminalClientFactory`, `IAbilityProvider`, `AbilityRegistry`).

#### R1: TriageRouter is the terminal client via ITerminalClientFactory

`UseTriage(e2bRaw, options?)` registers the e2b client and a `TriageRouterFactory : ITerminalClientFactory`. `AddReasoner(reasoner)` and `AddEgress(egress)` register those backends. At `Build()`, the Phase 1 hook resolves the factory and uses its output as the terminal client. `TriageRouterFactory.Create(services)` resolves the three backends + `AbilityRegistry` from DI and constructs `TriageRouter`. The verbs are `IDmonHostBuilder` extension methods shipped in the `Dmon.Hosting` namespace (host-level verbs per `composition-root-hosting`).

#### R2: Classifier and answer client share one underlying e2b model

`UseTriage` builds two pipeline configurations over the single `e2bRaw` client: `classifier = e2bRaw` (no tools, structured output) and `e2bWithTools = e2bRaw.AsBuilder().UseFunctionInvocation().Build()` (tools + per-turn manifest). The composition root constructs `e2bRaw` (e.g. `OllamaApiClient` with `keep_alive: -1` so the classify pass is sub-second); the router package takes no vendor SDK dependency.

#### R3: Structured-output classify pass, not route-token-as-tool-call

`await classifier.GetResponseAsync<RouteDecision>(messages, ct)` is debuggable, typed, and does not interact with `UseFunctionInvocation` ordering. Revisit only if classify-pass latency shows up in profiling.

#### R4: Bias to "personal" on ambiguity — backstop in the router, not only the prompt

The classifier prompt defaults ambiguous turns to `"personal"`, `Tier.Direct`. As defence-in-depth, `TriageRouter` also overrides any `RouteDecision` with `Confidence < EgressThreshold` to `"personal"` scope before dispatch. Misroute costs are asymmetric: personal-mistaken-for-world leaks private data; world-mistaken-for-personal only wastes compute.

#### R5: Egress gated on world scope + Impersonal + confidence; privacy enforced by the manifest

```csharp
IChatClient target = route switch
{
    { Scope: "world", Impersonal: true, Confidence: > EgressThreshold } => egress,
    { Tier: Tier.Reasoner }                                             => reasoner,
    _                                                                   => e2bWithTools,
};
```

`EgressThreshold` defaults to `0.8f`, configurable via `TriageOptions`. Privacy does not depend on which client is selected: `AbilityRegistry.ForScope(route.Scope)` builds the manifest, so even if egress were reached on a personal turn it would carry no personal tools. A `Tier.Reasoner` turn dispatches to the local reasoner *regardless of scope* — a world+reasoner turn runs locally with the world (web) manifest (the stubbed "WebSearch + 26B synthesis" path); scope still governs the manifest, tier governs the backend.

#### R6: Egress is provider-agnostic — explicit AddEgress(IChatClient) (resolves former OQ-B)

The earlier proposal left open how egress is obtained (a `UseGemini(passive)` overload vs. resolving the first DI provider named `"gemini"`). Both are rejected: a provider-name lookup couples the router to Gemini and contradicts ADR-023's provider-agnostic default. Instead `AddEgress(IChatClient)` is symmetric with `AddReasoner(IChatClient)`; the composition root supplies whatever egress client it wants (the Daemon uses Gemini over its OpenAI-compatible endpoint). The router never references a provider package.

#### R7: Per-turn misclassification metric — Personal→World specifically

`TriageRouter` increments `dmon.triage.misclassify.personal_to_world` (via `DaemonTelemetry`) on any turn where the raw classifier produced `"world"` but the confidence gate overrode it to `"personal"`. This is the leak-direction metric the brief requires; overall routing accuracy is a separate counter.

#### R8: Streaming ack before the target's first token

`GetStreamingResponseAsync` classifies, then yields one synthetic `ChatResponseUpdate` (a fixed, route-dependent ack string) before forwarding the target's stream. The ack necessarily follows the classify await — "immediate" means "before the target produces any token," not before classification.

### D4: Gateway process management via Foundation.Process

`GatewayManager` (Swift class) holds a `Foundation.Process` reference to the running `Dmon.Gateway` process. On launch it starts the process, monitors it via `terminationHandler`, and restarts with exponential back-off on crash. The Gateway binary path is resolved from a settings entry (default: looks for `Dmon.Gateway` in the NuGet global tool path, same as the `dmon` tool).

The Gateway is passed the Daemon agent's composition root path via `--agent daemon/Daemon.cs` (ADR-020 per-session agent selection). The Gateway process is a child of the menu bar app process; macOS terminates it when the app quits (no orphan).

### D5: Tailscale monitoring via CLI polling

`TailscaleMonitor` (Swift class) polls `tailscale status --json` every 30 seconds via `Foundation.Process`. It checks for the Tailscale interface being up and the daemon being reachable. Display states: green (up, peers reachable), amber (up but no peers), red (not running). This avoids a dependency on the Tailscale Swift SDK and works with any Tailscale installation.

**Alternative considered:** `Network.framework` `NWPathMonitor` for connectivity. Rejected: it detects general network, not Tailscale specifically. `tailscale status` is the only reliable signal.

### D6: Settings panel writes to .dmon/config.yaml

The settings panel (SwiftUI `Settings` scene) manages: API keys (Gemini, Dmail, Calendar), local model endpoints (e2b URL, reasoner URL), calendar iCal URL, sync interval, and triage confidence threshold. On save, the app writes to `~/.dmon/config.yaml` (the config path dmon core reads per ADR-005) and signals the Gateway to restart the core session so changes take effect. Sensitive values (API keys) are written to macOS Keychain and referenced by name from `config.yaml`, not stored in the YAML directly.

### D7: Login item via SMAppService (macOS 13+)

`Daemon.App` registers itself as a login item using `SMAppService.mainApp.register()` on first launch, with a toggle in the settings panel. Target minimum: macOS 14 (Sonoma), matching the Daemon's AR client target.

## Risks / Trade-offs

- **Gateway restart on settings change:** Restarting the Gateway drops any active session. For the Daemon's single-user, always-on model this is acceptable. Mitigation: show a warning in the settings panel before saving.
- **Foundation.Process for Gateway:** The gateway process is a child of the menu bar app. If the app crashes hard, the Gateway may orphan briefly before macOS cleans it up. Mitigation: the Gateway has its own health endpoint; a fresh app launch will detect and re-adopt a running Gateway (check PID file before spawning).
- **Tailscale CLI polling latency:** 30-second poll means up to 30s lag in status display. Acceptable for a status indicator; not a functional dependency (the Gateway listens on Tailscale regardless of what the app shows).
- **Keychain dependency:** API key management via Keychain requires the app to be code-signed. Unsigned dev builds store keys in config.yaml directly (with a warning). Production requires a Developer ID cert.

## Open Questions

- **OQ-A (ADR number):** *Resolved* — a new **ADR-028** (Accepted), not a targeted ADR-025 amendment. It also adds the `services/` bucket and the `dcal` rename (beyond the originally-scoped daemon bucket).
- **OQ-B (Gateway re-adoption):** How does a restarted `Daemon.App` detect an already-running Gateway? Options: PID file in `~/.dmon/run/`, or poll `GET /health` on the Gateway port. PID file is simpler; implement during tasks.
- **OQ-C (Daemon.cs model endpoints):** The composition root hard-codes `http://localhost:11434` and `http://localhost:8080/v1` as defaults. These should come from `~/.dmon/config.yaml` / env vars. The settings panel in Daemon.App needs corresponding fields. Confirm config key names during implementation.
- **OQ-D (confidence threshold):** `EgressThreshold = 0.8f` is a placeholder doing double duty as both the egress gate and the personal-bias floor (R4/R5). The correct value depends on measured misclassification rates against the Daemon's real turn distribution. Keep it configurable via `TriageOptions`; document that tuning requires the R7 metric to be running. Decide during implementation whether the egress gate and the personal-bias floor should be one knob or two.
- **OQ-E (streaming ack content):** The brief specifies an immediate ack ("checking your calendar…") before the target's first token (R8). A fixed per-route string is fine for this milestone; making it configurable is a follow-on.
