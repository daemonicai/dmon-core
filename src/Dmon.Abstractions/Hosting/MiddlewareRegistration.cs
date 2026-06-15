using Dmon.Abstractions.Extensions;

namespace Dmon.Abstractions.Hosting;

/// <summary>
/// Carries a <see cref="IDmonMiddleware"/> instance paired with its optional
/// per-registration priority override. Registered in DI so the build step can
/// enumerate all middleware entries and route them into <c>IMiddlewareRegistry</c>
/// with the correct priority.
/// </summary>
public sealed class MiddlewareRegistration
{
    /// <summary>
    /// Gets the middleware instance.
    /// </summary>
    public IDmonMiddleware Middleware { get; }

    /// <summary>
    /// Gets the per-registration priority override. When <see langword="null"/>,
    /// the middleware's <see cref="DmonMiddlewareAttribute"/> priority or 0 is used.
    /// </summary>
    public int? PriorityOverride { get; }

    /// <summary>
    /// Initialises a new <see cref="MiddlewareRegistration"/>.
    /// </summary>
    public MiddlewareRegistration(IDmonMiddleware middleware, int? priorityOverride = null)
    {
        Middleware = middleware;
        PriorityOverride = priorityOverride;
    }
}
