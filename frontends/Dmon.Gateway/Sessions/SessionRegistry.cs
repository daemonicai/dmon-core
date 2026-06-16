using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Dmon.Gateway.Sessions;

/// <summary>
/// In-memory registry of active <see cref="SessionHandler"/> instances keyed by session id.
/// Registered as a singleton in DI. Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, SessionHandler> _handlers = new();

    /// <summary>
    /// Current number of registered handlers. Used by <see cref="TryRegister"/> and callers
    /// that need to check headroom before deciding to allocate a new handler.
    /// </summary>
    public int Count => _handlers.Count;

    /// <summary>
    /// Registers a handler. Replaces any existing handler registered under the same session id.
    /// Use this only for test setup or when session-creation logic has already verified the cap.
    /// Production session creation must use <see cref="TryRegister"/> to enforce
    /// <see cref="GatewayOptions.MaxConcurrentHandlers"/> (Group 10 wires this).
    /// </summary>
    public void Register(string sessionId, SessionHandler handler) =>
        _handlers[sessionId] = handler;

    /// <summary>
    /// Attempts to register <paramref name="handler"/> for <paramref name="sessionId"/>,
    /// enforcing the concurrent-handler cap. Returns <c>true</c> and registers the handler
    /// when the current count is below <paramref name="maxConcurrentHandlers"/>; returns
    /// <c>false</c> (without modifying the registry) when the cap is already reached.
    ///
    /// This is the correct registration primitive for new session creation (Group 10).
    /// Reattaching to an existing session does not call this method — it looks up and attaches
    /// to an already-registered handler, so reattach never fails due to the cap.
    ///
    /// Note: there is a benign TOCTOU between the Count check and the insertion under high
    /// concurrency (two threads could both see Count &lt; cap and both succeed, briefly
    /// exceeding the cap by the number of concurrent creators). For a personal home-server
    /// deployment this is acceptable; a stricter implementation would use a lock or
    /// Interlocked-based counter.
    /// </summary>
    public bool TryRegister(string sessionId, SessionHandler handler, int maxConcurrentHandlers)
    {
        // Re-registration of an existing session id does not count against the cap.
        if (_handlers.ContainsKey(sessionId))
        {
            _handlers[sessionId] = handler;
            return true;
        }

        if (_handlers.Count >= maxConcurrentHandlers)
            return false;

        _handlers[sessionId] = handler;
        return true;
    }

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

    /// <summary>
    /// Returns a snapshot of all currently-registered (sessionId, handler) pairs.
    /// Used by the reaper to scan for expired handlers without blocking registration.
    /// </summary>
    public IReadOnlyList<(string SessionId, SessionHandler Handler)> Snapshot()
    {
        List<(string, SessionHandler)> result = [];
        foreach (KeyValuePair<string, SessionHandler> kv in _handlers)
            result.Add((kv.Key, kv.Value));
        return result;
    }
}
