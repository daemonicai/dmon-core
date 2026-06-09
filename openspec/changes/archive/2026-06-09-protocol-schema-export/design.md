## Context

The dmon wire protocol is fully typed in `Dmon.Protocol`: `Command` (31 leaves) and `Event` (41 leaves) are `[JsonPolymorphic]` hierarchies discriminated by `type`; the ADR-016 `Part` union is discriminated the same way; the gateway `gw`-channel control frames live in `Dmon.Gateway/Protocol/ControlFrames.cs`. Serialization is `System.Text.Json`, camelCase, no opaque `object` payloads (ADR-015). The behavioural contract — attach/replay/fencing/heartbeat/auth — is already specified by `remote-session-gateway`.

What is missing is anything a *client author* can consume: there is no machine-readable schema to codegen Swift `Codable` types from, the control frames sit outside the protocol assembly so they would not export alongside commands/events, and the existing spec reads as gateway-implementer requirements, not a consumer-facing frame reference.

Constraints: `TreatWarningsAsErrors` is on; no new third-party dependency (ADR-001 spirit; `JsonSchemaExporter` is in-box on .NET 10); the wire shapes must not change (byte-compatibility with ADR-003 and the existing gateway spec is required); contracts use dmon-owned types only (no M.E.AI types in the exported surface).

## Goals / Non-Goals

**Goals:**
- A single, versioned, checked-in machine-readable description of the entire client-visible wire surface (commands, events, parts, control frames).
- A test gate that fails when the checked-in schema drifts from the C# types.
- Control frames living in `Dmon.Protocol` so the export is one source of truth.
- A consumer-facing protocol guide with a worked, copy-pasteable JSON frame sequence.

**Non-Goals:**
- Generating Swift code in this repo. We publish the schema + guide; the client team owns codegen (quicktype or hand-written `Codable`).
- Changing any wire shape, gateway behaviour, or core behaviour.
- An OpenAPI/Swagger document or a hosted docs site.
- Re-specifying gateway behaviour — `remote-session-gateway` stays authoritative; the guide references it.

## Decisions

### D1: Export with `System.Text.Json`'s `JsonSchemaExporter`, not a third-party generator
`JsonSchemaExporter.GetJsonSchema(options, typeof(T))` walks the same `[JsonPolymorphic]`/`[JsonDerivedType]`/`[JsonPropertyName]` metadata the runtime serializes with, so the exported schema is correct by construction and stays aligned with naming-policy and polymorphism settings. The exporter must be driven by the **same `JsonSerializerOptions`** the gateway/core use on the wire (camelCase, the configured polymorphism), so the schema reflects real bytes. *Alternative considered:* reflection-based or NJsonSchema export — rejected: another dependency, and a second interpretation of the type model that can disagree with `System.Text.Json`'s actual output.

### D2: Export the four hierarchies as a single artifact with `$defs`
Emit one `docs/protocol/schema.json` whose top level is a `oneOf` over the root unions (`Command`, `Event`) plus the `Part` union and the control-frame records, with shared leaves in `$defs`. A single file is the simplest codegen input and keeps cross-references (e.g. an event embedding a `Part`) resolvable. The artifact carries a top-level `x-protocolVersion` stamped from `ProtocolVersion.Current` (ADR-011). *Alternative considered:* one file per hierarchy — rejected as harder to cross-reference for marginal tidiness; revisit only if a single file proves unwieldy.

### D3: Generate at test time and diff against the checked-in file (freshness gate)
A test in the protocol test project regenerates the schema in-memory and asserts byte-equality with the committed `docs/protocol/schema.json`, emitting a clear "run `make schema` to refresh" message on mismatch. The artifact is committed (so external consumers and PR reviewers see protocol diffs without a build), and the test guarantees it can never silently drift. A `make schema` target (or a `dotnet run` exporter entry point) writes the file. *Alternative considered:* generate purely at build time into `build/` and never commit — rejected: consumers and reviewers lose a stable, reviewable contract, and protocol changes become invisible in diffs.

### D4: Relocate control frames into `Dmon.Protocol`
Move `ControlFrames.cs` into `Dmon.Protocol` (e.g. `Dmon.Protocol/Gateway/ControlFrames.cs`) so the `gw`-channel DTOs export in the same pass as commands/events. `Dmon.Gateway` references them from the new namespace; this is a namespace move with no wire-shape change. The `gw` frames are deliberately a **separate** discriminator channel (`gw` vs `type`) and the schema/guide must present them as a distinct family so a client routes `gw`-bearing frames to the control path and everything else to the ADR-003 path. *Alternative considered:* leave them in `Dmon.Gateway` and have the exporter reference both assemblies — rejected: two export sources and a `Dmon.Protocol`→consumer story that omits the frames a client needs first.

### D5: The guide is hand-written prose + generated frames, the schema is generated
Behavioural semantics (ordering, replay seam, fencing, parking) can't be expressed in JSON Schema, so the guide carries them, cross-linking `remote-session-gateway` and ADR-003/012/014/015/016 rather than restating them. The worked example's JSON frames should be real serializer output where practical so they can't drift in shape.

## Risks / Trade-offs

- **Schema churn noise in diffs** → the freshness test makes protocol changes explicit and reviewable; that visibility is the point, not a cost. Keep the exporter output deterministically ordered so unrelated diffs don't appear.
- **`JsonSchemaExporter` can't express every constraint** (e.g. `required` semantics on polymorphic bases, discriminator-as-`const`) → document the discriminator convention in the guide; if a leaf needs a constraint the exporter drops, note it in prose rather than hand-editing the generated file (hand edits would fail the freshness gate).
- **Relocating frames touches `Dmon.Gateway`** → pure namespace move guarded by the existing gateway tests; no behavioural test should change.
- **Exporter options drift from real wire options** → source the `JsonSerializerOptions` from the same place the gateway/core configure them (or assert they match) so the schema can't describe bytes that aren't emitted.
- **Guide prose drifts from spec** → the guide references `remote-session-gateway` requirements by name instead of paraphrasing normative behaviour.

## Open Questions

- Single `schema.json` vs per-hierarchy files — provisionally D2 (single file); revisit if codegen tooling on the client side prefers split inputs.
- Whether the exporter entry point is a tiny `dotnet run` console target or a `[Fact]`-driven writer invoked by `make schema` — settle during tasks; both satisfy the gate.
