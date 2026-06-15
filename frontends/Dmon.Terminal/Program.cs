using Dcli;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Runtime;
using Dmon.Terminal;

// Forward core stderr to host stderr so launch failures and diagnostics are visible.
ConsoleDiagnosticSink diagnosticSink = new();

// Dispatch top-level subcommands before starting the TUI.
if (args.Length > 0 && args[0] == "init")
{
    int exitCode = InitCommand.Run(
        Directory.GetCurrentDirectory(),
        Console.Out,
        Console.Error);
    return exitCode;
}

string? corePathOverride = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--core-path")
    {
        corePathOverride = args[i + 1];
        break;
    }
}

using CancellationTokenSource cts = new();

CoreLauncher launcher = new();
CoreSession coreSession = await launcher
    .StartProtocolCompatibleCoreAsync(
        corePathOverride,
        onStderrLine: diagnosticSink.WriteLine,
        cancellationToken: cts.Token)
    .ConfigureAwait(false);

// Single ITerminal instance for the lifetime of the process; dcli owns the fixed region.
await using ITerminal terminal = await Dcli.Terminal.StartAsync(
    new TerminalOptions(), cts.Token).ConfigureAwait(false);

// Long-lived across restarts — created once, shared by all sessions.
InputStateLayer inputStateLayer = new();
TerminalRenderer renderer = new(terminal);
renderer.PrintSeparator("dmon");

// Render the initial agentReady readiness that was consumed by the compatibility gate.
AgentReadyEvent initialReady = coreSession.AgentReady;
renderer.AddSystemLine(
    $"[Ready] dmon core v{initialReady.CoreVersion} (protocol {initialReady.ProtocolVersion})");

// Per-session RPC client — reset on each /reload.
// Declared before handler so the send lambda captures the variable (not a fixed instance).
IRpcClient client = BuildClient(coreSession);

// Per-session cancellation; cancelled when the user requests /reload.
// Recreated after each RestartAsync (point B) so a second /reload during the
// restart window hits the already-cancelled source and is a no-op.
CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

// Guards the read-and-cancel in requestReload against the dispose-and-swap at point B
// and against the shutdown finally dispose.
// Without this lock a concurrent reload request can call Cancel() on an already-disposed
// CancellationTokenSource, escalating a harmless double-reload into an app shutdown.
object reloadGate = new();
// Set to true under reloadGate before sessionCts is disposed in the shutdown finally.
// requestReload checks this flag so a post-shutdown reload is a safe no-op.
bool reloadClosed = false;

ConsoleEventHandler handler = new(
    renderer,
    inputStateLayer,
    sendCommand: async (cmd, ct) => await client.SendAsync(cmd, ct).ConfigureAwait(false),
    cts,
    requestReload: () => { lock (reloadGate) { if (!reloadClosed) sessionCts.Cancel(); } },
    terminal: terminal);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Task drainTask = handler.DrainAsync(terminal.Events, cts.Token);

IRpcClient BuildClient(CoreSession session)
{
    CoreProcessRpcTransport transport = new(session.Process);
    return new RpcClient(transport);
}

try
{
    bool restart;
    do
    {
        // Subscribe before starting the pump so no events are missed.
        // BroadcastSubscription registers its channel synchronously in the ctor.
        IAsyncEnumerable<Event> eventStream = client.Events;
        await client.StartAsync(cts.Token).ConfigureAwait(false);

        // Returns true if a /reload was requested, false if the session ended normally.
        // Input flows exclusively through DrainAsync (dcli events); /reload cancels sessionCts,
        // which unblocks the await-foreach without touching the outer cts.
        async Task<bool> RunSessionAsync()
        {
            try
            {
                await foreach (Event evt in eventStream
                    .WithCancellation(sessionCts.Token)
                    .ConfigureAwait(false))
                {
                    await handler.HandleRpcEventAsync(evt, cts.Token).ConfigureAwait(false);
                }
                // Stream completed normally (core closed stdout) — no restart.
                return false;
            }
            catch (OperationCanceledException) when (sessionCts.IsCancellationRequested && !cts.IsCancellationRequested)
            {
                // /reload cancelled sessionCts but not the outer cts → restart.
                return true;
            }
            catch (OperationCanceledException)
            {
                // Outer cts cancelled (Ctrl+C or /quit) → exit cleanly.
                return false;
            }
        }

        restart = await RunSessionAsync().ConfigureAwait(false);

        if (restart && !cts.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);

            // RestartAsync: stops the old process, starts a fresh one, and re-runs the
            // protocol gate on the new process's stdout.
            coreSession = await launcher
                .RestartAsync(coreSession, cts.Token)
                .ConfigureAwait(false);

            // Recreate here (point B) so a second /reload during the restart window hits the
            // already-cancelled sessionCts and is a no-op, rather than cancelling the fresh one.
            // Lock reloadGate so requestReload cannot Cancel() the CTS mid-dispose.
            lock (reloadGate)
            {
                sessionCts.Dispose();
                sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            }

            client = BuildClient(coreSession);

            AgentReadyEvent reloadReady = coreSession.AgentReady;
            renderer.AddSystemLine(
                $"[Reload] Core restarted. dmon core v{reloadReady.CoreVersion} (protocol {reloadReady.ProtocolVersion})");

            // Re-open the active session on the fresh process so it re-acquires the lock.
            if (handler.ActiveSessionId is not null)
            {
                SessionLoadCommand loadCmd = new()
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Path = handler.ActiveSessionId,
                };
                try { await client.SendAsync(loadCmd, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }
    while (restart && !cts.IsCancellationRequested);
}
catch (OperationCanceledException) { }
finally
{
    // Set reloadClosed under the gate before disposing so a concurrent requestReload
    // call on the drainTask (still live until line ~176) sees the flag and skips
    // Cancel() rather than throwing ObjectDisposedException on the disposed CTS.
    lock (reloadGate) { reloadClosed = true; sessionCts.Dispose(); }
    await client.DisposeAsync().ConfigureAwait(false);
}

renderer.PrintSeparator("goodbye");
await coreSession.Process.StopAsync().ConfigureAwait(false);

try
{
    await drainTask.ConfigureAwait(false);
}
catch (OperationCanceledException) { }

return 0;
