## Why

The Daemon personal assistant agent needs an always-on host on a Mac mini: a process that keeps the Gateway running, monitors Tailscale connectivity, and provides a local configuration surface for API keys, calendar URL, and model endpoints. A SwiftUI menu bar application is the native macOS idiom for this role — always accessible, no dock presence, integrates cleanly with macOS process lifecycle and login items. The AR body (a separate client) reaches the Daemon via the Gateway over Tailscale; this application is the management layer that keeps that path open.

This change also formally adds `daemon/` and a new `services/` bucket to the monorepo (ADR-028, amending ADR-025), introduces Swift as a second language for native macOS integration, renames the calendar capability to `dcal` (server `services/Dcal`, tool `tools/Dmon.Tools.Dcal`), delivers the Daemon's triage router (the agent-routing policy that consumes the Phase 1 `ITerminalClientFactory`/`IAbilityProvider` seams), and delivers the Daemon's C# composition root that wires that router, dcal abilities, Dmail, and Meko memory into a single agent.

The triage router is **Daemon-specific application policy**, not reusable engine infrastructure: it lives in `daemon/`, not in `middleware/`, and is not a `Dmon.*` first-party package on the protocol-lockstep release train (ADR-024). "Middleware" in this repo means `IDmonMiddleware.Wrap(inner)` (the `extension-middleware` spec); a multi-backend terminal client is not that. The Phase 1 `terminal-client-factory` change supplies the only general seams it needs.

## What Changes

- New `daemon/Daemon.Routing/` — C# library holding `TriageRouter : DelegatingChatClient` (per-turn classify → scope-gated manifest → dispatch over e2b-with-tools / local reasoner / gated egress), `RouteDecision`, the `Tier` enum, `TriageOptions`, and the builder verbs `UseTriage`, `AddReasoner`, `AddEgress` (in the `Dmon.Hosting` namespace). It registers a `TriageRouterFactory : ITerminalClientFactory` (Phase 1 seam) so `TriageRouter` becomes the terminal client. Scope is the Phase 1 opaque string label; the Daemon uses `"personal"`/`"world"`.
- New `daemon/Daemon.App/` — Swift Package Manager project; SwiftUI macOS menu bar app (`NSStatusItem`) managing Gateway process lifecycle, Tailscale health monitoring, and a settings panel.
- New `daemon/Daemon.cs` — C# composition root (ADR-019 file-based program) wiring `UseTriage`, `AddReasoner`, `AddEgress`, `AddDcalAbilities`, `AddToolExtension<DmailExtension>`, and Meko memory.
- New `services/` bucket (ADR-028 D3) for standalone backing servers that pair with a `tools/` extension; the iCal-sync server moves and renames `daemon/Daemon.Calendar` → `services/Dcal`, and the tool package + standing specs rename to `dcal` (`tools/Dmon.Tools.Dcal`, `dcal-lookup`/`dcal-sync`).
- ADR-028 (accepted) formally adds `daemon/` and `services/` as monorepo buckets, records the `dcal` rename, and records Swift as a supported language for native macOS components.
- `daemon/daemon.slnx` and `services/services.slnx` incorporated into `Everything.slnx` as the canonical C# solutions for the two buckets.

## Capabilities

### New Capabilities

- **triage-routing**: The Daemon's per-turn classify → manifest → dispatch policy. `TriageRouter` classifies each turn with a structured-output pass on a small always-warm local model, builds a scope-gated tool manifest via the Phase 1 `AbilityRegistry`, and dispatches to one of three backends (e2b-with-tools, local reasoner, gated egress). Privacy is structural — personal-scope turns receive no egress tools regardless of which backend is selected. Routing axes (scope `"personal"`/`"world"`, tier Direct/Reasoner) are independent. Biases to `"personal"` on low-confidence classification and emits a Personal→World misclassification metric.
- **daemon-host**: macOS menu bar application. Manages Gateway process lifecycle (launch, monitor, restart on crash), displays live status (running / degraded / stopped), monitors Tailscale interface availability, exposes a settings panel for all Daemon configuration, and registers as a macOS login item.
- **daemon-composition-root**: C# composition root for the Daemon agent. Wires three inference backends (e2b local, 26B reasoner, Gemini egress), triage routing, personal-scope abilities (calendar, email, memory), and launches via the Gateway.

### Modified Capabilities

- **monorepo-layout**: Adds `daemon/` and `services/` to the top-level role-bucket set and the per-area-solution requirement (`daemon.slnx`, `services.slnx`), per ADR-028.

### Renamed Capabilities

- **calendar-lookup → dcal-lookup** and **calendar-sync → dcal-sync**: the calendar capability is renamed `dcal` (server `services/Dcal`, tool `tools/Dmon.Tools.Dcal`), matching the shipped `DCAL_*` config.

### Removed Capabilities

_(none)_
