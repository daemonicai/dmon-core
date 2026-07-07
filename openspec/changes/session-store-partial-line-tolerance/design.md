## Context

`core/Dmon.Core/Session/SessionStore.cs` exposes two readers over the same `messages.jsonl` file:

- `ReadRecordsAsync` → `IReadOnlyList<SessionLogLine>` (typed records; consumed by `SessionHandler`/`TurnHandler`). It reads all lines, then deserializes each inside a `try { … } catch (JsonException) { /* skip */ }` loop — **tolerant**.
- `ReadMessagesAsync` → `IReadOnlyList<object>` of raw `JsonElement` (the `IShortTermMemory.ReadMessagesAsync` path). It deserializes each retained line with a bare `JsonSerializer.Deserialize<JsonElement>(l)` at three points — **fragile**. A single bad line throws `JsonException` and the whole read fails.

`MessageAppender.WriteLineAsync` (`MessageAppender.cs:53-63`) appends `json + "\n"` over a `FileMode.Append` stream and returns once the async write completes; there is no `FileStream.Flush(true)`/`fsync`. So after a crash mid-append, a **partial trailing line** is an expected on-disk state. The two readers currently disagree about whether that state is survivable.

Within `ReadMessagesAsync`, the three deserialize sites are: (1) the no-compaction early return (`lines.Select(l => Deserialize(l))`), (2) the "no compaction marker found" early return (same shape), and (3) the final materialization loop that also applies the compaction skip window. The compaction logic itself already tolerates malformed lines — the marker scan (`Contains("\"type\":\"compaction\"")` → `try Deserialize<CompactionMessage>`) and the `supersedesUpTo` scan (`try JsonDocument.Parse`) both `catch (JsonException)` and keep scanning. Only the final materialization of *retained* lines is unguarded.

## Goals / Non-Goals

**Goals:**
- `ReadMessagesAsync` skips any line that fails to deserialize instead of throwing, so a partial/malformed line cannot lock a session out of its own history.
- Behaviour matches `ReadRecordsAsync`'s existing crash model for the same file.
- Compaction output is unchanged for well-formed input.

**Non-Goals:**
- Adding `fsync`/durability barriers to the append path (a separate durability concern; the fix is to make reads tolerant of the *current* non-fsync'd model, per the audit).
- Changing method signatures, the `IShortTermMemory` contract, the wire protocol, or the on-disk record format.
- Repairing or truncating the file on read (reads stay read-only; a bad line is skipped in-memory only).
- Emitting new events/telemetry for skipped lines (a Debug log line is acceptable but not required).

## Decisions

**D1 — Skip malformed lines at materialization, not by pre-filtering `lines`.** Deserialization stays where it is (the three retained-line sites); each gains a `try/catch (JsonException) → skip`. The raw `lines` list keeps every line (including the malformed one) so the compaction index arithmetic — `lastCompactionIndex` and `supersedesUpToIndex`, both computed as positions in `lines` — remains correct. Alternative considered: filter `lines` to well-formed entries up front, then index into the filtered list. Rejected: it would shift every index and force the compaction scans to be reworked against the filtered list, a larger and riskier change for no benefit. A partial line is only ever the *last* line, so skipping it at materialization never perturbs an earlier compaction/supersedes position anyway; guarding all three sites also covers a malformed *interior* line defensively.

**D2 — Factor the guarded deserialize once.** The three sites become one small local helper (e.g. `static bool TryDeserializeElement(string line, out JsonElement element)`), so the tolerance rule lives in exactly one place and the two early-return `.Select(...)` expressions and the final loop all route through it. Alternative: inline the `try/catch` three times. Rejected: duplication invites the three sites drifting apart again — which is the exact bug being fixed.

**D3 — Silent skip, matching `ReadRecordsAsync`.** `ReadRecordsAsync` swallows the `JsonException` with only a comment. For consistency `ReadMessagesAsync` does the same. An optional `_logger.LogDebug` noting a skipped line is permitted (aids post-crash diagnosis, no user-facing noise) but not mandated — the worker may add it if it fits the surrounding logging style.

## Risks / Trade-offs

- **Silent data loss masks genuine corruption** → A malformed interior line (not just a trailing partial) is skipped without surfacing. Mitigation: this already matches `ReadRecordsAsync` behaviour over the identical file, so the two readers now agree; the append-only + non-fsync crash model makes a *trailing* partial the only expected case, and an interior malformation would already be silently skipped by `ReadRecordsAsync` on the same file. An optional Debug log (D3) leaves a breadcrumb.
- **Compaction index drift if the skip were applied to `lines`** → Avoided by D1 (skip at materialization, keep `lines` intact).

## Migration Plan

No migration. Pure read-path robustness change; no format, schema, or config change. Rollback is reverting the commit.
