## ADDED Requirements

### Requirement: Implicit session creation on first turn

The agent core SHALL NOT execute a turn that cannot be persisted. When a turn is submitted and there is no active session, the core SHALL create a session and set it as the active session **before** the turn's messages are persisted, so that `session-storage`'s unconditional requirement to append every completed turn to `messages.jsonl` holds for every turn without exception. The core SHALL NOT silently skip persistence for want of an active session.

Creation SHALL be lazy: the core SHALL NOT create a session at startup, nor at any point before a turn actually requires one. A core that is started and never asked to run a turn SHALL leave no session directory behind.

The implicitly created session SHALL be bound to the agent the core is already running, and SHALL be indistinguishable, once created, from a session created by an explicit `session.create` command — same on-disk layout, same activation semantics, same eligibility for `session.fork`, `session.load` and compaction.

When the core creates a session implicitly it SHALL emit a `sessionStarted` event carrying the new `SessionMeta`, so a host learns the identity of a session it never requested.

#### Scenario: Turn submitted with no active session

- **WHEN** the core receives `turn.submit` and `ISessionHandler.CurrentSession` is null
- **THEN** the core creates a session, sets it active, emits `sessionStarted` carrying its `SessionMeta`, and the completed turn's messages are appended to that session's `messages.jsonl`

#### Scenario: Turn submitted with an active session

- **WHEN** the core receives `turn.submit` and a session is already active
- **THEN** no session is created, no `sessionStarted` event is emitted, and the turn is persisted to the already-active session

#### Scenario: Core started but never asked to run a turn

- **WHEN** a core process starts and exits without any `turn.submit` being received
- **THEN** no session directory is created

#### Scenario: Gateway create handshake is unaffected

- **WHEN** a client creates a session through the gateway's two-step `session.create` → path-less `session.load` handshake and then submits a turn
- **THEN** the session is already active, so no implicit creation occurs and no `sessionStarted` event is emitted

#### Scenario: Implicit session supports the same operations as an explicit one

- **WHEN** a session created implicitly is subsequently the target of `session.fork` or `session.load`
- **THEN** the operation succeeds exactly as it would for a session created by `session.create`

### Requirement: Session started event

The agent core SHALL expose `sessionStarted` on the RPC surface as a **non-command** event carrying `SessionMeta`. It SHALL NOT be a `ResultEvent`: it has no originating command and therefore no command `id` to correlate to, and per ADR-015 a `ResultEvent`'s `id` correlates to the command that produced it. `sessionStarted` SHALL be emitted only for sessions the core creates on its own initiative; a session created by an explicit `session.create` command SHALL continue to be reported by `session.createResult` and SHALL NOT additionally emit `sessionStarted`.

The event SHALL appear in the machine-readable wire-protocol schema export, and the schema freshness gate SHALL fail if the export omits it.

Because a gateway-fronted session is always made active by the gateway's own `session.create` → path-less `session.load` handshake before any `turn.submit` is accepted, the core SHALL NOT emit `sessionStarted` on that path at all. Gateway clients are therefore not exposed to the event by construction, for as long as that handshake guarantee holds; hosts speaking JSONL/stdio to the core directly are the only ones that receive it.

#### Scenario: Implicit creation emits the non-command event

- **WHEN** the core creates a session implicitly
- **THEN** it emits `sessionStarted` carrying the new `SessionMeta`, and the event is not a `ResultEvent` and carries no command correlation id

#### Scenario: Explicit creation does not emit it

- **WHEN** a host sends `session.create` and the core creates the session
- **THEN** the core emits `session.createResult` correlated to the command id, and does not emit `sessionStarted`

#### Scenario: Event appears in the exported schema

- **WHEN** the wire-protocol schema export is regenerated
- **THEN** it declares the `sessionStarted` event, and the freshness gate fails if it does not

#### Scenario: Unknown-event tolerance is preserved

- **WHEN** a host that does not recognise `sessionStarted` receives it while awaiting the result of an unrelated command
- **THEN** the event is ignored by the request/response correlation path and the pending command still completes normally
