using System.Text.Json;
using Dcli;
using Dmon.Abstractions.Providers;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Providers;
using Dmon.Providers.Ollama;
using Dmon.Terminal;

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
using CoreProcessManager coreProcess = new(corePathOverride);

await coreProcess.StartAsync().ConfigureAwait(false);

// Single ITerminal instance for the lifetime of the process; dcli owns the fixed region.
await using ITerminal terminal = await Dcli.Terminal.StartAsync(
    new TerminalOptions(), cts.Token).ConfigureAwait(false);

// Long-lived across restarts — created once, shared by all sessions.
InputReader inputReader = new();
TerminalRenderer renderer = new(terminal);
renderer.PrintSeparator("dmon");

async Task SendCommandAsync(Command cmd, CancellationToken ct)
{
    // Serialize as the base Command type so [JsonPolymorphic] emits the "type" discriminator.
    // Passing cmd.GetType() (the concrete type) bypasses the polymorphic attributes and omits it.
    string json = JsonSerializer.Serialize(cmd, jsonOptions);
    // ADR-003: strict LF framing. Do not rely on StreamWriter.NewLine (CRLF on Windows).
    await coreProcess.StandardInput.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
    await coreProcess.StandardInput.WriteAsync('\n').ConfigureAwait(false);
    await coreProcess.StandardInput.FlushAsync(ct).ConfigureAwait(false);
}

IReadOnlyList<IProviderFactory> providerFactories =
[
    new AnthropicProviderFactory(),
    new OpenAiProviderFactory(),
    new GeminiProviderFactory(),
    new OllamaProviderFactory(),
];

bool reloadRequested = false;

ConsoleEventHandler handler = new(
    renderer,
    inputReader,
    SendCommandAsync,
    cts,
    providerFactories,
    requestReload: () => reloadRequested = true);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Task inputTask = inputReader.RunAsync(cts.Token);

IAsyncEnumerator<string> inputEnum = inputReader.ReadLinesAsync(cts.Token)
    .GetAsyncEnumerator(cts.Token);

Task? dispatchTask = null;

try
{
    Task<bool> nextInputLine = inputEnum.MoveNextAsync().AsTask();

    bool restart;
    do
    {
        // Build a per-session dispatcher on the current process's stdout.
        EventDispatcher dispatcher = new(coreProcess.StandardOutput);
        dispatchTask = dispatcher.RunAsync(cts.Token);

        Task<bool> nextEvent = dispatcher.Events.WaitToReadAsync(cts.Token).AsTask();

        // Returns true if a /reload was requested, false if the session ended normally.
        async Task<bool> RunSessionAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                Task completed = await Task.WhenAny(nextInputLine, nextEvent).ConfigureAwait(false);

                // Drain any events that arrived while we were waiting.
                while (dispatcher.Events.TryRead(out Event? evt))
                {
                    await handler.HandleAsync(evt, cts.Token).ConfigureAwait(false);
                }

                if (completed == nextEvent)
                {
                    bool hasEvents;
                    try { hasEvents = await nextEvent.ConfigureAwait(false); }
                    catch (OperationCanceledException) { return false; }

                    // Channel completed: the core process closed its output stream.
                    if (!hasEvents) return false;

                    nextEvent = dispatcher.Events.WaitToReadAsync(cts.Token).AsTask();
                }
                else
                {
                    bool hasLine;
                    try { hasLine = await nextInputLine.ConfigureAwait(false); }
                    catch (OperationCanceledException) { return false; }

                    if (!hasLine) return false;

                    string line = inputEnum.Current;
                    await handler.HandleUserInputAsync(line, cts.Token).ConfigureAwait(false);
                    nextInputLine = inputEnum.MoveNextAsync().AsTask();

                    if (reloadRequested)
                    {
                        reloadRequested = false;
                        return true;
                    }
                }
            }

            return false;
        }

        restart = await RunSessionAsync().ConfigureAwait(false);

        if (restart && !cts.IsCancellationRequested)
        {
            // Drain any buffered events before tearing down the old dispatcher.
            while (dispatcher.Events.TryRead(out Event? leftover))
            {
                await handler.HandleAsync(leftover, cts.Token).ConfigureAwait(false);
            }

            // Wait for the old dispatcher's RunAsync to finish (it completes on old stdout EOF
            // after StopAsync closes the process).
            await coreProcess.RestartAsync().ConfigureAwait(false);
            try { await dispatchTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }

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

            renderer.AddSystemLine("[Reload] Core restarted.");
        }
    }
    while (restart && !cts.IsCancellationRequested);
}
catch (OperationCanceledException) { }
finally
{
    // ChannelReader<T>.ReadAllAsync in .NET 10 throws NotSupportedException from
    // DisposeAsync when the enumerator is still suspended at WaitToReadAsync.
    // OperationCanceledException is also expected here on normal shutdown.
    try { await inputEnum.DisposeAsync().ConfigureAwait(false); }
    catch (NotSupportedException) { }
    catch (OperationCanceledException) { }
}

await coreProcess.StopAsync().ConfigureAwait(false);
renderer.PrintSeparator("goodbye");

try
{
    await Task.WhenAll(dispatchTask ?? Task.CompletedTask, inputTask).ConfigureAwait(false);
}
catch (OperationCanceledException) { }
