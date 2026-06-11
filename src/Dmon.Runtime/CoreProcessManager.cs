using System.Diagnostics;

namespace Dmon.Runtime;

/// <summary>
/// Manages the dmoncore process lifecycle.
/// Spawns the core either directly (apphost override/dev tiers) or via
/// <c>dotnet exec</c> (cached NuGet package tier), connects stdio pipes,
/// and provides graceful shutdown.
/// </summary>
public sealed class CoreProcessManager : ICoreProcess
{
    private Process? _process;
    private readonly ResolvedCore _resolvedCore;
    private readonly string? _workingDirectory;
    private readonly Action<string>? _onStderrLine;
    private bool _disposed;

    internal CoreProcessManager(
        ResolvedCore resolvedCore,
        string? workingDirectory = null,
        Action<string>? onStderrLine = null)
    {
        _resolvedCore = resolvedCore;
        _workingDirectory = workingDirectory;
        _onStderrLine = onStderrLine;
    }

    /// <summary>
    /// Constructs a manager for a known apphost path (override / dev tier).
    /// Intended for tests and callers that have already resolved the path externally.
    /// </summary>
    public CoreProcessManager(
        string? corePathOverride,
        string? workingDirectory = null,
        Action<string>? onStderrLine = null)
        : this(
            ResolveDirectExecutable(corePathOverride),
            workingDirectory,
            onStderrLine)
    {
    }

    private static ResolvedCore ResolveDirectExecutable(string? corePathOverride)
    {
        if (!string.IsNullOrEmpty(corePathOverride) && File.Exists(corePathOverride))
            return new ResolvedCore(Path.GetFullPath(corePathOverride), LaunchMode.DirectExecutable);

        string? envPath = Environment.GetEnvironmentVariable("DMON_CORE_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return new ResolvedCore(Path.GetFullPath(envPath), LaunchMode.DirectExecutable);

        string entryDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetEntryAssembly()?.Location) ?? ".";

        // Published layout: dmoncore/ sits next to the dmon executable.
        string publishedCandidate = Path.Combine(entryDir, "dmoncore", "dmoncore");
        if (File.Exists(publishedCandidate))
            return new ResolvedCore(Path.GetFullPath(publishedCandidate), LaunchMode.DirectExecutable);

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
                return new ResolvedCore(Path.GetFullPath(candidate), LaunchMode.DirectExecutable);
        }

        throw new FileNotFoundException(
            "Could not find dmoncore. " +
            "Run 'make build' to produce the published layout, or set DMON_CORE_PATH / --core-path.",
            "dmoncore");
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

    // Explicit interface implementations widen the concrete StreamReader/StreamWriter
    // properties to the TextReader/TextWriter interface surface.
    TextReader ICoreProcess.StandardOutput => StandardOutput;
    TextWriter ICoreProcess.StandardInput => StandardInput;

    /// <summary>
    /// The OS process ID of the running core process, or null if not started.
    /// </summary>
    internal int? ProcessId => _process?.Id;

    /// <summary>
    /// Starts the core process.
    /// </summary>
    public Task StartAsync()
    {
        ProcessStartInfo psi = BuildProcessStartInfo(_resolvedCore);

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
    /// Stops the core process gracefully, then forcefully after a 500 ms timeout.
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
            // Pipe may already be closed.
        }

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(500));
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

    private static ProcessStartInfo BuildProcessStartInfo(ResolvedCore core)
    {
        ProcessStartInfo psi;

        if (core.LaunchMode == LaunchMode.DotnetExec)
        {
            // Cached NuGet publish closure: dotnet exec <dmoncore.dll>
            // The deps.json / runtimeconfig.json in the same directory resolve all dependencies.
            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
            };
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add(core.Path);
        }
        else
        {
            // Override / dev tier: launch the apphost executable directly.
            psi = new ProcessStartInfo
            {
                FileName = core.Path,
            };
        }

        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        return psi;
    }
}
