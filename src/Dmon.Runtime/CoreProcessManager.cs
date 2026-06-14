using System.Diagnostics;

namespace Dmon.Runtime;

/// <summary>
/// Manages the dmoncore process lifecycle.
/// Spawns the core directly (apphost), via <c>dotnet exec</c> (prebuilt closure), or via
/// <c>dotnet run --no-build</c> (file-based program), connects stdio pipes,
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
    /// The resolved core descriptor (path + launch mode) used by this manager.
    /// Exposed so <see cref="CoreLauncher"/> can inspect the mode during restart.
    /// </summary>
    internal ResolvedCore ResolvedCore => _resolvedCore;

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

    /// <summary>
    /// Runs <c>dotnet build &lt;dmonCsPath&gt;</c> as a separate captured process.
    /// The SDK incremental up-to-date check acts as the staleness gate — an unchanged
    /// <c>Dmon.cs</c> completes nearly instantly with no restore or recompile.
    /// Stdout and stderr are captured; build output never reaches the JSONL/stdio channel.
    /// </summary>
    /// <param name="dmonCsPath">Absolute path to the <c>Dmon.cs</c> file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="CoreAcquisitionException">The build failed.</exception>
    internal static async Task BuildFileBasedProgramAsync(
        string dmonCsPath,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(dmonCsPath) ?? ".",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(dmonCsPath);
        psi.ArgumentList.Add("--tl:off");

        using Process proc = new() { StartInfo = psi };
        proc.Start();

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            throw new CoreAcquisitionException(
                $"Failed to build '{dmonCsPath}' (exit {proc.ExitCode}).\n" +
                $"Build output:\n{stdout}\n{stderr}");
        }
    }

    private static ProcessStartInfo BuildProcessStartInfo(ResolvedCore core)
    {
        ProcessStartInfo psi;

        switch (core.LaunchMode)
        {
            case LaunchMode.DotnetExec:
                // Prebuilt publish closure: dotnet exec <dmoncore.dll>
                // The deps.json / runtimeconfig.json in the same directory resolve all dependencies.
                psi = new ProcessStartInfo { FileName = "dotnet" };
                psi.ArgumentList.Add("exec");
                psi.ArgumentList.Add(core.Path);
                break;

            case LaunchMode.FileBasedProgram:
                // File-based program: dotnet run <Dmon.cs> --no-build
                // --no-build skips the build phase (and implies --no-restore), so no MSBuild output
                // reaches stdout. The first stdout line will be the agentReady JSONL frame.
                psi = new ProcessStartInfo { FileName = "dotnet" };
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add(core.Path);
                psi.ArgumentList.Add("--no-build");
                break;

            default:
                // DirectExecutable: launch the apphost executable directly.
                psi = new ProcessStartInfo { FileName = core.Path };
                break;
        }

        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        return psi;
    }
}
