using Dmon.Extensions;

namespace Dmon.Core.Extensions;

/// <summary>
/// Thread-safe, append-only store of <see cref="IDmonMiddleware"/> instances.
/// </summary>
public sealed class MiddlewareRegistry : IMiddlewareRegistry
{
    private readonly List<IDmonMiddleware> _items = [];
    private readonly Lock _lock = new();

    /// <inheritdoc/>
    public void Register(IReadOnlyList<IDmonMiddleware> instances)
    {
        if (instances.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            _items.AddRange(instances);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<IDmonMiddleware> GetAll()
    {
        lock (_lock)
        {
            return [.. _items];
        }
    }
}
