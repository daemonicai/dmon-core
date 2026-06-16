namespace Dmon.Abstractions.Memory;

/// <summary>
/// Durable, eventually-consistent memory tier backed by Meko over MCP.
/// Extends <see cref="IMemoryStore"/> with explicit fact CRUD. Capture is opt-in —
/// callers must call <see cref="AddFactAsync"/> or <see cref="IMemoryStore.RecordAsync"/>
/// explicitly; the store does NOT auto-capture every turn.
/// </summary>
/// <remarks>
/// Long-term memory is eventually consistent. A successful return from any write method
/// does NOT guarantee that the written content is immediately visible via
/// <see cref="IMemoryStore.SearchAsync"/> or <see cref="GetAsync"/>.
/// <see cref="IMemoryStore.FlushAsync"/> is best-effort for this tier.
/// </remarks>
public interface ILongTermMemory : IMemoryStore
{
    /// <summary>
    /// Asserts a free-text fact into long-term memory. The store is responsible for
    /// distillation and graph extraction; callers supply the raw statement.
    /// </summary>
    /// <param name="fact">The fact to assert (natural language).</param>
    /// <param name="scope">The scope under which to store this fact.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task AddFactAsync(
        string fact,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single memory entry by its stable identifier, or
    /// <see langword="null"/> if not found.
    /// </summary>
    /// <param name="id">The memory entry identifier (as returned by <see cref="MemoryHit.Id"/>).</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task<MemoryHit?> GetAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all memory entries stored under the given <paramref name="scope"/>.
    /// </summary>
    /// <param name="scope">The scope to enumerate.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task<IReadOnlyList<MemoryHit>> ListAsync(
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the text of an existing memory entry.
    /// </summary>
    /// <param name="id">The memory entry identifier.</param>
    /// <param name="text">The replacement text.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task UpdateAsync(
        string id,
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes a memory entry from long-term storage.
    /// </summary>
    /// <param name="id">The memory entry identifier.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task DeleteAsync(
        string id,
        CancellationToken cancellationToken = default);
}
