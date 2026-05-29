using Dmon.Abstractions.Memory;

namespace Dmon.Memory.Meko;

/// <summary>
/// Single policy point for Meko's <c>scope</c> constant and the
/// <see cref="MemoryScope"/>→<c>run_id</c> mapping (D9, live-verified 2026-05-29).
///
/// <para>
/// <c>scope</c> is a fixed required string on every Meko tool call — the server
/// schema says literally "Pass <c>admin</c>." It is NOT a partition selector.
/// </para>
/// <para>
/// Partitioning is handled via <c>run_id</c>: <see cref="MemoryScope.Session"/>
/// restricts to the current dmon session; all durable scopes omit <c>run_id</c>
/// for cross-conversation recall. Adjust <see cref="ToRunId"/> here only.
/// </para>
/// </summary>
internal static class MekoScopeMapping
{
    /// <summary>
    /// The fixed Meko <c>scope</c> value required on every <c>memory_*</c> and
    /// <c>conversation_create</c> call. Update here if Meko widens the accepted values.
    /// </summary>
    public const string AdminScope = "admin";

    /// <summary>
    /// Maps <paramref name="scope"/> to the <c>run_id</c> to send (or
    /// <see langword="null"/> to omit it). Single adjustable policy point (D9):
    /// <list type="bullet">
    ///   <item><see cref="MemoryScope.Session"/> → normalized <paramref name="sessionId"/> (restricts to this conversation).</item>
    ///   <item>All durable scopes (<c>Agent</c>/<c>User</c>/<c>Shared</c>) → <see langword="null"/> (omit <c>run_id</c>).</item>
    /// </list>
    /// <para>
    /// Meko applies <c>int(run_id, 16)</c> server-side, so the value must be a
    /// pure hex string. When <paramref name="sessionId"/> is a standard hyphenated
    /// GUID it is returned in <c>"N"</c> format (32 hex chars, no hyphens). Any
    /// other string has hyphens stripped as a best-effort fallback.
    /// </para>
    /// </summary>
    public static string? ToRunId(MemoryScope scope, string sessionId)
    {
        if (scope != MemoryScope.Session)
        {
            return null;
        }

        // Meko applies int(run_id, 16) — the value must be a pure hex string.
        // Standard dmon session ids are hyphenated GUIDs; normalise to "N" format.
        if (Guid.TryParse(sessionId, out Guid guid))
        {
            return guid.ToString("N");
        }

        // Best-effort for non-GUID session ids: strip hyphens only.
        return sessionId.Replace("-", string.Empty, StringComparison.Ordinal);
    }
}
