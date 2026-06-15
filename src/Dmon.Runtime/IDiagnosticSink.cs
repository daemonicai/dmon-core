namespace Dmon.Runtime;

/// <summary>
/// Receives diagnostic lines emitted by the core process on its stderr stream.
/// Implementations must be non-blocking and thread-safe — the callback fires on
/// the OS stderr-drain thread, which is independent of the RPC event loop.
/// </summary>
public interface IDiagnosticSink
{
    void WriteLine(string line);
}
