using System.Diagnostics;
using System.Reflection;

namespace Dmon.Console;

/// <summary>
/// Manages the Dmon.Core process lifecycle.
/// Spawns the core via <c>dotnet exec</c>, connects stdio pipes,
/// and provides graceful shutdown.
/// </summary>
public sealed class CoreProcessManager : IDisposable
{
    private Process? _process;
    private readonly string _corePath;
    private bool _disposed;

    public CoreProcessManager(string? corePathOverride)
    {
        _corePath = ResolveCorePath(corePathOverride);
    }

    /// <summary>
    /// The core process's standard output stream (JSONL events).
    /// </summary>
    public StreamReader StandardOutput => _process?.StandardOutput
        ?? throw new InvalidOperationException("Process not started.");

    /// <summary>
    /// The core process's standard input stream (JSONL commands).
    /// </summary>
    public StreamWriter StandardInput => _process?.StandardInput
        ?? throw new InvalidOperationException("Process not started.");

    /// <summary>
    /// Whether the core process is currently running.
    /// </summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Starts the core process. Does not wait for <c>agentReady</c>.
    /// </summary>
    public Task StartAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{_corePath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = new Process { StartInfo = psi };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                System.Console.Error.WriteLine($"[core-stderr] {e.Data}");
        };

        _process.Start();
        _process.BeginErrorReadLine();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the core process gracefully, then forcefully after a timeout.
    /// </summary>
    public async Task StopAsync()
    {
        if (_process is not { HasExited: false })
            return;

        try
        {
            _process.StandardInput.Close();
        }
        catch
        {
            // Pipe may already be closed
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }
        _process?.Dispose();
    }

    private static string ResolveCorePath(string? overridePath)
    {
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);

        string? envPath = Environment.GetEnvironmentVariable("DAEMON_CORE_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return Path.GetFullPath(envPath);

        string entryDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? ".";
        string repoRoot = Path.GetFullPath(Path.Combine(entryDir, "../../../.."));

        string[] candidates =
        [
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Debug/net10.0/Dmon.Core.dll"),
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Release/net10.0/Dmon.Core.dll"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Could not find Dmon.Core.dll. " +
            "Ensure the core project is built, or set DAEMON_CORE_PATH env var / --core-path argument.",
            "Dmon.Core.dll");
    }
}
