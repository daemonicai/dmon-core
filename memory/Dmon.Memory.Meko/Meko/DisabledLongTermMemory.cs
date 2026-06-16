using Dmon.Abstractions.Memory;
using Dmon.Protocol.Conversation;

namespace Dmon.Memory.Meko;

/// <summary>
/// Null-object implementation of <see cref="ILongTermMemory"/>. All writes and flush
/// are no-ops; reads return empty/null. Makes zero MCP or network calls (D8, 3.5).
/// Registered when long-term memory is disabled in configuration.
/// </summary>
internal sealed class DisabledLongTermMemory : ILongTermMemory
{
    public static readonly DisabledLongTermMemory Instance = new();

    private DisabledLongTermMemory() { }

    public Task RecordAsync(
        IReadOnlyList<MessageRecord> records,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<MemoryHit>> SearchAsync(
        string query,
        MemoryScope scope = MemoryScope.Agent,
        int limit = 10,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MemoryHit>>([]);

    public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public Task AddFactAsync(
        string fact,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<MemoryHit?> GetAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<MemoryHit?>(null);

    public Task<IReadOnlyList<MemoryHit>> ListAsync(
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MemoryHit>>([]);

    public Task UpdateAsync(
        string id,
        string text,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DeleteAsync(
        string id,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
