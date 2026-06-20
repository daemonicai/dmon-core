using System.Collections.Concurrent;

namespace Dmail.Services;

/// <summary>
/// In-memory store for OAuth2 PKCE state and code verifier.
/// Single-user app, so a simple ConcurrentDictionary suffices.
/// </summary>
public sealed class OAuth2StateStore
{
    private readonly ConcurrentDictionary<string, (string CodeVerifier, DateTime Created)> _states = new();

    public void Store(string state, string codeVerifier)
    {
        _states[state] = (codeVerifier, DateTime.UtcNow);
        // Cleanup old entries (older than 10 minutes)
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var kv in _states.Where(kv => kv.Value.Created < cutoff))
        {
            _states.TryRemove(kv.Key, out _);
        }
    }

    public string? GetVerifier(string state)
    {
        if (_states.TryRemove(state, out var entry))
            return entry.CodeVerifier;
        return null;
    }
}
