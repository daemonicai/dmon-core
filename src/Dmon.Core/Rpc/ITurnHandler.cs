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
}
