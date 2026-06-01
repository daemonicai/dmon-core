# DEVLOG: typed-command-result-events

Retire the generic `{type:"response", data}` envelope; command results become dedicated typed events correlated by command `id` (ADR-015).

## 1. Protocol foundations (additive)

- Added `ResultEvent : Event` (abstract, `[JsonPropertyName("id")] CommandId`), `CommandErrorEvent`, six `Session*ResultEvent` types, and the `SessionStats` record; registered 7 `[JsonDerivedType]` discriminators on `Event.cs` (`commandError`, `session.{create,fork,clone,load,list,getStats}Result`).
- **Decision (orchestrator):** moved `SessionMeta`/`SessionTokens`/`SessionCost` from `Dmon.Core.Session` to `Dmon.Protocol.Sessions`. `Dmon.Protocol` is a leaf project (no project refs) and the typed session events must reference `SessionMeta`; it's a pure wire-contract DTO so it belongs in the contract layer. Every `[JsonPropertyName]` preserved byte-identically (meta.json/wire unchanged); ~14 referencing files updated. Reviewer confirmed the move is byte-identical and Protocol is still a leaf.
- **Decision:** bumped `ProtocolVersion.Current` `0.1→0.2` *and* `Directory.Build.props` `MinVerMinimumMajorMinor` `0.1→0.2` — the latter is an ADR-011 version-skew guard that compares the MinVer tag's Major.Minor against `ProtocolVersion.Current`; both must move together.
- **Gate hiccup (resolved):** a stale `bin/Debug/dmoncore.dll` (27 May) made the `AgentReady` integration test read `1.0` because `CoreProcessFixture.FindCoreDll()` probes Debug before Release. Fixed by `dotnet clean -c Debug` + rebuild. The Debug-before-Release probe is a **latent harness bug** (flagged by reviewer) — out of scope here; see NEXT.
- Updated four version-pinned tests for the `0.2` bump (`ProtocolVersionTests` ×3, `CoreResolverTests.ResolveAsync_CacheHit_DoesNotCallNetwork`); they now derive the expected version from `ProtocolVersion.Current` so they won't break on the next bump.
- Gates: `make build` 0/0; full `make test` green (all projects, 0 failures, 2 pre-existing skips); `openspec validate --strict` valid.

## NEXT

- **Up next:** Group 2 — Session handler migration + retire `ResponseEvent`: rewrite `SessionHandler` emit sites to the typed events + `CommandErrorEvent`, quarantine `session.getMessages` on the legacy path, delete dead `ResponseEvent` usage, add wire-shape tests.
- **Open questions:** none.
- **Nits / deferred:** (1) `session.getMessages` stays on legacy `ResponseEvent` until the conversation-persistence change un-quarantines it. (2) **Harness follow-up (out of scope for this change):** `CoreProcessFixture.FindCoreDll()` prefers `bin/Debug` over `bin/Release` under `dotnet test -c Release`, silently running stale binaries — should prefer the configuration under test or clean/gitignore `bin/Debug`.
- **Carry-forward:** apply runs on `change/typed-command-result-events`; `change/conversation-persistence` is stacked on top and applies second.
