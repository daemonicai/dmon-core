using Dmon.Extensions;

namespace Dmon.Core.Extensions;

/// <summary>
/// Collects <see cref="IDmonMiddleware"/> instances discovered during extension loading.
/// Group 3 (pipeline fold) reads <see cref="GetAll"/> to assemble the <c>IChatClient</c>
/// pipeline; this registry is intentionally minimal — registration and enumeration only.
/// </summary>
public interface IMiddlewareRegistry
{
    /// <summary>
    /// Adds <paramref name="instances"/> to the registry.
    /// Called by <see cref="ExtensionService"/> after each successful assembly load.
    /// </summary>
    void Register(IReadOnlyList<IDmonMiddleware> instances);

    /// <summary>
    /// Returns all middleware registered so far, in registration order.
    /// </summary>
    IReadOnlyList<IDmonMiddleware> GetAll();
}
