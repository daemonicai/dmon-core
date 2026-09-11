using System.Diagnostics;
using System.Text;

namespace Dmon.Tests.Shared;

/// <summary>
/// Result of a <see cref="ProcessRunner"/> run. <see cref="OutputTruncated"/> is true when
/// a descendant process was still holding the output pipes after the drain grace elapsed —
/// <see cref="StandardOutput"/> and <see cref="StandardError"/> then hold only what arrived
/// before the grace ran out, not the full output.
/// </summary>
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool OutputTruncated)
{
    /// <summary>
    /// " (output truncated — a descendant still held the pipe)" when <see cref="OutputTruncated"/>
    /// is true, otherwise empty — for appending to a failure message without a ternary at
    /// every call site.
    /// </summary>
    public string TruncatedNote => OutputTruncated ? " (output truncated — a descendant still held the pipe)" : "";
}

/// <summary>
/// Runs a child process with a bounded wait for exit and a bounded grace for the output
/// pumps to drain, so a descendant that outlives the process it was spawned by can never
/// block the caller indefinitely. <c>dotnet build</c>/<c>pack</c>/<c>restore</c> spawn
/// reusable MSBuild worker nodes (<c>/nodeReuse:true</c>, the default) that inherit the
/// redirected pipes; when the parent exits but a node stays alive, plain EOF-based reads
/// (<c>ReadToEndAsync</c> awaited with no timeout) never complete.
/// </summary>
internal static class ProcessRunner
{
    private static readonly TimeSpan DefaultDrainGrace = TimeSpan.FromSeconds(10);

    public static Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => RunAsync(fileName, arguments, workingDirectory, timeout, DefaultDrainGrace, cancellationToken);

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        TimeSpan drainGrace,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Propagates through `bash pack-core.sh` to every `dotnet pack` it runs — stops a
        // worker node from surviving the script and holding the pipe open behind it.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using Process process = new() { StartInfo = psi };
        process.Start();

        // Pump stdout/stderr into a thread-safe snapshot instead of awaiting ReadToEndAsync,
        // so a snapshot is always available even if EOF never arrives. Do NOT switch to
        // BeginOutputReadLine/OutputDataReceived: since .NET 5, WaitForExitAsync (and the
        // parameterless WaitForExit()) also waits for EOF on async-mode redirected streams,
        // which reproduces the exact hang this helper exists to avoid.
        OutputPump stdoutPump = new(process.StandardOutput);
        OutputPump stderrPump = new(process.StandardError);
        Task stdoutPumpTask = stdoutPump.RunAsync();
        Task stderrPumpTask = stderrPump.RunAsync();

        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested) throw;

            throw new TimeoutException(
                $"{fileName} {arguments} timed out after {timeout}.\n" +
                $"stdout so far: {stdoutPump.Snapshot()}\nstderr so far: {stderrPump.Snapshot()}");
        }

        using CancellationTokenSource graceCts = new(drainGrace);
        bool outputTruncated = false;
        try
        {
            await Task.WhenAll(stdoutPumpTask, stderrPumpTask).WaitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A descendant still holds a pipe open (the same node-reuse shape as the exit
            // wait above). The pump tasks keep running in the background and catch every
            // exception internally, so returning here leaves nothing unobserved.
            outputTruncated = true;
        }

        return new ProcessResult(process.ExitCode, stdoutPump.Snapshot(), stderrPump.Snapshot(), outputTruncated);
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    private sealed class OutputPump(StreamReader reader)
    {
        private readonly StringBuilder _buffer = new();
        private readonly object _gate = new();

        public async Task RunAsync()
        {
            try
            {
                char[] chunk = new char[4096];
                while (true)
                {
                    int read = await reader.ReadAsync(chunk).ConfigureAwait(false);
                    if (read == 0) break;

                    lock (_gate)
                    {
                        _buffer.Append(chunk, 0, read);
                    }
                }
            }
            catch
            {
                // May run long after RunAsync returns and the caller disposes the Process
                // (and this reader with it); must never surface as an unobserved fault.
            }
        }

        public string Snapshot()
        {
            lock (_gate)
            {
                return _buffer.ToString();
            }
        }
    }
}
