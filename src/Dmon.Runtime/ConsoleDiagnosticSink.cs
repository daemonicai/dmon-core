namespace Dmon.Runtime;

/// <summary>
/// Routes core diagnostic stderr lines to the host process's stderr (<see cref="Console.Error"/>).
/// Writing to <see cref="Console.Error"/> is thread-safe, so this sink is safe to call from
/// the OS stderr-drain thread without additional locking.
/// </summary>
public sealed class ConsoleDiagnosticSink : IDiagnosticSink
{
    public void WriteLine(string line)
    {
        Console.Error.WriteLine($"[core] {line}");
    }
}
