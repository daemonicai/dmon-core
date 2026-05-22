# Binding ADRs

All in `docs/adrs/`. Accepted ADRs cannot be contradicted by code. To revisit, write a superseding ADR first.

| ADR | Decision |
|-----|----------|
| ADR-001 | `IChatClient` (M.E.AI) for LLM. No MAF dependency. |
| ADR-002 | Extensions expose `AIFunction` via `IDaemonExtension`. No wrapper interface. |
| ADR-003 | JSONL over stdio, Pi-compatible shape. Not JSON-RPC 2.0. Commands: `{id, type, ...params}`. Events: `{id, event, ...payload}`. |
| ADR-004 | Session = relocatable dir. `messages.jsonl` append-only. Large outputs in `attachments/`. SQLite index is a cache, not source of truth. |
| ADR-005 | API keys only (env or config file). No OAuth V1. |
| ADR-006 | Conservative permissions. Read inside CWD implicit; all writes prompt; tree-based grants. |
