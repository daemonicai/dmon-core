namespace Dmon.Gateway.Sessions;

/// <summary>
/// Result of <see cref="SessionHandler.TryAdmitCommand"/>: whether a command id was seen for
/// the first time or is a duplicate of an already-admitted command (ADR-012 Decision 5).
/// </summary>
public enum CommandAdmission
{
    Accepted,
    Duplicate,
}
