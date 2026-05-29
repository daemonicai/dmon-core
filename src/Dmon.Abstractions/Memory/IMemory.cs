namespace Dmon.Abstractions.Memory;

/// <summary>
/// Unified memory facade that fans out writes to both tiers and fuses reads into a
/// single ranked result list. Callers that need tier-specific behaviour access the
/// <see cref="ShortTerm"/> or <see cref="LongTerm"/> properties directly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Write fan-out:</strong> <see cref="IMemoryStore.RecordAsync"/> is dispatched
/// unconditionally to both tiers. Long-term capture being opt-in refers to explicit
/// fact assertion (<see cref="ILongTermMemory.AddFactAsync"/>); raw turn ingestion
/// always reaches short-term, and the long-term implementation governs whether and how
/// turns are distilled.
/// </para>
/// <para>
/// <strong>Read fusion:</strong> <see cref="IMemoryStore.SearchAsync"/> queries both
/// tiers and merges results via rank-based fusion (RRF). Scores within a tier are
/// comparable; cross-tier scores in the fused list reflect rank position, not raw
/// similarity values. Each <see cref="MemoryHit"/> carries <see cref="MemoryHit.Source"/>
/// so callers can attribute results back to the originating tier.
/// </para>
/// <para>
/// <strong>Disabled long-term:</strong> when no <see cref="ILongTermMemory"/> is
/// configured, <see cref="LongTerm"/> is <see langword="null"/> and the facade degrades
/// gracefully to short-term only.
/// </para>
/// </remarks>
public interface IMemory : IMemoryStore
{
    /// <summary>
    /// The short-term (per-session) memory tier. Never null.
    /// </summary>
    IShortTermMemory ShortTerm { get; }

    /// <summary>
    /// The long-term (Meko) memory tier. Null when long-term memory is not configured.
    /// </summary>
    ILongTermMemory? LongTerm { get; }
}
