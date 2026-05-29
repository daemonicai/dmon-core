namespace Dmon.Abstractions.Memory;

/// <summary>
/// A directed edge in the Meko AGE knowledge graph, present only on
/// <see cref="MemoryHit"/> values sourced from long-term memory.
/// </summary>
/// <param name="Source">The subject node identifier.</param>
/// <param name="Relation">The edge label (e.g. <c>"knows"</c>, <c>"part_of"</c>).</param>
/// <param name="Target">The object node identifier.</param>
public sealed record MemoryRelation(string Source, string Relation, string Target);
