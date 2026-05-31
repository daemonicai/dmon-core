using System.Collections.Concurrent;

namespace Dmon.Gateway.Sessions;

/// <summary>
/// In-memory registry of active <see cref="SessionHandler"/> instances keyed by session id.
/// Registered as a singleton in DI. Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, SessionHandler> _handlers = new();

    /// <summary>
    /// Registers a handler. Replaces any existing handler registered under the same session id.
    /// </summary>
    public void Register(string sessionId, SessionHandler handler) =>
        _handlers[sessionId] = handler;

    /// <summary>
    /// Returns the handler for <paramref name="sessionId"/>, or <c>null</c> if not found.
    /// </summary>
    public SessionHandler? TryGet(string sessionId) =>
        _handlers.TryGetValue(sessionId, out SessionHandler? handler) ? handler : null;

    /// <summary>
    /// Removes the handler for <paramref name="sessionId"/> and returns it, or <c>null</c>
    /// if no handler was registered under that id. Used by Group 7 reaping.
    /// </summary>
    public SessionHandler? Remove(string sessionId) =>
        _handlers.TryRemove(sessionId, out SessionHandler? handler) ? handler : null;
}
