using System.Text.Json;
using Dmon.Protocol.Commands;
using Terminal.Gui.App;

namespace Dmon.Tui;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static async Task Main(string[] args)
    {
        string? corePath = ParseCorePath(args);

        using CoreProcessManager coreProcess = new(corePath);
        await coreProcess.StartAsync().ConfigureAwait(false);

        EventDispatcher dispatcher = new(coreProcess.StandardOutput);

        CancellationTokenSource cts = new();

        using IApplication app = Application.Create();
        app.Init();

        DmonWindow window = new();

        async Task SendCommandAsync(Command command, CancellationToken cancellationToken)
        {
            // Serialize against the base Command type so the polymorphic "type" discriminator
            // is emitted (ADR-003). Serializing the concrete runtime type omits it.
            string json = JsonSerializer.Serialize(command, JsonOptions);
            // ADR-003 mandates strict LF framing; WriteLineAsync would emit CRLF on Windows.
            await coreProcess.StandardInput.WriteAsync($"{json}\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await coreProcess.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        TuiEventHandler handler = new(window, app, SendCommandAsync);

        window.UserInput += (text) => Task.Run(() => handler.HandleUserInputAsync(text, cts.Token));

        window.StartEventLoop(dispatcher.Events, handler, app, cts.Token);

        _ = dispatcher.RunAsync(cts.Token);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            app.Invoke(() => app.RequestStop());
        };

        try
        {
            app.Run(window, null);
        }
        catch (OperationCanceledException)
        {
            // Shutting down — swallow silently.
        }

        await coreProcess.StopAsync().ConfigureAwait(false);
    }

    private static string? ParseCorePath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--core-path")
                return args[i + 1];
        }
        return null;
    }
}
