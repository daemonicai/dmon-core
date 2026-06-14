using Daemonic.Dmail.Models;
using Microsoft.Extensions.VectorData;

namespace Daemonic.Dmail.Services;

public sealed class VectorStoreService
{
    private readonly VectorStoreCollection<string, EmailVector> _collection;

    public VectorStoreService(VectorStore vectorStore)
    {
        _collection = vectorStore.GetCollection<string, EmailVector>("emails");
    }

    public async Task UpsertVectorAsync(string account, uint uid, float[] vector, CancellationToken ct = default)
    {
        var record = new EmailVector
        {
            Id = EmailVector.BuildId(account, uid),
            Vector = new ReadOnlyMemory<float>(vector)
        };
        await _collection.UpsertAsync(record, cancellationToken: ct);
    }

    public async Task UpsertBatchAsync(IReadOnlyList<(string Account, uint Uid, float[] Vector)> entries, CancellationToken ct = default)
    {
        var records = entries.Select(e => new EmailVector
        {
            Id = EmailVector.BuildId(e.Account, e.Uid),
            Vector = new ReadOnlyMemory<float>(e.Vector)
        });
        await _collection.UpsertAsync(records, cancellationToken: ct);
    }

    public Task EnsureCollectionAsync(CancellationToken ct = default) =>
        _collection.EnsureCollectionExistsAsync(ct);

    public VectorStoreCollection<string, EmailVector> Collection => _collection;
}
