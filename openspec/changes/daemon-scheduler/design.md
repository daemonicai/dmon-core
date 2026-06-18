## Context

The Daemon (Phase 3, `daemon-app`) is an always-on personal assistant on a Mac mini: a menu-bar app keeps a Gateway process alive, the Gateway spawns the Daemon core (`Daemon.cs`), and an AR client reaches it over Tailscale. Every turn today is started by an inbound client command (`turn.submit`) — there is no time-driven trigger. This change adds a crontab-style scheduler so the Daemon acts on its own clock.

Investigation of the turn path (core + Gateway) established the one architectural fact this design hinges on: **a server-side component can already submit a turn without a connected client.** A turn is just a `turn.submit` command routed through `CommandDispatcher` → `TurnHandler.SubmitAsync` → `RunTurnAsync`; `TurnHandler` is not connection-aware, and the Gateway's `SessionHandler` (which owns the core's stdin/stdout) exposes a public `WriteToCoreAsync(frameJson, ct)` that works whether or not a WebSocket is attached, with events buffered in the per-session `seq` log for replay (ADR-012/014). `SessionRegistry` is a DI singleton in the Gateway process; the Gateway already runs background work as hosted services (`SessionReaper`, `DeviceKeyStoreWatcher`) that resolve it. The scheduler is the same shape.

Prerequisites: Phase 1 `terminal-client-factory` (merged) supplies the `ability-registry` seam (`IAbilityProvider` / `AddAbilities<T>()`, opaque `"personal"` scope). Phase 3 `daemon-app` supplies the `daemon/` bucket, `Daemon.cs`, and the Gateway-fronted Daemon session.

## Goals / Non-Goals

**Goals:**
- A resident scheduler that fires due jobs by injecting an agent turn into the Daemon session through the existing Gateway session machinery — no new core/protocol surface.
- Two authoring sources merged at load: a declarative crontab-style file and an agent-callable `"personal"`-scope ability set (`schedule_task` / `list_tasks` / `cancel_task`).
- Persisted jobs that survive process restart, with defined misfire/catch-up behaviour.
- Per-job scope so injected turns are gated by the Daemon's triage router exactly like client turns.
- ADR-029 recording server-side turn injection as a sanctioned pattern and the Gateway-hosted-scheduler topology.

**Non-Goals:**
- No new wire/protocol command, no change to `TurnHandler` or core. Injection reuses `turn.submit` + `WriteToCoreAsync`.
- No distributed scheduling, no second machine, no clustering — single resident process.
- No UI for editing schedules in the menu-bar app (the crontab file + agent tools are the surface; a settings panel is a later `daemon-app` concern).
- No sub-minute precision; tick granularity is coarse (see D6).
- Not a general-purpose engine feature — the scheduler lives only in `daemon/`.

## Decisions

### D1: Fire = inject a `turn.submit` via the Gateway's existing seams (fork resolved)

When a job is due the scheduler builds a `TurnSubmitCommand { Message = job.Prompt }`, serialises it with the wire options, and calls `SessionHandler.WriteToCoreAsync` on the target session's handler. The core processes it through the standard dispatcher → `TurnHandler` pipeline; the full triage router, scope-gated abilities, and memory apply unchanged. Events buffer in the `seq` log and replay when a client next attaches.

- **Alternative — new `ITurnInjector` core seam:** rejected. The brief asked whether a seam is needed; it is not. `WriteToCoreAsync` is already public and connection-agnostic, and `TurnHandler` already ignores connection state. Adding a seam would duplicate an existing path and modify `remote-session-gateway` for no behavioural gain.
- **Alternative — inject `ChatMessage` directly into core history:** rejected. It would bypass `seq` sequencing, command idempotency, and event replay, and couple the scheduler to core internals across the process boundary.

### D2: The scheduler is a Gateway-process `IHostedService`; the Daemon supplies a thin Gateway host

The scheduler must hold the in-process `SessionRegistry`, which lives in the Gateway process. It is therefore a `BackgroundService` registered with `AddHostedService`, mirroring `SessionReaper`. To keep this Daemon-specific code out of the general `frontends/Dmon.Gateway` `Program.cs`, the Daemon owns a thin Gateway host composition that registers the existing Gateway services **and** `AddDaemonScheduler()`. The general Gateway `Program.cs` is untouched; if any wiring is shared it is via a behaviour-identical `AddGateway(this WebApplicationBuilder)` extraction (a refactor, not a requirement change — so no `remote-session-gateway` spec delta).

- **Alternative — scheduler in the Swift menu-bar app:** rejected. Swift has no access to `SessionRegistry`; it would have to inject over the WebSocket as a synthetic client, re-authenticating and competing with the single-active-writer fence — strictly worse than in-process.
- **Alternative — config flag on the general Gateway:** rejected for now. Loading Daemon scheduling into the general Gateway couples a reusable frontend to Daemon policy; a separate host keeps the boundary clean and matches the composition-root philosophy (ADR-019/022).

### D3: Target-session resolution — a designated long-lived Daemon session, ensured before firing

