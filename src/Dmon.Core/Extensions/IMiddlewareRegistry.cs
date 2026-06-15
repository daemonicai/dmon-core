using Dmon.Extensions;

namespace Dmon.Core.Extensions;

/// <summary>
/// Collects <see cref="IDmonMiddleware"/> instances registered through the
/// <c>DmonHostBuilder</c>. The pipeline fold reads <see cref="GetAll"/> to
/// assemble the <c>IChatClient</c> pipeline; this registry is intentionally
/// minimal — registration and enumeration only.
/// </summary>
public interface IMiddlewareRegistry
{
    /// <summary>
    /// Adds <paramref name="instances"/> to the registry with an optional per-registration
    /// priority override. A non-null <paramref name="priorityOverride"/> beats both the
    /// <see cref="DmonMiddlewareAttribute"/> value and any config priority.
    /// </summary>
    void Register(IReadOnlyList<IDmonMiddleware> instances, int? priorityOverride = null);

    /// <summary>
    /// Returns all middleware registered so far, in registration order, paired with
    /// their effective priority override (null means use attribute/config).
    /// </summary>
    IReadOnlyList<(IDmonMiddleware Middleware, int? PriorityOverride)> GetAll();
}
