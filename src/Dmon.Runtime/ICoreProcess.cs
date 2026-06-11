namespace Dmon.Runtime;

/// <summary>
/// Abstracts the dmoncore process lifecycle surface consumed by the gateway and host surfaces.
/// Covers stdio pipe access, liveness, and start/stop/restart control.
/// </summary>
public interface ICoreProcess : IDisposable
{
    /// <summary>
    /// The core process's standard output stream (JSONL events).
    /// </summary>
    TextReader StandardOutput { get; }

    /// <summary>
    /// The core process's standard input stream (JSONL commands).
    /// </summary>
    TextWriter StandardInput { get; }

    /// <summary>
    /// Whether the core process is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts the core process.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Stops the core process gracefully, then forcefully after a timeout.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Stops the current core process and spawns a fresh one.
    /// </summary>
    Task RestartAsync();
}
