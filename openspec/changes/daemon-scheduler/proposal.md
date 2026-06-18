## Why

The Daemon personal assistant should act on its own clock — "every morning summarise my calendar", "remind me to check the deploy at 17:00", "every Monday draft the week plan" — not only when a human opens a session and types. There is no time-driven trigger anywhere in the system today: every turn is started by an inbound client command. This change gives the always-on Daemon host a crontab-style scheduler that fires prompts into the agent on a schedule, so the agent can do recurring and deferred work unattended.

## What Changes

- New `daemon/Daemon.Scheduler/` C# library: a persisted job store and a resident scheduler service that, when a job is due, **injects an agent turn** — it submits the job's prompt as a `turn.submit` command into the Daemon's session and lets the full triage router / scope-gated abilities / memory handle it exactly as if a client had typed it.
- **Server-side turn injection reuses existing seams — no new core/protocol surface.** The scheduler resolves the Gateway's `SessionRegistry`, finds (or ensures) the Daemon session's live `SessionHandler`, and calls the already-public `SessionHandler.WriteToCoreAsync(...)`. Events buffer in the per-session `seq` log and replay to any client that later attaches (ADR-012/014). The Gateway already hosts background services this way (`SessionReaper`, `DeviceKeyStoreWatcher`).
- **Two authoring sources, merged at load:**
  - A declarative crontab-style file (human-edited, read at startup) — deterministic, no LLM involvement to register a job.
  - An agent-callable ability set (`schedule_task` / `list_tasks` / `cancel_task`) exposed as an `IAbilityProvider` under the `"personal"` scope (the Phase 1 `ability-registry` seam) — the agent schedules its own future work.
- **Persistence:** jobs survive process restart. Each job records its schedule (cron expression or fixed interval), the prompt, the target session, scope, enabled flag, and last/next fire times. Cron parsing uses a NuGet library (e.g. Cronos), which is acceptable in `daemon/` — that bucket is application-level, not the vendor-SDK-free engine (ADR-023).
- The scheduler is hosted **in the Gateway process** (the always-on resident that owns `SessionRegistry`), wired via a thin daemon-owned Gateway host that registers the existing Gateway endpoint plus the scheduler; the general `frontends/Dmon.Gateway` `Program.cs` is unchanged.
- New **ADR-029** sanctioning (a) server-side turn injection as a supported pattern and (b) the scheduler-as-Gateway-hosted-service topology.

This is **Phase 4** of the Daemon build. It depends on Phase 3 (`daemon-app`) for the Daemon composition root, the `daemon/` bucket, and the Gateway-fronted session, and on Phase 1 (`terminal-client-factory`, merged) for the `ability-registry` seam. It is proposed and strict-validated now; it is **applied only after `daemon-app` lands**.

## Capabilities

### New Capabilities

- `daemon-scheduler`: Crontab-style scheduled triggers for the Daemon agent. Owns the persisted job model, the two authoring sources (declarative file + `"personal"`-scope ability tools) and their merge, the resident tick-and-fire service, server-side turn injection into the Daemon session via the Gateway's `SessionRegistry`/`WriteToCoreAsync`, and the misfire/restart-recovery semantics.

### Modified Capabilities

_(none — the Gateway's connection-decoupled session machinery and `WriteToCoreAsync` are reused as-is through their existing public surface; no requirement changes.)_

## Impact

- **New code:** `daemon/Daemon.Scheduler/` (job model, store, cron/interval parsing, scheduler hosted service, the `IAbilityProvider` tool set); a thin daemon-owned Gateway host composition that adds the scheduler; new tests under `test/`.
- **New dependency:** a cron-expression NuGet package (e.g. Cronos), scoped to `daemon/`.
- **Reused (unchanged):** `frontends/Dmon.Gateway` `SessionRegistry`, `SessionHandler.WriteToCoreAsync`, `ICoreLauncher`; the `turn.submit` protocol command; the Phase 1 `ability-registry` seam (`IAbilityProvider` / `AddAbilities<T>()`, scope `"personal"`).
- **Docs/specs:** new `ADR-029`; new standing spec `daemon-scheduler` on archive; the `daemon/` bucket README notes the scheduler is Gateway-process-hosted.
- **Sequencing:** depends on `daemon-app` (Phase 3); do not `/opsx:apply` until Phase 3 is merged.
