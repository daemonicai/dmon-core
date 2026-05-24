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

    // Cached adapter list from SetupRequiredEvent — reused by /add-provider
    private IReadOnlyList<AdapterInfo>? _cachedAdapters;

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

            // 3. Wait for agentReady (may be interrupted by SetupRequiredEvent)
            bool ready = await WaitForReadyAsync(_eventDispatcher.Events, ct).ConfigureAwait(false);
            if (!ready)
                return;

            // 4. Main loop — reads the current dispatcher each iteration so a
            //    mid-session core restart (provider setup) is picked up.
            await MainLoopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for <c>agentReady</c>, handling <c>setupRequired</c> mid-way if present.
    /// Returns <c>true</c> if ready; <c>false</c> if cancelled or fatal error.
    /// </summary>
    private async Task<bool> WaitForReadyAsync(ChannelReader<Event> events, CancellationToken ct)
    {
        try
        {
            await foreach (Event evt in events.ReadAllAsync(ct))
            {
                if (evt is AgentReadyEvent)
                {
                    _renderer.Render(evt);
                    return true;
                }

                if (evt is SetupRequiredEvent setupEvt)
                {
                    _cachedAdapters = setupEvt.Adapters;

                    SetupWizard.SetupWizardResult result =
                        SetupWizard.Show(setupEvt.Adapters, isAddProvider: false);

                    var configureCmd = new ProviderConfigureCommand
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Adapter = result.Adapter,
                        ModelId = result.ModelId,
                        EnvVar = result.EnvVar,
                        Scope = result.Scope
                    };
                    await WriteCommandAsync(configureCmd, ct).ConfigureAwait(false);

                    // Wait for ProviderConfiguredEvent or ErrorEvent
                    await foreach (Event followUp in events.ReadAllAsync(ct))
                    {
                        if (followUp is ProviderConfiguredEvent)
                        {
                            AnsiConsole.MarkupLine("[green]✓[/] Provider configured. Restarting...");
                            await RestartCoreAsync(ct).ConfigureAwait(false);
                            // After restart, re-enter the ready wait on the new events channel
                            return await WaitForReadyAsync(_eventDispatcher!.Events, ct)
                                .ConfigureAwait(false);
                        }

                        if (followUp is ErrorEvent err)
                        {
                            AnsiConsole.MarkupLine($"[red]Setup failed: {Markup.Escape(err.Message)}[/]");
                            _hostCts?.Cancel();
                            return false;
                        }
                    }

                    return false;
                }

                _renderer.Render(evt);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return false;
    }

    /// <summary>
    /// Stops the current core process, disposes it, creates a fresh one, and starts it.
    /// Also replaces <see cref="_eventDispatcher"/> so the new stream is read.
    /// </summary>
    private async Task RestartCoreAsync(CancellationToken ct)
    {
        if (_processManager is not null)
        {
            await _processManager.StopAsync().ConfigureAwait(false);
            _processManager.Dispose();
        }

        _processManager = new CoreProcessManager(_corePathOverride);
        await _processManager.StartAsync().ConfigureAwait(false);

        _eventDispatcher = new EventDispatcher(_processManager.StandardOutput);
        _ = _eventDispatcher.RunAsync(ct);
    }

    private async Task MainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Re-read the dispatcher each iteration: a provider-setup restart
            // replaces it, and the old channel belongs to the exited process.
            ChannelReader<Event> events = _eventDispatcher!.Events;

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

            case ProviderConfiguredEvent:
                // Handled directly in ProcessUserInputAsync (/add-provider path); ignore here.
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

        if (parseResult.ClientCommand is AddProviderCommand)
        {
            await HandleAddProviderAsync(ct).ConfigureAwait(false);
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

    private async Task HandleAddProviderAsync(CancellationToken ct)
    {
        if (_cachedAdapters is null)
        {
            AnsiConsole.MarkupLine("[yellow]Provider list not available.[/]");
            return;
        }

        SetupWizard.SetupWizardResult result =
            SetupWizard.Show(_cachedAdapters, isAddProvider: true);

        var configureCmd = new ProviderConfigureCommand
        {
            Id = Guid.NewGuid().ToString("N"),
            Adapter = result.Adapter,
            ModelId = result.ModelId,
            EnvVar = result.EnvVar,
            Scope = result.Scope
        };
        await WriteCommandAsync(configureCmd, ct).ConfigureAwait(false);

        // Wait for ProviderConfiguredEvent or ErrorEvent
        ChannelReader<Event> events = _eventDispatcher!.Events;
        await foreach (Event evt in events.ReadAllAsync(ct))
        {
            if (evt is ProviderConfiguredEvent)
            {
                AnsiConsole.MarkupLine("[green]✓[/] Provider added. Restarting...");
                await RestartCoreAsync(ct).ConfigureAwait(false);
                bool ready = await WaitForReadyAsync(_eventDispatcher.Events, ct)
                    .ConfigureAwait(false);
                if (!ready)
                    _hostCts?.Cancel();
                return;
            }

            if (evt is ErrorEvent err)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(err.Message)}[/]");
                return;
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