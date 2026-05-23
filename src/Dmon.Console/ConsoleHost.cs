using System.Text.Json;
using System.Threading.Channels;
using Dmon.Console;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Spectre.Console;

namespace Dmon.Console;

/// <summary>
/// Orchestrates the console host: spawns the core process, reads events,
/// renders them, and handles user input (messages and slash commands).
/// </summary>
public sealed class ConsoleHost : IAsyncDisposable
{
    private readonly string? _corePathOverride;
    private CoreProcessManager? _processManager;
    private EventDispatcher? _eventDispatcher;
    private readonly EventRenderer _renderer = new();
    private CancellationTokenSource? _hostCts;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Turn state
    private bool _turnActive;
    private bool _turnComplete;
    private string? _lastEntryId;

    public ConsoleHost(string? corePathOverride = null)
    {
        _corePathOverride = corePathOverride;
    }

    /// <summary>
    /// Starts the core process, waits for <c>agentReady</c>, then enters the main loop.
    /// </summary>
    public async Task RunAsync(CancellationToken externalToken = default)
    {
        _hostCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        CancellationToken ct = _hostCts.Token;

        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _hostCts.Cancel();
        };

        try
        {
            // 1. Start the core process
            _processManager = new CoreProcessManager(_corePathOverride);
            await _processManager.StartAsync().ConfigureAwait(false);

            // 2. Start the event dispatcher (background JSONL reader)
            _eventDispatcher = new EventDispatcher(_processManager.StandardOutput);
            _ = _eventDispatcher.RunAsync(ct); // fire-and-forget, channel completion signals exit

            // 3. Wait for agentReady
            ChannelReader<Event> events = _eventDispatcher.Events;
            try
            {
                await foreach (Event evt in events.ReadAllAsync(ct))
                {
                    _renderer.Render(evt);
                    if (evt is AgentReadyEvent)
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 4. Main loop
            await MainLoopAsync(events, ct).ConfigureAwait(false);
        }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    private async Task MainLoopAsync(ChannelReader<Event> events, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Drain all pending events first
            bool hadConfirm = false;
            while (events.TryRead(out Event? evt))
            {
                hadConfirm = await ProcessEventAsync(evt, ct).ConfigureAwait(false);
            }

            if (hadConfirm)
                continue;

            // Core has exited?
            if (!_processManager!.IsRunning && events.Completion.IsCompleted)
            {
                AnsiConsole.MarkupLine("[grey]Core process exited.[/]");
                break;
            }

            // Prompt for user input (blocks until user presses Enter)
            string? input = PromptForInput();
            if (input is null)
                break;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            await ProcessUserInputAsync(input, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> ProcessEventAsync(Event evt, CancellationToken ct)
    {
        switch (evt)
        {
            case ToolConfirmRequestEvent confirmEvt:
                await HandleToolConfirmAsync(confirmEvt, ct).ConfigureAwait(false);
                return true; // handled a blocking prompt

            case UiInputRequestEvent inputEvt:
                await HandleUiInputAsync(inputEvt, ct).ConfigureAwait(false);
                return true; // handled a blocking prompt

            case TurnStartEvent:
                _turnActive = true;
                _turnComplete = false;
                _renderer.Render(evt);
                break;

            case TurnEndEvent:
                _turnActive = false;
                _turnComplete = true;
                _renderer.Render(evt);
                break;

            case ResponseEvent resp:
                if (!resp.Success)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]\u2717[/] Command [grey]{resp.Command}[/] failed: {Markup.Escape(resp.Error ?? "unknown")}");
                }
                break;

            case ErrorEvent error:
                _renderer.Render(error);
                if (!error.Recoverable)
                {
                    AnsiConsole.MarkupLine("[red]Unrecoverable core error — shutting down.[/]");
                    _hostCts?.Cancel();
                }
                break;

            default:
                _renderer.Render(evt);
                break;
        }

        return false;
    }

    private async Task HandleToolConfirmAsync(ToolConfirmRequestEvent evt, CancellationToken ct)
    {
        try
        {
            ToolConfirmPrompt.Result result = ToolConfirmPrompt.Show(evt);

            var response = new ToolConfirmResponseCommand
            {
                Id = evt.ConfirmId,
                Confirmed = result.Confirmed,
                Cancelled = result.Cancelled
            };

            await WriteCommandAsync(response, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If prompt fails, cancel the confirm so the core unblocks
            var cancelResponse = new ToolConfirmResponseCommand
            {
                Id = evt.ConfirmId,
                Confirmed = false,
                Cancelled = true
            };
            await WriteCommandAsync(cancelResponse, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleUiInputAsync(UiInputRequestEvent evt, CancellationToken ct)
    {
        try
        {
            UiInputPrompt.Result result = UiInputPrompt.Show(evt);

            var response = new UiInputResponseCommand
            {
                Id = evt.EventId,
                Value = result.Value,
                Cancelled = result.Cancelled
            };

            await WriteCommandAsync(response, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cancelResponse = new UiInputResponseCommand
            {
                Id = evt.EventId,
                Value = null,
                Cancelled = true
            };
            await WriteCommandAsync(cancelResponse, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessUserInputAsync(string input, CancellationToken ct)
    {
        SlashCommandParser.ParseResult parseResult = SlashCommandParser.Parse(input);

        if (!parseResult.IsSlashCommand)
        {
            if (_turnActive)
            {
                var steerCmd = new TurnSteerCommand
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Message = input
                };
                await WriteCommandAsync(steerCmd, ct).ConfigureAwait(false);
            }
            else if (_turnComplete)
            {
                _turnComplete = false;
                var followUpCmd = new TurnFollowUpCommand
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Message = input
                };
                await WriteCommandAsync(followUpCmd, ct).ConfigureAwait(false);
            }
            else
            {
                var submitCmd = new TurnSubmitCommand
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Message = input
                };
                await WriteCommandAsync(submitCmd, ct).ConfigureAwait(false);
                _lastEntryId = submitCmd.Id; // approximate — real entry ID is assigned by core
            }
            return;
        }

        // Slash command
        if (parseResult.IsExit)
        {
            if (_turnActive)
            {
                var abortCmd = new TurnAbortCommand
                {
                    Id = Guid.NewGuid().ToString("N")
                };
                await WriteCommandAsync(abortCmd, ct).ConfigureAwait(false);
            }
            _hostCts?.Cancel();
            return;
        }

        if (parseResult.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(parseResult.Error)}[/]");
            return;
        }

        if (parseResult.Command is not null)
        {
            // Special-case fork: needs last entry ID (approximate for now)
            if (parseResult.Command is SessionForkCommand forkCmd && string.IsNullOrEmpty(forkCmd.EntryId))
            {
                if (_lastEntryId is null)
                {
                    AnsiConsole.MarkupLine("[red]Cannot fork: no active session or last turn to fork from.[/]");
                    return;
                }
                forkCmd = forkCmd with { EntryId = _lastEntryId };
                await WriteCommandAsync(forkCmd, ct).ConfigureAwait(false);
            }
            else
            {
                await WriteCommandAsync(parseResult.Command, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteCommandAsync(Command cmd, CancellationToken ct)
    {
        if (_processManager?.IsRunning != true)
        {
            AnsiConsole.MarkupLine("[red]Core process is not running.[/]");
            return;
        }

        try
        {
            string json = JsonSerializer.Serialize<Command>(cmd, JsonOptions);
            await _processManager.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
            await _processManager.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Failed to write command: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static string? PromptForInput()
    {
        try
        {
            return AnsiConsole.Prompt(
                new TextPrompt<string>("> ")
                    .AllowEmpty());
        }
        catch
        {
            return null;
        }
    }

    private async Task CleanupAsync()
    {
        if (_processManager is not null)
        {
            await _processManager.StopAsync().ConfigureAwait(false);
            _processManager.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync().ConfigureAwait(false);
        _hostCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}