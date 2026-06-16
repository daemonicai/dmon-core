## ADDED Requirements

### Requirement: Long-term memory over the Meko data layer

The `Dmon.Memory.Meko` package SHALL provide an `ILongTermMemory` implementation backed by Meko's `memory_*` MCP tools over Streamable HTTP (`mko_tkn_` bearer auth), confining all Meko/MCP coupling behind the `ILongTermMemory` interface so the rest of dmon never sees an `McpClient`. The implementation SHALL map memory operations to Meko tools as: add-fact → `memory_add` (text), record-turns → `memory_add` (messages), search → `memory_search`, get → `memory_get_by_id`, list → `memory_get_all`, update → `memory_update`, delete → `memory_delete_by_id`.

#### Scenario: A fact is persisted and recalled

- **WHEN** a fact is added via `ILongTermMemory.AddFactAsync` and later searched for
- **THEN** the implementation calls `memory_add` to persist it and `memory_search` to recall it
- **AND** no Meko or MCP type crosses the `ILongTermMemory` boundary

### Requirement: Scope maps onto Meko run_id

The implementation SHALL map dmon's `MemoryScope` onto Meko's partitioning: the `Session` scope SHALL use `run_id` set to the dmon session id (normalized to hex, since Meko parses it as `int(x, 16)`); the durable scopes (`Agent`/`User`/`Shared`) SHALL omit `run_id` so recall spans conversations. Meko's `scope` argument SHALL be the fixed string `"admin"`, and `memory_*` calls SHALL supply a `conversation_id` (a UUID from `conversation_create`) created lazily per session and cached.

#### Scenario: Session-scoped recall is partitioned by run_id

- **WHEN** a `Session`-scoped search is issued
- **THEN** the Meko call carries `run_id` = the hex-normalized session id
- **AND** a durable-scoped search omits `run_id`

### Requirement: Turn capture is opt-in

Recording a conversation turn SHALL be gated by a capture mode that defaults to off (`MekoCaptureMode.None`), so a turn is never silently sent for hosted distillation. Adding an explicit fact SHALL always persist regardless of capture mode.

#### Scenario: Capture is off by default

- **WHEN** a turn is recorded with the default capture mode
- **THEN** no `memory_add` (messages) call is made
- **AND** an explicit `AddFactAsync` still calls `memory_add`

### Requirement: Disabled tier and flush semantics

When no API key is configured, registration SHALL fall back to a no-op `ILongTermMemory` (writes no-op, searches return empty) so dmon runs with long-term memory off and the MCP client is never on the critical path. `FlushAsync` SHALL be a best-effort no-op (Meko's `flush_pending_memory_candidates` performs no server-side write; dmon captures explicitly at record time). Long-term memory SHALL NOT be assumed read-your-writes — freshly added memories may not be immediately searchable.

#### Scenario: No key yields the disabled store

- **WHEN** `AddMekoLongTermMemory` is registered with an empty API key
- **THEN** the resolved `ILongTermMemory` is the no-op store
- **AND** searches return empty and writes have no effect

### Requirement: DI registration verb

The package SHALL expose an `AddMekoLongTermMemory(...)` registration verb that configures the endpoint (default `https://mcp.mekodata.ai/mcp`), API key, capture mode, and optional datapack id, and that registers `ILongTermMemory` so the `IMemory` facade (from `Dmon.Memory`) resolves it. Registration order relative to `AddDmonMemory()` SHALL NOT matter.

#### Scenario: The facade picks up the long-term tier

- **WHEN** `AddDmonMemory()` and `AddMekoLongTermMemory(...)` are both registered (in either order) with a valid key
- **THEN** the `IMemory` facade resolves a non-null `LongTerm` tier
- **AND** with no long-term registration, `IMemory.LongTerm` is null and dmon runs short-term-only
