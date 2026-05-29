using System.Text.Json;

namespace Dmon.Abstractions.Memory;

/// <summary>
/// A single result returned by <c>SearchAsync</c> from any memory tier or the fused facade.
/// <c>Source</c> identifies which tier produced this hit, enabling callers to attribute
/// results back to short-term or long-term memory.
/// </summary>
/// <param name="Id">Stable identifier for this memory entry (opaque string; tier-assigned).</param>
/// <param name="Text">The recalled text content.</param>
/// <param name="Source">Which memory tier produced this hit.</param>
/// <param name="Score">
/// Relevance score in the range [0, 1]; higher is more relevant. Scores from different
/// tiers are NOT directly comparable — the facade performs rank-based fusion, not
/// score-based merging.
/// </param>
/// <param name="Metadata">
/// Optional tier-supplied metadata (e.g. timestamps, tags), holding parsed JSON values
/// from the backing store (long-term/Meko). Each value is a <see cref="JsonElement"/>,
/// faithfully representing loosely-structured or nested JSON without lossy stringification.
/// Null when not provided.
/// </param>
/// <param name="Relations">
/// Graph edges from the Meko AGE knowledge graph. Present only when
/// <see cref="Source"/> is <see cref="MemorySource.LongTerm"/>; null for short-term hits.
/// </param>
public sealed record MemoryHit(
    string Id,
    string Text,
    MemorySource Source,
    double Score,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    IReadOnlyList<MemoryRelation>? Relations = null);
