## 1. Relocate control frames into Dmon.Protocol

- [x] 1.1 Move `ControlFrames.cs` from `Dmon.Gateway/Protocol/` into `Dmon.Protocol` (e.g. `Dmon.Protocol/Gateway/ControlFrames.cs`), updating the namespace
- [x] 1.2 Update `Dmon.Gateway` (`GatewayConnectionEndpoint`, `WebSocketGatewayConnection`, `SessionHandler`, and any tests) to reference the frames from their new namespace
- [x] 1.3 Confirm `make build` is clean and the existing gateway tests pass — the move is namespace-only with no wire-shape change

## 2. Schema export

- [x] 2.1 Locate the canonical `JsonSerializerOptions` used on the wire by the gateway/core; expose or reuse them so the exporter and the runtime share one configuration
- [x] 2.2 Add a schema-export routine in `Dmon.Protocol` that calls `JsonSchemaExporter.GetJsonSchema(...)` for `Command`, `Event`, `Part`, and the control-frame family, composing one document with shared leaves in `$defs` and a top-level `x-protocolVersion` from `ProtocolVersion.Current`
- [x] 2.3 Make the output deterministically ordered so regeneration produces stable diffs
- [x] 2.4 Add a generator entry point (`make schema` target driving a `dotnet run` console exporter or equivalent) that writes `docs/protocol/schema.json`
- [x] 2.5 Generate and commit the initial `docs/protocol/schema.json`

## 3. Freshness gate

- [x] 3.1 Add a test in the protocol test project that regenerates the schema in-memory and asserts byte-equality with the committed `docs/protocol/schema.json`
- [x] 3.2 On mismatch, emit an actionable message naming the regeneration command (`make schema`)
- [x] 3.3 Verify the gate fails on a deliberate type change and passes once the artifact is regenerated

## 4. Schema content verification

- [x] 4.1 Assert all `Command` and `Event` leaves and all `Part` leaves appear in the schema, keyed by their `type` discriminator, in camelCase
- [x] 4.2 Assert the eight `gw` control frames appear keyed by their `gw` discriminator, as a family distinct from the `type` channel
- [x] 4.3 Assert `session.getMessages`'s result references the `Part` union, and assert no third-party (`Microsoft.Extensions.AI`) type names leak into the schema

## 5. Consumer-facing protocol guide

- [x] 5.1 Write `docs/protocol/README.md` (or `guide.md`): connection lifecycle, `gw` vs `type` channel routing, attach/create handshakes, `seq`/replay/dedup, generation fencing, heartbeat, id-correlated `ResultEvent`/`CommandErrorEvent`, shared-key `Bearer` auth, `ProtocolVersion`
- [x] 5.2 Reference `remote-session-gateway` requirements and ADR-003/012/014/015/016 as the normative source instead of restating them; point at `schema.json` as the type source of truth
- [x] 5.3 Add the worked frame-sequence example: connect → `attach` → `turn.submit` → streamed message/turn events → `turnEnd`, with raw JSON frames consistent with the schema
- [x] 5.4 Add the reconnect-with-replay example: `attach` with non-zero `lastSeq` and replayed events with greater `seq`, in order

## 6. Gates

- [ ] 6.1 `make build` clean (no warnings; `TreatWarningsAsErrors`)
- [ ] 6.2 `make test` green (new schema/freshness tests and all existing tests)
- [ ] 6.3 `openspec validate protocol-schema-export --strict`
