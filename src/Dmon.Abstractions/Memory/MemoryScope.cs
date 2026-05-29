namespace Dmon.Abstractions.Memory;

/// <summary>
/// Controls which identity boundary a memory record or search is scoped to.
/// Carried per-call on <c>RecordAsync</c> and <c>SearchAsync</c>; the ambient
/// <see cref="MemoryContext"/> binds the identity once per session.
/// </summary>
/// <remarks>
/// <para><c>Session</c> is the natural scope for short-term memory — ephemeral, bounded
/// to the current conversation.</para>
/// <para><c>Agent</c> (default) and <c>User</c> are the durable long-term scopes.</para>
/// <para><c>Shared</c> is reserved for future collective/cross-agent promotion.</para>
/// <para>The exact Meko <c>scope</c> string values are mapped at the implementation layer;
/// this enum is the single adjustment point when those values are confirmed.</para>
/// </remarks>
public enum MemoryScope
{
    /// <summary>Scoped to the agent identity (default).</summary>
    Agent,

    /// <summary>Scoped to the current conversation session.</summary>
    Session,

    /// <summary>Scoped to the authenticated user.</summary>
    User,

    /// <summary>Shared across agents; reserved for future promotion workflows.</summary>
    Shared,
}
