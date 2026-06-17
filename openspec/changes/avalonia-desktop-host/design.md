## Context

The brief planned two host surfaces — a console/TUI host and an Avalonia desktop host — and deferred the desktop host behind a sequencing precondition: *"build the console host first, prove the RPC surface."* That precondition is now met. `Dmon.Terminal` ships, and the host-facing RPC surface was extracted into `Dmon.Runtime` (`IRpcTransport`, `IRpcClient`, `ICoreLauncher`, `ICoreProcess`, `CoreProcessRpcTransport`, `RpcClient`) and is already consumed by two hosts (Terminal and Gateway). All of those types are **per-instance** — there is no shared/singleton state in the launch→transport→client path — which is what makes a future N-session desktop free at the runtime layer.

This change adds a second, independent GUI frontend. Its secondary purpose is to be the proof that the RPC surface is genuinely host-agnostic: if the desktop host can be built purely against `Dmon.Runtime` + `Dmon.Protocol` with no core changes, the abstraction is validated; if it forces core changes, that leak is the headline finding and must be surfaced rather than patched silently.

## Goals / Non-Goals

**Goals:**

- A working `frontends/Dmon.Desktop` Avalonia app that reaches **parity with the TUI** for a single session: render the parts model, stream deltas, answer tool-confirm/UI-input requests, support reload.
- Local-spawn ("TUI-with-pixels"): one local core over stdio via `Dmon.Runtime`, exactly the Terminal pattern.
- ReactiveUI MVVM with **full routing from day one** (`SessionViewModel : IScreen`), so the multi-tab future and in-session screens are additive.
- PipBoy look (`Pipboy.Avalonia` + `.Fx`); markdown via `Markdown.Avalonia`.
- Prove `Dmon.Runtime` is host-agnostic by adding zero core/protocol changes.

**Non-Goals:**

- **Multi-session / multi-core tabs.** Deferred to a second change. It is free at the runtime layer (per-instance client) and grows out of this design without rework (see Decision 2).
- The V1.5+ desktop affordances the brief lists: visual diff preview before approving edits, side-by-side tool panels, session-graph view, skill/extension browser. (The first two slot into the Interaction modal and the routing stack respectively; nothing here forecloses them.)
- A self-contained installable artifact that bundles its own core. The first cut resolves the core at runtime from the NuGet cache like the `dmon` tool (dev-machine assumption).
- Any change to the wire protocol, core, or session storage.
- Not a multi-agent orchestration concern: even the future multi-tab case runs N **independent** cores that never communicate with each other, which is in scope per ADR-010 (orchestration = cores talking to each other over RPC).

## Decisions

### Decision 1 — Local-spawn over `Dmon.Runtime`, not a gateway client

The desktop host spawns a local core and drives it through `IRpcClient`, identical to `Dmon.Terminal`. **Alternative considered:** make the desktop a WebSocket client of the ADR-012 gateway (unifying with the in-scope iOS client's transport). Rejected for the first cut: it adds the gateway's attach/replay/auth surface (ADR-012/014/018) to what should be the simplest possible second frontend, and the local-spawn path is the one that most directly stress-tests `Dmon.Runtime`. A gateway-client desktop remains a possible future transport, not this change.

### Decision 2 — ReactiveUI routing from the start; `SessionViewModel : IScreen`

ReactiveUI routing models navigation **depth** (a per-`IScreen` `RoutingState` push/pop stack); multi-tab is navigation **breadth** (N independent screens). We make the first cut a degenerate case of the multi-tab future by having `SessionViewModel` implement `IScreen` now and own its `RoutingState`. First cut: `App` hosts one `SessionViewModel` in a top-level `RoutedViewHost`; `ConversationViewModel` is the default routed screen. Second pass: a `ShellViewModel` owns an observable collection of those same `SessionViewModel`s and a `TabControl` hosts one `RoutedViewHost` per tab — the session VM is unchanged. **Alternative considered:** keep it flat (no routing) for the first cut and add routing when tabs land. Rejected per the user's call: routing depth is the painful part to retrofit, and buying it now is cheap; in-session screens (settings/session-graph/extension-browser) become `Router.Navigate` pushes from day one.

### Decision 3 — ReactiveUI event/threading model bridges the stdio pump

`IRpcClient.Events` (`IAsyncEnumerable<Event>`) is bridged to Rx via `.ToObservable()` and observed on `RxApp.MainThreadScheduler`, so the background pump never touches Avalonia controls — this is the dispatcher-marshal handled idiomatically rather than via manual `Dispatcher.UIThread.Post`. `messageDelta` is coalesced with `Buffer`/`Sample` over a short window (≈ one frame) before the per-token UI update, to keep fast token streams from flooding the dispatcher. Outbound user actions are `ReactiveCommand`s whose bodies call `IRpcClient.SendAsync`/`RequestAsync`; the send command's `CanExecute` is driven by an `IsStreaming` observable.

### Decision 4 — Tool-confirm / UI-input via ReactiveUI `Interaction<,>`

The "core asks the host a question and awaits an answer" shape (`tool.confirmRequest`, UI input requests) maps exactly onto ReactiveUI `Interaction<TInput,TOutput>`: VM raises, view's handler shows a modal, result flows back, VM relays the response command. This is also the seam where the V1.5+ **visual diff preview before approving an edit** later slots in — the modal grows richer, the plumbing doesn't change. **Alternative considered:** ad-hoc event + dialog service. Rejected: Interactions are the testable, idiomatic ReactiveUI primitive for this and keep the VM view-agnostic.

### Decision 5 — Markdown via `Markdown.Avalonia`, themed to PipBoy

`TextPart` is rendered with `Markdown.Avalonia`, styled to the PipBoy phosphor/monospace palette. The TUI's `dcli`-based markdown renderer does not transfer (different substrate), and markdown rendering is the single biggest chunk of genuinely-new rendering work — build-vs-buy resolved to **buy** (per the user's call). **Alternative considered:** hand-roll a minimal renderer matching the TUI exactly. Rejected: more work, less capable, no payoff.

