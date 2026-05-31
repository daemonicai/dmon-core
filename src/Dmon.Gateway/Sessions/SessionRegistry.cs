namespace Dmon.Gateway.Sessions;

/// <summary>
/// Tracks active gateway session handlers keyed by session id.
/// Skeleton registered in DI by group 1; full lifecycle management is group 2.
/// </summary>
public sealed class SessionRegistry
{
    // Group 2 adds handler registration, lookup, eviction, and TTL tracking.
}
