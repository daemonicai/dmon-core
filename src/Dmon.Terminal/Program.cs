using System.Text.Json;
using Dmon.Abstractions.Providers;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Providers;
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

TerminalRenderer renderer = new();
renderer.PrintSeparator("dmon");

await coreProcess.StartAsync().ConfigureAwait(false);

EventDispatcher dispatcher = new(coreProcess.StandardOutput);
InputReader inputReader = new();

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
];

ConsoleEventHandler handler = new(renderer, inputReader, SendCommandAsync, cts, providerFactories);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Task dispatchTask = dispatcher.RunAsync(cts.Token);
Task inputTask = inputReader.RunAsync(cts.Token);

renderer.PrintPrompt();

IAsyncEnumerator<string> inputEnum = inputReader.ReadLinesAsync(cts.Token)
    .GetAsyncEnumerator(cts.Token);
try
{
    Task<bool> nextInputLine = inputEnum.MoveNextAsync().AsTask();
    Task<bool> nextEvent = dispatcher.Events.WaitToReadAsync(cts.Token).AsTask();

    while (!cts.IsCancellationRequested)
    {
        Task completed = await Task.WhenAny(nextInputLine, nextEvent).ConfigureAwait(false);

        while (dispatcher.Events.TryRead(out Event? evt))
        {
            await handler.HandleAsync(evt, cts.Token).ConfigureAwait(false);
        }

        if (completed == nextEvent)
        {
            bool hasEvents;
            try { hasEvents = await nextEvent.ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            // Channel completed: the core process closed its output stream.
            if (!hasEvents) break;

            nextEvent = dispatcher.Events.WaitToReadAsync(cts.Token).AsTask();
        }
        else
        {
            bool hasLine;
            try { hasLine = await nextInputLine.ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            if (!hasLine) break;

            string line = inputEnum.Current;
            await handler.HandleUserInputAsync(line, cts.Token).ConfigureAwait(false);
            nextInputLine = inputEnum.MoveNextAsync().AsTask();
        }
    }
}
catch (OperationCanceledException) { }
finally
{
    await inputEnum.DisposeAsync().ConfigureAwait(false);
}

await coreProcess.StopAsync().ConfigureAwait(false);
renderer.PrintSeparator("goodbye");

try
{
    await Task.WhenAll(dispatchTask, inputTask).ConfigureAwait(false);
}
catch (OperationCanceledException) { }
