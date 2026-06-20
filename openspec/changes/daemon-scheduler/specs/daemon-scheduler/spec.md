## ADDED Requirements

### Requirement: A due job fires by injecting a turn into its target session

When a job's scheduled time arrives, the scheduler SHALL submit the job's prompt as a `turn.submit` command into the job's target session through the Gateway's `SessionHandler.WriteToCoreAsync`, so the prompt is processed by the standard turn pipeline as if a client had sent it. The scheduler SHALL NOT introduce a new wire command or bypass the dispatcher.

#### Scenario: Due job submits its prompt as a turn

- **WHEN** a job with prompt `"Summarise today's calendar"` becomes due and its target session has a live handler
- **THEN** a `turn.submit` carrying that prompt is written to that session's core via `WriteToCoreAsync`

#### Scenario: Injection requires no connected client

- **WHEN** a job fires while no WebSocket client is attached to the target session
- **THEN** the turn is still submitted and its resulting events are retained for replay on the next client attach

### Requirement: The scheduler is a resident service hosted in the Gateway process

The scheduler SHALL run as a hosted background service in the Gateway process and resolve `SessionRegistry` from DI, registered by a Daemon-owned host via `AddDaemonScheduler()`. Registering the scheduler SHALL NOT modify the general Gateway `Program.cs` behaviour.

#### Scenario: Scheduler resolves the live session registry

- **WHEN** the Daemon Gateway host starts with `AddDaemonScheduler()` registered
- **THEN** the scheduler runs as a hosted service and obtains the same `SessionRegistry` singleton the Gateway endpoint uses

### Requirement: Jobs are persisted and survive process restart

Agent-authored jobs SHALL be persisted to durable storage in the Daemon data directory and reloaded on startup, including each job's schedule, prompt, target session, scope, enabled flag, and last/next fire times. Store writes SHALL be atomic.

#### Scenario: Agent job survives a restart

- **WHEN** a job is created via the ability tools, the process is restarted, and the scheduler reloads
- **THEN** the job is present with its schedule and next-fire time intact and continues firing on cadence

### Requirement: Jobs can be declared in a crontab-style file

The scheduler SHALL read a declarative crontab-style file from the Daemon data directory at startup and on change. Each line declares a schedule, a scope label, and a prompt. File-declared jobs SHALL be treated as read-only to the agent and SHALL be re-derived from the file rather than copied into the mutable store, so reloading the file is idempotent.

#### Scenario: A crontab line becomes an active job

- **WHEN** the file contains a line declaring a cron schedule, scope `personal`, and a prompt
- **THEN** a corresponding active job exists and fires on that schedule

#### Scenario: Reloading the file does not duplicate jobs

- **WHEN** the file is reloaded with unchanged content
- **THEN** the active job set is unchanged and no duplicate jobs are created

### Requirement: The agent can manage jobs through personal-scope abilities

The scheduler SHALL expose `schedule_task`, `list_tasks`, and `cancel_task` as tools on an `IAbilityProvider` declaring scope `"personal"`, registered via `AddAbilities<T>()`. These tools SHALL appear only in the manifest for the `"personal"` scope.

#### Scenario: Scheduling tool creates a persisted job

- **WHEN** the agent calls `schedule_task` with a schedule and a prompt
- **THEN** a new agent-sourced job is persisted and begins firing on that schedule

#### Scenario: Cancel removes an agent job

- **WHEN** the agent calls `cancel_task` with the id of an existing agent-sourced job
- **THEN** that job is removed from the store and no longer fires

#### Scenario: Scheduling abilities are personal-scope only

- **WHEN** the ability manifest is built for a scope other than `"personal"`
- **THEN** `schedule_task`, `list_tasks`, and `cancel_task` are absent from that manifest

### Requirement: File and agent sources are merged with file precedence

The active job set SHALL be the union of file-declared and agent-authored jobs keyed by a stable job id. File-declared jobs SHALL use a deterministic id derived from their declaration. On id collision between the two sources, the file-declared job SHALL win.

#### Scenario: Both sources contribute jobs

- **WHEN** one job is declared in the file and one created by the agent
- **THEN** both are active and fire independently on their own schedules

#### Scenario: File declaration wins a collision

- **WHEN** a file-declared job and an agent job resolve to the same id
- **THEN** the file-declared job is the one that fires

### Requirement: Schedules support cron expressions and fixed intervals

A job's schedule SHALL be expressible either as a cron expression or as a fixed interval. Schedules requiring sub-minute precision SHALL be rejected at load with an error that does not stop other jobs from loading.

#### Scenario: Cron schedule computes the correct next fire

- **WHEN** a job declares a daily cron schedule
- **THEN** its computed next-fire time is the next matching instant in the host's local time zone

#### Scenario: Sub-minute schedule is rejected

- **WHEN** a job declares an interval shorter than one minute
- **THEN** that job is rejected at load and the remaining jobs still load

### Requirement: Missed fires during downtime coalesce to a single catch-up

If a job's scheduled time passed one or more times while the scheduler was not running, the job SHALL fire at most once on restart as a catch-up and then resume its normal cadence. Missed occurrences SHALL NOT each produce a separate turn.

#### Scenario: One catch-up after a long downtime

- **WHEN** a daily job's fire time was missed on three consecutive days while the process was down, and the scheduler restarts
- **THEN** the job fires exactly once as a catch-up and its next fire is the next scheduled occurrence

### Requirement: A fire is deferred while a turn is already running

Before injecting, the scheduler SHALL check whether the target session already has a turn in progress. If so, the fire SHALL be deferred to a later tick rather than submitted concurrently or queued unboundedly.

#### Scenario: Fire defers during an active turn

- **WHEN** a job becomes due while its target session is mid-turn
- **THEN** the scheduler does not submit the turn at that tick and re-evaluates the job on a later tick

### Requirement: Each job carries a scope applied to its injected turn

Each job SHALL carry a scope label, and the turn it injects SHALL be associated with that scope so the Daemon's routing gates abilities for that turn exactly as for a client turn of the same scope.

#### Scenario: Personal-scope job injects a personal-scope turn

- **WHEN** a job declared with scope `personal` fires
- **THEN** the injected turn is treated as `personal` scope by the Daemon routing

### Requirement: Missing target sessions and failing fires never crash the scheduler

If a job's target session has no live handler at fire time, the scheduler SHALL attempt to ensure the session and, failing that, SHALL skip the fire and log it. Any exception raised while firing a job SHALL be isolated to that job and logged; the scheduler SHALL continue evaluating other jobs.

#### Scenario: Absent session is skipped, not fatal

- **WHEN** a job fires but its target session cannot be found or ensured
- **THEN** the fire is skipped and logged and the scheduler continues running other jobs

#### Scenario: A throwing fire is isolated

- **WHEN** submitting one job's turn throws
- **THEN** the error is logged and the scheduler still evaluates and fires other due jobs

### Requirement: Disabled jobs do not fire

A job whose enabled flag is false SHALL NOT fire, regardless of its schedule, while remaining persisted so it can be re-enabled.

#### Scenario: Disabled job is skipped

- **WHEN** a job is disabled and its scheduled time arrives
- **THEN** no turn is injected for that job and the job remains in the store
