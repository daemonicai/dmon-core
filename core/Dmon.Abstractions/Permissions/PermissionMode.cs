namespace Dmon.Abstractions.Permissions;

/// <summary>
/// Controls the permission posture applied during a session.
/// </summary>
public enum PermissionMode
{
    /// <summary>
    /// Standard coding posture: CWD-subtree reads are implicit; writes and shell
    /// execution require per-operation prompts (ADR-006 default behaviour).
    /// </summary>
    Coding,

    /// <summary>
    /// Identical to <see cref="Coding"/> except that write/edit/delete operations
    /// whose normalised target is within the session's own <c>assets/&lt;session_id&gt;/</c>
    /// directory are implicitly allowed (risk: none) — mirroring the implicit-read-within-CWD
    /// rule. The denylist and all other gate behaviour are unchanged.
    /// </summary>
    Sandbox,
}
