using Dmon.Protocol.Commands;

namespace Dmon.Core.Rpc;

public interface ITurnHandler
{
    Task SubmitAsync(TurnSubmitCommand cmd, CancellationToken cancellationToken);
    Task SteerAsync(TurnSteerCommand cmd, CancellationToken cancellationToken);
    Task FollowUpAsync(TurnFollowUpCommand cmd, CancellationToken cancellationToken);
    Task AbortAsync(TurnAbortCommand cmd, CancellationToken cancellationToken);
    Task ConfirmResponseAsync(ToolConfirmResponseCommand cmd, CancellationToken cancellationToken);
    Task UiInputResponseAsync(UiInputResponseCommand cmd, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the canonical session log, applies the compaction rule, and seeds the in-memory
    /// conversation history so the next turn resumes from the persisted state.
    /// Must be called after <see cref="ISessionHandler.LoadAsync"/> completes.
    /// No-op when no session store is configured.
    /// </summary>
    Task SeedHistoryFromSessionAsync(string sessionId, CancellationToken cancellationToken);
}
