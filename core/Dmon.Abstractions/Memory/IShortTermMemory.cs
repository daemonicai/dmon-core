namespace Dmon.Abstractions.Memory;

/// <summary>
/// Per-session memory tier backed by the canonical <c>messages.jsonl</c> file and a
/// derived hybrid search index (<c>index.db</c>). Extends <see cref="IMemoryStore"/>
/// with a verbatim/chronological read path.
/// </summary>
/// <remarks>
/// Short-term memory is bound to one session via the ambient <see cref="MemoryContext"/>;
/// no <c>sessionId</c> parameter is needed on these methods. The canonical JSONL is the
/// source of truth — the index is a rebuildable projection and may lag until
/// <see cref="IMemoryStore.FlushAsync"/> is called.
/// </remarks>
public interface IShortTermMemory : IMemoryStore
{
    /// <summary>
    /// Returns the raw, chronologically ordered messages for the current session.
    /// When <paramref name="applyCompaction"/> is <see langword="true"/>, compacted
    /// summaries replace the original turns they summarise (implementation detail;
    /// verbatim content is always preserved in the JSONL).
    /// </summary>
    /// <param name="applyCompaction">
    /// <see langword="true"/> to substitute compacted summaries inline;
    /// <see langword="false"/> to return the complete unmodified turn sequence.
    /// </param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The ordered message objects for the current session.</returns>
    Task<IReadOnlyList<object>> ReadMessagesAsync(
        bool applyCompaction = true,
        CancellationToken cancellationToken = default);
}
