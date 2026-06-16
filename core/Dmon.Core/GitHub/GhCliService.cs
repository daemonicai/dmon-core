using System.Diagnostics;

namespace Dmon.Core.GitHub;

/// <summary>
/// Runs <c>gh auth status</c> once and caches the result for the session lifetime.
/// </summary>
public sealed class GhCliService : IGhCliService
{
    private readonly Func<CancellationToken, Task<bool>> _probe;
    private bool? _cached;

    /// <summary>
    /// Production constructor — probes the real <c>gh</c> CLI.
    /// </summary>
    public GhCliService() : this(RunGhAuthStatusAsync) { }

    /// <summary>
    /// Test constructor — supply a custom probe so no real process is spawned.
    /// </summary>
    internal GhCliService(Func<CancellationToken, Task<bool>> probe)
    {
        _probe = probe;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (_cached.HasValue)
            return _cached.Value;

        _cached = await _probe(cancellationToken);
        return _cached.Value;
    }

    private static async Task<bool> RunGhAuthStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "gh",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("auth");
            process.StartInfo.ArgumentList.Add("status");

            process.Start();

            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                // Process may have already exited between the cancellation and this call;
                // swallowing is intentional — this method's contract is to never throw.
                // Kill can race the process exiting (InvalidOperationException) or fail
                // at the OS level; either way there is nothing left to do, so ignore it.
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
        }
        catch
        {
            // gh not installed or any other OS-level failure
            return false;
        }
    }
}
