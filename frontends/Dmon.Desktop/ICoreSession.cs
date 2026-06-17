using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Desktop;

/// <summary>
/// Testability seam for VMs that need to observe events and send commands.
/// Hides the swappable <see cref="IRpcClient"/> behind a stable interface so
/// unit tests can supply a fake without a live core process.
/// </summary>
public interface ICoreSession
{
    /// <summary>
    /// Hot event stream marshalled onto the injected scheduler.
    /// </summary>
    IObservable<Event> Events { get; }

    /// <summary>
    /// Sends a command via the current RPC client.
    /// No-op (returns immediately) when the core is not in the Ready state —
    /// callers gate send eligibility via CanExecute before invoking.
    /// </summary>
    Task SendAsync(Command command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reloads the core: disposes the current client, restarts the process, rebinds.
    /// </summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
