namespace Dmon.Desktop;

/// <summary>
/// Lifecycle state of the local dmoncore process as observed by the desktop host.
/// Transitions: Booting → Ready | Faulted.
/// </summary>
public enum CoreState
{
    /// <summary>
    /// <see cref="ICoreLauncher.StartProtocolCompatibleCoreAsync"/> is in flight.
    /// Interaction with the core is not yet possible.
    /// </summary>
    Booting,

    /// <summary>
    /// The core started, passed the protocol-compat gate, and the RPC client pump is running.
    /// Commands may now be issued via <see cref="CoreSessionService.Client"/>.
    /// </summary>
    Ready,

    /// <summary>
    /// Launch failed — <see cref="CoreAcquisitionException"/> or
    /// <see cref="ProtocolMismatchException"/> was thrown. The error message is in
    /// <see cref="CoreSessionService.FaultMessage"/>.
    /// </summary>
    Faulted
}
