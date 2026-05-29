using System.Text.Json.Serialization;
using Dmon.Abstractions.Memory;

namespace Dmon.Memory.Index;

/// <summary>
/// Minimal JSONL line shape written by <see cref="ShortTermMemory"/> for each
/// <c>ChatMessage</c> turn appended to <c>messages.jsonl</c>.
///
/// This is the short-term tier's own line shape. It is intentionally minimal and
/// ADR-004-aligned (append-only, human-readable, forward-compatible) while the
/// canonical core turn-persistence change (tracked separately) is still pending.
/// When that change lands it will supersede this record with a richer DTO; existing
/// lines remain readable because they carry the same core fields.
///
/// <c>scope</c> is persisted so that <see cref="ShortTermMemory.RebuildFromJsonlAsync"/>
/// can reproduce the original per-scope searchability without reclassifying every
/// entry as <see cref="MemoryScope.Agent"/>.
/// </summary>
internal sealed record TurnLineRecord(
    [property: JsonPropertyName("entryId")]   string          EntryId,
    [property: JsonPropertyName("timestamp")] DateTimeOffset  Timestamp,
    [property: JsonPropertyName("role")]      string          Role,
    [property: JsonPropertyName("text")]      string          Text,
    [property: JsonPropertyName("scope")]     MemoryScope     Scope
);
