using System.Text.Json;
using Dcli;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Runtime;
using Dmon.Terminal;

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

JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

using CancellationTokenSource cts = new();

CoreLauncher launcher = new();
CoreSession coreSession = await launcher
    .StartProtocolCompatibleCoreAsync(corePathOverride, cancellationToken: cts.Token)
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

async Task SendCommandAsync(Command cmd, CancellationToken ct)
{
    // Serialize as the base Command type so [JsonPolymorphic] emits the "type" discriminator.
    // Passing cmd.GetType() (the concrete type) bypasses the polymorphic attributes and omits it.
    string json = JsonSerializer.Serialize(cmd, jsonOptions);
    // ADR-003: strict LF framing. Do not rely on StreamWriter.NewLine (CRLF on Windows).
    await coreSession.Process.StandardInput.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
    await coreSession.Process.StandardInput.WriteAsync('\n').ConfigureAwait(false);
    await coreSession.Process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
}

// Signals the session loop to restart when /reload is submitted via DrainAsync.
TaskCompletionSource<bool> reloadSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

ConsoleEventHandler handler = new(
    renderer,
    inputStateLayer,
    SendCommandAsync,
    cts,
    requestReload: () => reloadSignal.TrySetResult(true),
    terminal: terminal);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Task drainTask = handler.DrainAsync(terminal.Events, cts.Token);

Task? dispatchTask = null;

try
{
    bool restart;
    do
    {
        // Build a per-session dispatcher on the current process's stdout.
        // StandardOutput is positioned after the agentReady line already consumed by the gate.
        EventDispatcher dispatcher = new(coreSession.Process.StandardOutput);
        dispatchTask = dispatcher.RunAsync(cts.Token);

        Task<bool> nextEvent = dispatcher.Events.WaitToReadAsync(cts.Token).AsTask();

        // Returns true if a /reload was requested, false if the session ended normally.
        // Input flows exclusively through DrainAsync (dcli events); /reload unblocks the loop
        // via reloadSignal rather than through the RPC event channel.
        async Task<bool> RunSessionAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                Task completed = await Task.WhenAny(nextEvent, reloadSignal.Task).ConfigureAwait(false);

                if (completed == reloadSignal.Task)
                {
                    return true;
                }

                bool hasEvents;
                try { hasEvents = await nextEvent.ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }

                // Channel completed: the core process closed its output stream.
                if (!hasEvents) return false;

                // Drain all available events.
                while (dispatcher.Events.TryRead(out Event? evt))
                {
                    await handler.HandleRpcEventAsync(evt, cts.Token).ConfigureAwait(false);
                }

                nextEvent = dispatcher.Events.WaitToReadAsync(cts.Token).AsTask();
            }

            return false;
        }

        restart = await RunSessionAsync().ConfigureAwait(false);

        if (restart && !cts.IsCancellationRequested)
        {
            // Drain any buffered events before tearing down the old dispatcher.
            while (dispatcher.Events.TryRead(out Event? leftover))
            {
                await handler.HandleRpcEventAsync(leftover, cts.Token).ConfigureAwait(false);
            }

            // RestartAsync: stops the old process, starts a fresh one, and re-runs the
            // protocol gate on the new process's stdout.
            coreSession = await launcher
                .RestartAsync(coreSession, cts.Token)
                .ConfigureAwait(false);

            // Recreate here (point B) so a second /reload during the restart window hits the
            // already-completed TCS and is a no-op, rather than lighting up the freshly-reset one.
            reloadSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { await dispatchTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }

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
                try { await SendCommandAsync(loadCmd, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }
    while (restart && !cts.IsCancellationRequested);
}
catch (OperationCanceledException) { }

renderer.PrintSeparator("goodbye");
await coreSession.Process.StopAsync().ConfigureAwait(false);

try
{
    await Task.WhenAll(dispatchTask ?? Task.CompletedTask, drainTask).ConfigureAwait(false);
}
catch (OperationCanceledException) { }

return 0;
