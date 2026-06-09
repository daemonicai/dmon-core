## ADDED Requirements

### Requirement: Machine-readable wire-protocol schema export
The project SHALL produce a machine-readable JSON Schema describing the client-visible wire surface — the `Command` and `Event` polymorphic hierarchies, the conversation `Part` union (ADR-016), and the gateway control frames — generated from the live `Dmon.Protocol` types via `System.Text.Json`'s `JsonSchemaExporter`. The exporter SHALL use the same `JsonSerializerOptions` (camelCase naming, configured polymorphism) that the gateway and core use on the wire, so the schema reflects the bytes actually emitted. The schema SHALL be a dmon-owned surface only and SHALL NOT expose `Microsoft.Extensions.AI` or other third-party types.

#### Scenario: Every command and event leaf is present
- **WHEN** the schema is generated
- **THEN** every `[JsonDerivedType]` leaf of `Command` and of `Event` appears, keyed by its `type` discriminator, with its properties in camelCase

#### Scenario: Conversation parts are present
- **WHEN** the schema is generated
- **THEN** the `Part` union leaves (`text`, `toolCall`, `toolResult`, `image`, `reasoning`, `usage`, `unknown`) appear and are referenced by the `session.getMessages` result event's `messages` payload

#### Scenario: No third-party types leak
- **WHEN** the schema is inspected
- **THEN** no `Microsoft.Extensions.AI` or other non-dmon type names appear in it

### Requirement: Control frames exported alongside commands and events
The gateway control-frame DTOs (`attach`, `attached`, `ack`, `create`, `created`, `createRejected`, `ping`, `pong`) SHALL reside in the `Dmon.Protocol` assembly and SHALL be included in the same exported schema as commands and events. The schema SHALL present them as a distinct family discriminated by the `gw` field, separate from the ADR-003 `type` channel, so a client can route a `gw`-bearing frame to the control path and any other frame to the ADR-003 command/event path. Relocating the DTOs SHALL NOT change their wire shape.

#### Scenario: Control frames appear in the schema
- **WHEN** the schema is generated
- **THEN** the eight `gw` control frames appear, each keyed by its `gw` discriminator value, separate from the `type`-keyed command and event families

#### Scenario: Wire shape unchanged after relocation
- **WHEN** a control frame is serialized after the DTOs are moved into `Dmon.Protocol`
- **THEN** the JSON is byte-identical to the shape produced before the move

### Requirement: Schema is versioned and checked in
The exported schema SHALL be committed to the repository as a stable artifact under `docs/protocol/` and SHALL carry the current wire-protocol version from `ProtocolVersion.Current` (ADR-011, `Major.Minor` = wire protocol). Consumers and reviewers SHALL be able to read and diff the contract without building the solution.

#### Scenario: Version stamp present
- **WHEN** the checked-in schema is opened
- **THEN** it carries a protocol-version field equal to `ProtocolVersion.Current`

#### Scenario: Protocol change is visible in the diff
- **WHEN** a wire type is added, removed, or its properties change, and the schema is regenerated
- **THEN** the committed schema artifact changes correspondingly in the same commit

### Requirement: Freshness gate prevents schema drift
A test SHALL regenerate the schema from the live types and fail when it does not match the checked-in artifact, so the published contract cannot silently drift from the C# types. The failure message SHALL tell the developer how to regenerate the artifact.

#### Scenario: Stale artifact fails the build
- **WHEN** a wire type changes but the checked-in schema is not regenerated
- **THEN** the freshness test fails with a message instructing the developer to regenerate the schema

#### Scenario: Fresh artifact passes
- **WHEN** the checked-in schema matches the live types
- **THEN** the freshness test passes

### Requirement: Consumer-facing protocol guide
The project SHALL provide a client-facing protocol guide under `docs/protocol/` that describes, for an author building a client, how to consume the wire protocol: the connection lifecycle; the `gw` control channel versus the ADR-003 `type` channel; the attach and create handshakes; per-session `seq`, replay-on-reattach, and client-side dedup; generation fencing; `ping`/`pong` heartbeat; id-correlated `ResultEvent` and `CommandErrorEvent` (ADR-015); shared-key `Bearer` authentication on the WebSocket upgrade; and protocol versioning. The guide SHALL reference the `remote-session-gateway` requirements and ADR-003/012/014/015/016 as the normative source rather than restating them, and SHALL point at the exported schema as the type source of truth.

#### Scenario: Guide covers the control and ADR-003 channels
- **WHEN** a client author reads the guide
- **THEN** it explains how to distinguish a `gw` control frame from an ADR-003 command/event and how to route each

#### Scenario: Guide links rather than restates normative behaviour
- **WHEN** the guide describes replay, fencing, or auth
- **THEN** it cites the `remote-session-gateway` requirement or the relevant ADR as authoritative instead of duplicating the normative text

### Requirement: Worked frame-sequence example
The protocol guide SHALL include at least one worked end-to-end example as a sequence of raw JSON frames covering connect, `attach`, a `turn.submit` and its streamed events through `turnEnd`, and a reconnect that replays missed events by `seq`. The example frames SHALL be consistent with the exported schema.

#### Scenario: Happy-path turn is shown as raw frames
- **WHEN** a client author reads the worked example
- **THEN** it shows the literal JSON for attach, the submitted turn command, the streamed message/turn events, and the terminal `turnEnd`, each shape valid against the schema

#### Scenario: Reconnect-with-replay is shown
- **WHEN** the worked example covers reconnection
- **THEN** it shows an `attach` carrying a non-zero `lastSeq` and the replayed events with `seq` greater than that value, in order
