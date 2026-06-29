namespace Dmon.Abstractions.Hosting;

/// <summary>
/// In-process notification seam for session lifecycle activity.
/// </summary>
/// <remarks>
/// Implementations are discovered from DI (zero or more) and invoked in-process only.
/// Invocation is best-effort: exceptions from a listener are isolated per listener and
/// never block or fail the originating session command or turn. The seam carries no policy.
/// </remarks>
public interface ISessionActivityListener
{
    /// <summary>Called when a session becomes active (created or loaded).</summary>
    /// <param name="sessionId">The id of the session that became active.</param>
    void OnSessionActivated(string sessionId);

    /// <summary>Called at the start of each turn, after the turn gate is acquired.</summary>
    /// <param name="sessionId">The id of the active session.</param>
    void OnTurnStarted(string sessionId);
}
