namespace Dmon.Abstractions.Memory;

/// <summary>
/// Identifies which memory tier produced a <see cref="MemoryHit"/>.
/// </summary>
public enum MemorySource
{
    /// <summary>
    /// The hit originated from short-term memory (the per-session JSONL + hybrid index).
    /// </summary>
    ShortTerm,

    /// <summary>
    /// The hit originated from long-term memory (Meko over MCP).
    /// </summary>
    LongTerm,
}
