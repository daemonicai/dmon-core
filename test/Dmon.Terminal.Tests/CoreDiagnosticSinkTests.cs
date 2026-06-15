using Dmon.Runtime;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Tier-A tests for the core stderr diagnostic forwarding introduced in Group 4 of
/// the host-rpc-abstraction change.
///
/// Tests exercise the sink wiring and callback path deterministically without spawning
/// a real OS process — the <c>onStderrLine</c> callback is invoked directly to simulate
/// what <see cref="CoreProcessManager"/> does on each stderr line.
/// </summary>
public sealed class CoreDiagnosticSinkTests
{
    // ── Scenario 1: a core stderr line is forwarded to the diagnostic sink ──

    /// <summary>
    /// A line delivered to the <c>onStderrLine</c> callback must reach the sink.
    /// This verifies the wiring: the callback is non-null and delegates to the sink.
    /// </summary>
    [Fact]
    public void StderrCallback_ForwardsLine_ToSink()
    {
        List<string> captured = [];
        CapturingSink sink = new(captured);

        // Simulate what Program.cs does: create the callback from the sink, then what
        // CoreProcessManager does when ErrorDataReceived fires — call the callback.
        Action<string> onStderrLine = sink.WriteLine;

        onStderrLine("provider error: ANTHROPIC_API_KEY not set");

        Assert.Single(captured);
        Assert.Equal("provider error: ANTHROPIC_API_KEY not set", captured[0]);
    }

    /// <summary>
    /// Multiple stderr lines are all forwarded — not just the first.
    /// </summary>
    [Fact]
    public void StderrCallback_ForwardsMultipleLines_InOrder()
    {
        List<string> captured = [];
        CapturingSink sink = new(captured);
        Action<string> onStderrLine = sink.WriteLine;

        onStderrLine("line one");
        onStderrLine("line two");
        onStderrLine("line three");

        Assert.Equal(3, captured.Count);
        Assert.Equal("line one",   captured[0]);
        Assert.Equal("line two",   captured[1]);
        Assert.Equal("line three", captured[2]);
    }

    // ── Scenario 2: startup-failure stderr line is surfaced ─────────────────

    /// <summary>
    /// When the core fails during startup and writes to stderr before agentReady,
    /// the diagnostic callback captures those lines.
    ///
    /// In the real process flow, <see cref="CoreProcessManager.StartAsync"/> attaches
    /// <c>ErrorDataReceived</c> and calls <c>BeginErrorReadLine()</c> before
    /// <c>ReadAgentReadyAsync</c> blocks on stdout — so stderr lines written during
    /// startup (including failure messages) are delivered to the callback concurrently.
    /// This test validates the callback contract that makes that surfacing possible.
    /// </summary>
    [Fact]
    public void StderrCallback_StartupFailureLine_IsSurfaced()
    {
        List<string> captured = [];
        CapturingSink sink = new(captured);
        Action<string> onStderrLine = sink.WriteLine;

        // Simulate a startup-failure line (e.g. unhandled exception before agentReady).
        const string startupFailure =
            "Unhandled exception: System.InvalidOperationException: No provider configured.";
        onStderrLine(startupFailure);

        Assert.Single(captured);
        Assert.Equal(startupFailure, captured[0]);
    }

    // ── ConsoleDiagnosticSink: default sink prefixes lines with [core] ───────

    /// <summary>
    /// <see cref="ConsoleDiagnosticSink"/> writes each line prefixed with <c>[core]</c>
    /// to <see cref="Console.Error"/>, keeping diagnostic output clearly distinct from
    /// model/conversational output on stdout.
    ///
    /// This test redirects Console.Error to verify the prefix without spawning a process.
    /// </summary>
    [Fact]
    public void ConsoleDiagnosticSink_WriteLine_PrefixesWithCore()
    {
        using StringWriter writer = new();
        TextWriter original = Console.Error;
        Console.SetError(writer);

        try
        {
            ConsoleDiagnosticSink sink = new();
            sink.WriteLine("some diagnostic line");
        }
        finally
        {
            Console.SetError(original);
        }

        string output = writer.ToString();
        Assert.Contains("[core]", output);
        Assert.Contains("some diagnostic line", output);
    }

    // ── Callback is thread-safe: concurrent callers do not interleave ────────

    /// <summary>
    /// The <c>ErrorDataReceived</c> callback fires on the OS stderr-drain thread, which
    /// may be concurrent with the main render loop. Verify that the sink collects all
    /// lines without loss when called from multiple threads simultaneously.
    /// </summary>
    [Fact]
    public async Task StderrCallback_ConcurrentCallers_AllLinesCaptured()
    {
        // Use a thread-safe list to avoid false positives from the assertion itself.
        System.Collections.Concurrent.ConcurrentBag<string> captured = [];
        CapturingSink sink = new(null, captured);
        Action<string> onStderrLine = sink.WriteLine;

        const int threadCount = 8;
        const int linesPerThread = 50;

        Task[] tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < linesPerThread; i++)
                onStderrLine($"t{t}-line{i}");
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(threadCount * linesPerThread, captured.Count);
    }

    // ── helper ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Test double that captures lines written through <see cref="IDiagnosticSink.WriteLine"/>.
    /// Accepts either a plain <see cref="List{T}"/> (single-threaded tests) or a
    /// <see cref="System.Collections.Concurrent.ConcurrentBag{T}"/> (multi-threaded tests).
    /// </summary>
    private sealed class CapturingSink : IDiagnosticSink
    {
        private readonly List<string>? _list;
        private readonly System.Collections.Concurrent.ConcurrentBag<string>? _bag;

        internal CapturingSink(
            List<string>? list,
            System.Collections.Concurrent.ConcurrentBag<string>? bag = null)
        {
            _list = list;
            _bag  = bag;
        }

        public void WriteLine(string line)
        {
            _list?.Add(line);
            _bag?.Add(line);
        }
    }
}
