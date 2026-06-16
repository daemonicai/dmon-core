# Dmon.Memory.Meko

Durable, cross-session **long-term memory** for the [dmon](https://github.com/daemonicai/dmon-core) coding agent, backed by the [Meko](https://mekodata.ai) agent-native data layer.

This package implements dmon's `ILongTermMemory` over Meko's `memory_*` MCP tools (Streamable HTTP, `mko_tkn_` bearer auth). All Meko/MCP coupling is confined behind the interface — the rest of dmon never sees an `McpClient`. It pairs with the in-process short-term tier (`Dmon.Memory`) behind the `IMemory` facade.

---

## How it works

| `ILongTermMemory` | Meko tool |
|-------------------|-----------|
| `AddFactAsync` | `memory_add` (text) |
| `RecordAsync` | `memory_add` (messages) — gated by opt-in capture |
| `SearchAsync` | `memory_search` |
| `GetAsync` | `memory_get_by_id` |
| `ListAsync` | `memory_get_all` |
| `UpdateAsync` | `memory_update` |
| `DeleteAsync` | `memory_delete_by_id` |
| `FlushAsync` | *(best-effort no-op)* |

Behaviour notes:

- **Scope → `run_id`.** Meko's `scope` is the fixed string `"admin"`; partitioning is by `agent_id` + `run_id`. dmon's `Session` scope maps to `run_id` = the session id (hex-normalized); durable scopes (`Agent`/`User`/`Shared`) omit `run_id` for cross-conversation recall.
- **Opt-in capture.** `RecordAsync` is gated by `MekoCaptureMode` (`None` by default), so recording a turn never silently incurs hosted distillation cost. `AddFactAsync` always persists.
- **Eventual consistency.** Long-term memory is *not* read-your-writes — freshly added memories may not be immediately searchable.
- **Disabled tier.** With no API key, registration falls back to a no-op store (writes no-op, searches return empty); the MCP client is never on the critical path.

## Configuration & DI

```csharp
services.AddDmonCore()
        .AddDmonMemory()                 // short-term tier (Dmon.Memory)
        .AddMekoLongTermMemory(options =>
        {
            options.ApiKey      = configuration["Meko:ApiKey"];   // mko_tkn_...
            options.Endpoint    = "https://mcp.mekodata.ai/mcp";  // default
            options.CaptureMode = MekoCaptureMode.None;           // opt-in
            // options.DatapackId = "<uuid>";                     // optional
        });
```

Registration order relative to `AddDmonMemory()` does not matter — the `IMemory` facade resolves `ILongTermMemory` at construction. If `ApiKey` is empty, the disabled (no-op) store is used and dmon runs short-term-only.

## License

MPL-2.0 — see the repository [`LICENSE`](https://github.com/daemonicai/dmon-core/blob/main/LICENSE).
