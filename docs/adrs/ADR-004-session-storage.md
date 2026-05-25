# ADR-004: Session Storage

**Date:** 2026-05-22
**Status:** Accepted

## Context

Sessions need to be portable — copyable, shareable, forkable mid-conversation. The brief commits to a session-as-relocatable-directory model (from Pi). The decisions here concern what lives inside that directory, how large outputs are handled, how compaction works, and where sessions are stored.

## Decision

### Session directory layout

Each session is a self-contained directory:

```
<session-id>/
  messages.jsonl     — append-only conversation log
  meta.json          — metadata and running totals
  attachments/       — large tool outputs stored as files
    <callId>.txt
    <callId>.png
    ...
```

### `messages.jsonl`

Append-only. One JSON object per line. Contains `UserMessage`, `AssistantMessage`, `ToolResultMessage`, and `CompactionMessage` records (see below). Large tool outputs are stored in `attachments/` and referenced by path; the inline `content` field holds a truncation notice and the attachment path.

### `meta.json`

```json
{
  "id": "<uuid>",
  "name": "...",
  "created": "<iso8601>",
  "modified": "<iso8601>",
  "parentSession": "<uuid | null>",
  "forkEntryId": "<entryId | null>",
  "model": { ... },
  "tokens": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0 },
  "cost": { "total": 0.0 }
}
```

### Attachment threshold

Tool outputs larger than a configurable threshold are written to `attachments/` rather than inlined in `messages.jsonl`. The threshold is read from `IConfiguration` under `Daemon:Session:AttachmentThresholdBytes`, with a default of `1024` (1 KiB).

### Compaction

Compaction is non-destructive. Original messages are never deleted. When compaction occurs, a `CompactionMessage` is appended to `messages.jsonl`:

```json
{
  "type": "compaction",
  "entryId": "<uuid>",
  "timestamp": "<iso8601>",
  "summary": "...",
  "supersedesUpTo": "<entryId>",
  "reason": "manual | threshold | overflow",
  "tokensBefore": 0
}
```

Readers skip all messages with an `entryId` ≤ `supersedesUpTo` and treat the compaction summary as the effective start of the conversation. Multiple compaction markers may be present; the last one wins. The full original history remains available for export or inspection.

### Global index

A SQLite database at `<root>/sessions.db` maintains a lightweight index for fast session listing without reading every `meta.json`:

```
sessions (id, name, path, created, modified, parentSession)
```

If the index is lost or corrupted it can be fully rebuilt by scanning session directories. The index is never the source of truth for session content.

### Session location

Sessions are stored **project-local by default**: the agent walks up the directory tree from the working directory looking for a `.dmon/` directory, exactly as git does for `.git/`. If found, sessions are stored in `.dmon/sessions/`. If no `.dmon/` directory is found, the agent falls back to `~/.dmon/sessions/`.

A project can opt out of local storage by setting `sessionStore: global` in `.dmon/config.yaml`, which redirects all sessions to `~/.dmon/sessions/`. A custom path is also accepted (`sessionStore: /path/to/sessions`).

```
Discovery order:
  1. Walk up from CWD for .dmon/config.yaml → read sessionStore
  2. If sessionStore: local (default) → .dmon/sessions/ in that directory
  3. If sessionStore: global            → ~/.dmon/sessions/
  4. If sessionStore: <path>            → that path
  5. If no .dmon/ found anywhere     → ~/.dmon/sessions/
```

### Forking

```
Fork session <A> at entryId "e42" → new session <B>:
  1. cp -r sessions/<A> sessions/<B>
  2. Scan messages.jsonl; truncate after the line containing "e42"
  3. Rewrite meta.json: new id, parentSession = <A>, forkEntryId = "e42"
  4. Upsert into sessions.db
```

The scan-and-truncate is a linear pass over `messages.jsonl`. For sessions of reasonable size (before compaction threshold) this is fast enough to require no indexing. An offset index may be added later if profiling shows it is necessary.

## Consequences

- **Sessions are human-readable.** `messages.jsonl` can be read with standard tools. Attachments are plain files.
- **Sessions are portable.** A session directory can be `cp`-ed, zipped, and shared. Nothing outside the directory is required to read it.
- **Compaction is non-destructive.** Original turns are never deleted. The full history is always recoverable.
- **Project-local default means session history travels with the repo.** This is intentional for a coding agent — conversation context about a codebase is part of the project.
- **The SQLite index is a cache, not a store.** Loss or corruption has no effect on session content.
- **Attachment threshold is runtime-configurable.** Teams with slow storage or very large tool outputs can tune without recompiling.