Each job names a target session (default: the Daemon's canonical session id, settled by `daemon-app`). At fire time the scheduler looks it up in `SessionRegistry`. If present, inject. If absent (no client has connected since boot), the scheduler **ensures** it via the same create path the Gateway uses (`ICoreLauncher`, already in Gateway DI) before injecting; if it cannot be ensured, the fire is skipped and logged — a job never crashes the scheduler.

- **Open dependency:** the exact canonical-session identity is owned by `daemon-app`; this change consumes it and must not invent a parallel notion. Tracked in Open Questions.

### D4: Two authoring sources, merged at load with file-wins-by-key

- **Declarative file:** a crontab-style text file under the Daemon data dir, read at startup and on change. Lines are `<schedule> <scope> <prompt>` (schedule = a cron expression or `@every <duration>`). File jobs are **read-only** to the agent — they are owned by the human and re-derived from the file, never mutated by the store.
- **Agent jobs:** created/cancelled through the `"personal"`-scope ability tools and held in the mutable persisted store.
- **Merge:** the active job set is the union, keyed by a stable job id. File jobs use a deterministic id derived from the file line (so reloading the file is idempotent and doesn't duplicate). Agent jobs use a generated id. On id collision, the file source wins (the human's declaration is authoritative).
- **Alternative — single source:** rejected; the user explicitly chose both.

### D5: Persistence — a JSON job store written atomically

Agent-authored jobs persist to a single JSON file in the Daemon data dir. A job records: id, source (`file` | `agent`), schedule (cron string or interval), prompt, target session, scope, enabled, created-at, last-fire, next-fire. Writes are atomic (temp-file + rename). The store is small and single-writer (the scheduler), so no database is needed. File-sourced jobs are not persisted to the store (they are re-derived from the crontab file), keeping the two sources from drifting.

### D6: Schedule kinds and tick granularity — Cronos for cron, `@every` for intervals, minute tick

Cron expressions are parsed with **Cronos** (a small, well-tested .NET cron library). This is acceptable in `daemon/` — ADR-023's vendor-SDK-free rule is about the engine packages, not application-level `daemon/` code. Intervals use an `@every <duration>` form. The scheduler computes each job's next-fire and wakes on the nearest one (a single timer, not per-job threads); granularity is one minute — sub-minute schedules are rejected at load. Times are evaluated in the host's local time zone (DST handled by Cronos); the zone is not configurable in this change.

### D7: Misfire / restart recovery — coalesce missed fires to a single catch-up

If the process was down across one or more scheduled times, on restart each job fires **at most once** as a catch-up (missed occurrences are coalesced, not replayed N times), then resumes its normal cadence. A job whose `enabled` is false never fires. This avoids a thundering herd of stale prompts after a long downtime while still honouring "you missed this".

- **Alternative — replay every missed occurrence:** rejected (could inject dozens of turns at once). **Alternative — skip silently:** rejected (loses a genuinely-due daily summary).

### D8: One turn at a time per session — defer, don't pile up

`TurnHandler` runs a single turn at a time. Before injecting, the scheduler checks whether a turn is already running for the target session (via the session's running-turn state) and, if so, defers the fire to the next tick rather than queuing unboundedly. A fire that is deferred past its own next occurrence is coalesced per D7.

### D9: ADR-029 — sanction the pattern

A short ADR-029 records: (1) server-side turn injection via `WriteToCoreAsync` is a supported pattern for non-client-originated turns; (2) time-driven Daemon automation is a Gateway-process hosted service in `daemon/`, not middleware and not a protocol-lockstep package (consistent with ADR-026/027 keeping `middleware/` empty and Daemon policy in `daemon/`). It amends nothing normative in `remote-session-gateway`; it documents a reuse.

## Risks / Trade-offs

- **[Injected turn races a live user turn]** → D8 defers when a turn is running; coalesced per D7 so it doesn't pile up.
- **[Target session doesn't exist at fire time]** → D3 ensures-or-skips; a missing session never throws out of the tick loop.
- **[Long downtime → stale prompt storm]** → D7 coalesces missed fires to one catch-up per job.
- **[Crontab file and agent store drift]** → D4 keeps file jobs out of the store entirely and re-derives them; file-wins on id collision.
- **[Cron/DST surprises]** → delegated to Cronos; local-zone only this change; documented.
- **[A bad prompt/job throws]** → every fire is isolated; failures are logged and the scheduler continues (a job is never allowed to crash the host).
- **[Coupling daemon code into the general Gateway]** → D2 keeps it in a Daemon-owned host; general `Program.cs` untouched.

## Migration Plan

Additive and Daemon-only. No existing behaviour changes; no data migration. Rollout = ship `daemon/Daemon.Scheduler` + the Daemon Gateway host; with no crontab file and no agent-created jobs the scheduler is inert. Rollback = don't register `AddDaemonScheduler()` (or remove the crontab file); nothing else is affected. **Sequencing:** apply only after `daemon-app` (Phase 3) merges.

## Open Questions

- **Canonical Daemon session identity (owned by `daemon-app`).** This change targets "the Daemon session"; the concrete id/creation contract is settled by Phase 3. If Phase 3 lands a different model than D3 assumes, revisit D3 before applying.
- **Crontab file location and exact line grammar.** Proposed `<schedule> <scope> <prompt>` under the Daemon data dir; confirm against the `daemon-app` settings/data-dir layout at apply time.
- **Time-zone configurability.** Local-zone only here; a future change may add a per-job or global zone if the AR client is used across zones.
