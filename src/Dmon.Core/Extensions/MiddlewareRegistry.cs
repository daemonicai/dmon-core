using Dmon.Extensions;

namespace Dmon.Core.Extensions;

/// <summary>
/// Thread-safe, append-only store of <see cref="IDmonMiddleware"/> instances with
/// their optional per-registration priority overrides.
/// </summary>
public sealed class MiddlewareRegistry : IMiddlewareRegistry
{
    private readonly List<(IDmonMiddleware Middleware, int? PriorityOverride)> _items = [];
    private readonly Lock _lock = new();

    /// <inheritdoc/>
    public void Register(IReadOnlyList<IDmonMiddleware> instances, int? priorityOverride = null)
    {
        if (instances.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (IDmonMiddleware instance in instances)
            {
                _items.Add((instance, priorityOverride));
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<(IDmonMiddleware Middleware, int? PriorityOverride)> GetAll()
    {
        lock (_lock)
        {
            return [.. _items];
        }
    }
}
