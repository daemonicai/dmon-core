using System.Diagnostics;
using System.Reflection;

namespace Dmon.Terminal;

/// <summary>
/// Manages the Dmon.Core process lifecycle.
/// Spawns the core via <c>dotnet exec</c>, connects stdio pipes,
/// and provides graceful shutdown.
/// </summary>
public sealed class CoreProcessManager : IDisposable
{
    private Process? _process;
    private readonly string _corePath;
    private readonly string? _workingDirectory;
    private readonly Action<string>? _onStderrLine;
    private bool _disposed;

    public CoreProcessManager(
        string? corePathOverride,
        string? workingDirectory = null,
        Action<string>? onStderrLine = null)
    {
        _corePath = ResolveCorePath(corePathOverride);
        _workingDirectory = workingDirectory;
        _onStderrLine = onStderrLine;
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
    /// The OS process ID of the running core process, or null if not started.
    /// </summary>
    internal int? ProcessId => _process?.Id;

    /// <summary>
    /// Starts the core process. Does not wait for <c>agentReady</c>.
    /// </summary>
    public Task StartAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _corePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (_workingDirectory is not null)
            psi.WorkingDirectory = _workingDirectory;

        _process = new Process { StartInfo = psi };

        // Drain stderr to prevent the child process blocking on a full pipe buffer.
        // Core logs are structured JSON on stderr; the terminal host does not forward them.
        // When _onStderrLine is set (e.g. in tests), route each line to that callback instead.
        if (_onStderrLine is not null)
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _onStderrLine(e.Data); };
        else
            _process.ErrorDataReceived += (_, _) => { };

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

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
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

    /// <summary>
    /// Stops the current core process and spawns a fresh one.
    /// The old process's session lock is released before the new process starts.
    /// </summary>
    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _process?.Dispose();
        _process = null;
        await StartAsync().ConfigureAwait(false);
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

        string? envPath = Environment.GetEnvironmentVariable("DMON_CORE_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return Path.GetFullPath(envPath);

        string entryDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? ".";

        // Published layout: dmoncore/ sits next to the dmon executable.
        string publishedCandidate = Path.Combine(entryDir, "dmoncore", "dmoncore");
        if (File.Exists(publishedCandidate))
            return Path.GetFullPath(publishedCandidate);

        // Dev layout: walk back to the repo root and find the bin/ output.
        string repoRoot = Path.GetFullPath(Path.Combine(entryDir, "../../../.."));

        string[] devCandidates =
        [
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Debug/net10.0/dmoncore"),
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Release/net10.0/dmoncore"),
        ];

        foreach (string candidate in devCandidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Could not find dmoncore. " +
            "Run 'make build' to produce the published layout, or set DMON_CORE_PATH env var / --core-path argument.",
            "dmoncore");
    }
}
