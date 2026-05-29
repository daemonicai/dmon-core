using Microsoft.Extensions.AI;

namespace Dmon.Abstractions.Memory;

/// <summary>
/// Base contract shared by every memory store and the <see cref="IMemory"/> facade.
/// Every store is recordable, searchable, and flushable; tier-specific operations
/// (<see cref="IShortTermMemory"/> verbatim reads, <see cref="ILongTermMemory"/> CRUD)
/// are defined on the extending interfaces.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Consistency contract:</strong>
/// </para>
/// <para>
/// Short-term memory (<see cref="IShortTermMemory"/>) provides <em>read-your-writes</em>
/// semantics within a session, subject to indexing latency. After a <see cref="RecordAsync"/>
/// call returns, the canonical JSONL is durable; the derived hybrid index becomes
/// searchable after the next <see cref="FlushAsync"/> call (or sooner, at implementation
/// discretion).
/// </para>
/// <para>
/// Long-term memory (<see cref="ILongTermMemory"/>) is <em>eventually consistent</em>
/// and does NOT guarantee read-your-writes. A write to long-term memory may not be
/// immediately visible via <see cref="SearchAsync"/> on the same store.
/// </para>
/// <para>
/// <see cref="FlushAsync"/> is the <em>materialization barrier</em>: authoritative for
/// short-term (makes all pending indexed writes searchable), best-effort for long-term
/// (requests flush of pending distillation to Meko, but does not guarantee immediate
/// read-your-writes). Callers that require the latest short-term content to be
/// searchable before a handoff MUST call <see cref="FlushAsync"/> first.
/// </para>
/// </remarks>
public interface IMemoryStore
{
    /// <summary>
    /// Records one or more conversation turns into this store.
    /// Best-effort — a successful return guarantees canonical persistence for short-term
    /// but does NOT guarantee immediate searchability. Call <see cref="FlushAsync"/> to
    /// ensure the index is up to date before querying.
    /// </summary>
    /// <param name="turns">The turns to ingest.</param>
    /// <param name="scope">The scope that applies to every turn in this batch.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task RecordAsync(
        IReadOnlyList<ChatMessage> turns,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches this store for entries relevant to <paramref name="query"/>.
    /// Results are ordered by descending relevance; scores within a tier are comparable
    /// but scores across tiers are NOT directly comparable (see <see cref="IMemory"/>
    /// for fused cross-tier search via rank-based fusion).
    /// </summary>
    /// <param name="query">Natural-language or keyword query string.</param>
    /// <param name="scope">Restricts the search to entries recorded under this scope.</param>
    /// <param name="limit">Maximum number of hits to return.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>A ranked list of matching hits, or an empty list when nothing matches.</returns>
    Task<IReadOnlyList<MemoryHit>> SearchAsync(
        string query,
        MemoryScope scope = MemoryScope.Agent,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Materializes all pending writes so that subsequent <see cref="SearchAsync"/> calls
    /// reflect them. Authoritative for short-term (index rebuild/sync); best-effort for
    /// long-term (triggers distillation flush to Meko).
    /// </summary>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}
