using Dmon.Abstractions.Memory;
using Microsoft.Extensions.AI;

namespace Dmon.Memory.Tests.Facade.Fakes;

/// <summary>
/// Hand-written fake for <see cref="IShortTermMemory"/>.
/// Records call counts and captured arguments; returns a configurable hit list.
/// </summary>
internal sealed class FakeShortTermMemory : IShortTermMemory
{
    public int RecordCallCount { get; private set; }
    public IReadOnlyList<ChatMessage>? LastRecordedTurns { get; private set; }
    public MemoryScope LastRecordedScope { get; private set; }

    public int FlushCallCount { get; private set; }

    public IReadOnlyList<MemoryHit> SearchResults { get; set; } = [];

    public Func<string, MemoryScope, int, CancellationToken, Task<IReadOnlyList<MemoryHit>>>? SearchOverride { get; set; }

    public Task RecordAsync(
        IReadOnlyList<ChatMessage> turns,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        RecordCallCount++;
        LastRecordedTurns = turns;
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

    public Task<IReadOnlyList<object>> ReadMessagesAsync(
        bool applyCompaction = true,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ReadMessagesAsync is not exercised by the facade.");
}
