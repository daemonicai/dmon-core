namespace Dmon.Runtime;

/// <summary>
/// Abstracts the core-launch surface consumed by the gateway.
/// Resolves, starts, and protocol-gates a dmoncore process.
/// </summary>
public interface ICoreLauncher
{
    /// <summary>
    /// Resolves the core, starts it, reads its <c>agentReady</c> event,
    /// and verifies the protocol version.
    /// </summary>
    /// <param name="corePathOverride">
    /// Value of the <c>--core-path</c> CLI argument, or <see langword="null"/>.
    /// </param>
    /// <param name="workingDirectory">Optional working directory for the core process.</param>
    /// <param name="onStderrLine">Optional callback for stderr lines (e.g. for tests).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="CoreSession"/> whose <c>Process.StandardOutput</c> is positioned immediately
    /// after the consumed <c>agentReady</c> line, ready for the host's RPC event loop.
    /// </returns>
    Task<CoreSession> StartProtocolCompatibleCoreAsync(
        string? corePathOverride = null,
        string? workingDirectory = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default);
}
