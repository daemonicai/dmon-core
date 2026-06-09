## Why

An external team needs to build an iOS/Swift client against the WebSocket gateway, but the wire contract has no consumer-facing source of truth. The `Command`/`Event`/`Part` types and the `gw` control frames are fully typed in C#, yet there is no published schema a client can codegen from, and the existing `remote-session-gateway` spec describes the protocol as *implementer* requirements rather than a *consumer* contract with concrete frames. Today a client author would have to read C# source and five ADRs to reconstruct the wire format.

## What Changes

- Add a build-time **JSON Schema export** of the `Dmon.Protocol` wire surface — the `Command` and `Event` polymorphic hierarchies, the `Part` union (ADR-016), and the `gw` control frames — produced from the live types via `System.Text.Json`'s `JsonSchemaExporter`, checked into the repo as a versioned artifact stamped with `ProtocolVersion`.
- Add a **freshness gate**: a test that regenerates the schema and fails if the checked-in artifact is stale, so the published contract cannot drift from the types.
- **BREAKING (internal)**: relocate the control-frame DTOs (`AttachFrame`, `AttachedFrame`, `AckFrame`, `CreateFrame`, `CreatedFrame`, `CreateRejectedFrame`, `PingFrame`, `PongFrame`) from `Dmon.Gateway/Protocol/ControlFrames.cs` into `Dmon.Protocol` so they export in the same schema as commands/events and become the single source of truth for codegen. `Dmon.Gateway` references them from their new home. No wire-shape change.
- Add a **client-facing protocol guide** (`docs/protocol/`) that synthesises the consumer contract from the schema and ADR-003/012/014/015/016: connection lifecycle, the `gw` vs `type` channel split, attach/create handshake, per-session `seq` + replay + dedup, generation fencing, heartbeat, id-correlated `ResultEvent`/`CommandErrorEvent`, shared-key `Bearer` auth, and versioning — ending in a worked sequence with raw JSON frames (connect → attach → `turn.submit` → stream → `turnEnd`, plus a reconnect-with-replay).
- Verify `session.getMessages` (already implemented: `SessionMessagesResultEvent` carries the ADR-016 `Part` union) appears correctly in the exported schema; no new payload work is expected.

## Capabilities

### New Capabilities
- `protocol-schema`: A published, versioned, machine-readable description of the dmon wire protocol (commands, events, conversation parts, and gateway control frames), kept in lock-step with the C# types by a build/test gate, plus a consumer-facing protocol guide for client authors.

### Modified Capabilities
<!-- No requirement changes to remote-session-gateway: its behaviour is unchanged; this change describes that behaviour for consumers and moves frame DTOs without altering their wire shape. -->

## Impact

- **Code:** `Dmon.Protocol` gains the relocated control-frame DTOs and (optionally) a small schema-export entry point; `Dmon.Gateway/Protocol/ControlFrames.cs` is removed and its references updated. A new test project / test class owns the freshness gate.
- **Artifacts:** new checked-in `docs/protocol/schema.json` (or per-hierarchy files) and `docs/protocol/*.md` guide.
- **Dependencies:** none new — `JsonSchemaExporter` ships in the .NET 10 `System.Text.Json`.
- **Consumers:** the iOS/Swift client team gains a stable codegen input and a written contract; `ProtocolVersion` (`Major.Minor` = wire protocol, ADR-011) anchors compatibility.
- **No runtime/wire behaviour change** to the gateway or core.