### Decision 6 — Core acquisition reuses runtime-resolve; no new ADR

The host resolves `dmoncore` from the NuGet cache through `ICoreLauncher`, exactly like the `dmon` dotnet tool — no new bundling/pinning. This keeps the change inside the existing distribution model (ADR-011/019/024/025); the desktop installer story (an "app artifact" in the ADR-024/025 release matrix that bundles a pinned core) is deliberately deferred and noted as an open question for a later release-focused change. No ADR is required for this change.

### Decision 7 — Splat ↔ Microsoft.Extensions.DependencyInjection bridge

The rest of dmon composes over MS DI (ADR-022). ReactiveUI resolves views via Splat. We bridge them with `Splat.Microsoft.Extensions.DependencyInjection` so services **and** `IViewFor<TViewModel>` view registrations resolve from one inspectable composition root. Views are registered explicitly rather than relying on convention scanning, keeping the root inspectable like the engine's.

### Decision 8 — Scope-gate amendment is documentation, not an ADR

`coding-agent-brief.md` ("Out of Scope for V1" / "Avalonia UI Possibilities (V1.5+)") and the `CLAUDE.md` mirror are amended to record that the gating precondition is met and the host is now in scope. All technical decisions above fall inside existing ADRs (no ADR contradicted, none needed). This is a docs edit bundled in the change, not a superseding-ADR event.

## Risks / Trade-offs

- **The RPC surface leaks (core change proves necessary).** → This is the change's whole hypothesis. If a core/protocol/runtime change is required, **stop and surface it** (per CLAUDE.md "Stop and ask") rather than patching the host around it — the leak is a finding about the abstraction, not desktop trivia.
- **Avalonia/ReactiveUI test ergonomics differ from the TUI's headless harness.** → Keep logic in view-models testable without a UI: VMs depend only on `Dmon.Runtime`/`Dmon.Protocol` abstractions; the markdown-to-rendering and per-token coalescing are unit-testable; live UI behaviour (boot gate, modal round-trip) is verified manually with a copy-pasteable recipe at gate time (analogous to terminal-host's "verified against a live core" requirement).
- **Token-delta flooding the dispatcher.** → `Buffer`/`Sample` coalescing (Decision 3); a unit test asserts coalescing collapses a burst into bounded UI updates.
- **PipBoy / Markdown.Avalonia version or API drift.** → Both are external packages pinned via CPM; the theme is orthogonal to MVVM (styles over standard controls) so a theme bump cannot break logic. `Markdown.Avalonia` is isolated behind the `TextPart` template.
- **Reload/lifecycle races (learned from terminal-host hardening).** → Reuse the Terminal restart discipline: reload only between turns, single-shot under rapid input, dispose-then-relaunch with rebind; cross-thread state guarded by the Rx scheduler boundary rather than ad-hoc `volatile`.

## Open Questions

- **Desktop distribution/installer.** How a shippable desktop artifact acquires/pins the core (bundle a pinned `dmoncore` vs. resolve-at-runtime) is deferred. The first cut assumes a populated cache (dev machine). To be settled in a later release-focused change, possibly with a short ADR amending the distribution model.
- **Markdown styling fidelity.** Exact mapping of `Markdown.Avalonia` styles to the PipBoy palette (code-block treatment, link colour) is an implementation detail to tune against the theme; no decision blocks implementation.
