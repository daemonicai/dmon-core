using Dmon.Abstractions.Memory;
using Dmon.Protocol.Conversation;

namespace Dmon.Memory.Tests.Facade.Fakes;

/// <summary>
/// Hand-written fake for <see cref="ILongTermMemory"/>.
/// Records call counts and captured arguments; returns a configurable hit list.
/// ILongTermMemory-specific CRUD methods throw <see cref="NotSupportedException"/>
/// (unused by the facade).
/// </summary>
internal sealed class FakeLongTermMemory : ILongTermMemory
{
    public int RecordCallCount { get; private set; }
    public IReadOnlyList<MessageRecord>? LastRecordedRecords { get; private set; }
    public MemoryScope LastRecordedScope { get; private set; }

    public int FlushCallCount { get; private set; }

    public IReadOnlyList<MemoryHit> SearchResults { get; set; } = [];

    public Func<string, MemoryScope, int, CancellationToken, Task<IReadOnlyList<MemoryHit>>>? SearchOverride { get; set; }

    public Task RecordAsync(
        IReadOnlyList<MessageRecord> records,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        RecordCallCount++;
        LastRecordedRecords = records;
        LastRecordedScope = scope;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryHit>> SearchAsync(
        string query,
        MemoryScope scope = MemoryScope.Agent,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (SearchOverride is not null)
            return SearchOverride(query, scope, limit, cancellationToken);

        return Task.FromResult(SearchResults);
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        FlushCallCount++;
        return ValueTask.CompletedTask;
    }

    // ── ILongTermMemory CRUD (not used by the facade) ─────────────────────────

    public Task AddFactAsync(
        string fact,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<MemoryHit?> GetAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MemoryHit>> ListAsync(
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task UpdateAsync(
        string id,
        string text,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
