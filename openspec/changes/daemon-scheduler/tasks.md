## 1. ADR-029 and project scaffold

- [ ] 1.1 Write `docs/adrs/ADR-029-daemon-scheduler.md` (status Accepted): sanction server-side turn injection via `SessionHandler.WriteToCoreAsync` for non-client-originated turns; record the scheduler as a Gateway-process hosted service in `daemon/` (not middleware, not a protocol-lockstep package); note it amends nothing normative in `remote-session-gateway`. Add it to the ADR table in `CLAUDE.md`.
- [ ] 1.2 Create `daemon/Daemon.Scheduler/` C# library project (`Dmon.Daemon.Scheduler` or the daemon-bucket naming settled by `daemon-app`); add the Cronos package reference; set `TreatWarningsAsErrors` consistent with the repo.
- [ ] 1.3 Add the project to `daemon/daemon.slnx` and to `Everything.slnx`.
- [ ] 1.4 Create the test project `test/Daemon.Scheduler.Tests` and wire it into the solutions.

## 2. Job model and persisted store

- [ ] 2.1 Define the job record: id, source (`File` | `Agent`), schedule, prompt, target session, scope, enabled, created-at, last-fire, next-fire.
- [ ] 2.2 Implement the persisted store for agent-sourced jobs (JSON file under the Daemon data dir) with atomic temp-file + rename writes; file-sourced jobs are not persisted to the store.
- [ ] 2.3 Load the store on startup and expose add/remove/list and last/next-fire updates. (Satisfies "Jobs are persisted and survive process restart".)

## 3. Schedule parsing

- [ ] 3.1 Implement schedule parsing for cron expressions (Cronos) and a fixed-interval `@every <duration>` form, computing next-fire in the host local time zone.
- [ ] 3.2 Reject sub-minute schedules at load with a per-job error that does not abort loading of the remaining jobs. (Satisfies "Schedules support cron expressions and fixed intervals".)

## 4. Crontab-style file source

- [ ] 4.1 Parse the crontab-style file (`<schedule> <scope> <prompt>` per line) from the Daemon data dir; derive a deterministic id per line so reloads are idempotent.
- [ ] 4.2 Read at startup and reload on file change; treat file jobs as read-only (re-derived, never written to the store). (Satisfies "Jobs can be declared in a crontab-style file".)

## 5. Source merge

- [ ] 5.1 Merge file and agent jobs into the active set keyed by stable id; on id collision the file-sourced job wins. (Satisfies "File and agent sources are merged with file precedence".)

## 6. Turn injection and target-session resolution

- [ ] 6.1 Implement firing: build a `TurnSubmitCommand` from the job prompt, serialise with the wire options, and submit via the target session's `SessionHandler.WriteToCoreAsync` obtained from `SessionRegistry`.
- [ ] 6.2 Associate the injected turn with the job's scope so Daemon routing gates abilities as for a client turn of that scope. (Satisfies "Each job carries a scope applied to its injected turn".)
- [ ] 6.3 Resolve the target session against `SessionRegistry`; if absent, attempt to ensure it via `ICoreLauncher`; if it cannot be ensured, skip and log. (Satisfies "A due job fires by injecting a turn" + the absent-session half of the isolation requirement.)

## 7. Scheduler hosted service

- [ ] 7.1 Implement the `BackgroundService` tick loop: compute the nearest next-fire across enabled jobs and wake on it (single timer, minute granularity).
- [ ] 7.2 Skip disabled jobs. (Satisfies "Disabled jobs do not fire".)
- [ ] 7.3 Coalesce missed fires during downtime to a single catch-up per job, then resume cadence. (Satisfies "Missed fires during downtime coalesce to a single catch-up".)
- [ ] 7.4 Defer a fire when the target session already has a turn in progress, re-evaluating on a later tick. (Satisfies "A fire is deferred while a turn is already running".)
- [ ] 7.5 Isolate per-job failures: a throwing fire is logged and does not stop evaluation of other jobs. (Satisfies the failure-isolation half of "Missing target sessions and failing fires never crash the scheduler".)

## 8. Personal-scope ability tools

- [ ] 8.1 Implement `schedule_task`, `list_tasks`, and `cancel_task` over the store.
- [ ] 8.2 Expose them on an `IAbilityProvider` declaring scope `"personal"`, registered via `AddAbilities<T>()`; verify they appear only in the `"personal"` manifest. (Satisfies "The agent can manage jobs through personal-scope abilities".)

## 9. Daemon Gateway host wiring

- [ ] 9.1 Add `AddDaemonScheduler()` registering the hosted service and its dependencies, resolving the Gateway `SessionRegistry` and `ICoreLauncher` from DI.
- [ ] 9.2 Provide the thin Daemon-owned Gateway host that registers the existing Gateway services plus `AddDaemonScheduler()`, leaving the general `frontends/Dmon.Gateway` `Program.cs` untouched (extract a behaviour-identical `AddGateway` extension only if needed for reuse). (Satisfies "The scheduler is a resident service hosted in the Gateway process".)

## 10. Tests

- [ ] 10.1 Store tests: persistence round-trip across reload; atomic write.
- [ ] 10.2 Schedule tests: cron next-fire correctness; interval; sub-minute rejection leaves other jobs loaded.
- [ ] 10.3 File-source tests: line parsing; idempotent reload (no duplicates); read-only (not written to store).
- [ ] 10.4 Merge tests: union of both sources; file-wins on id collision.
- [ ] 10.5 Firing tests (with a fake `SessionRegistry`/handler): due job writes a `turn.submit`; absent session is skipped+logged; a throwing fire is isolated; disabled job does not fire; deferral while a turn is running; missed-fires coalesce to one catch-up.
- [ ] 10.6 Ability tests: `schedule_task` persists; `cancel_task` removes; tools present only in `"personal"` scope (via `AbilityRegistry.ForScope`).
- [ ] 10.7 Run the suite with `env -u MEKO_API_KEY` to avoid the Meko live-smoke hang.

## 11. Validation and docs sync

- [ ] 11.1 `openspec validate daemon-scheduler --strict` clean.
- [ ] 11.2 `make build` warning-clean and `make test` green.
- [ ] 11.3 Note in the `daemon/` bucket README that the scheduler is Gateway-process-hosted and document the crontab file location/grammar once confirmed against `daemon-app`.
