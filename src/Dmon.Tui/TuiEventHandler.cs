using System.Text.Json;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Terminal.Gui.App;

namespace Dmon.Tui;

/// <summary>
/// Routes events received from the agent core to the appropriate <see cref="DmonWindow"/> mutations.
/// All UI mutations are marshalled onto the Terminal.Gui main loop via
/// <c>IApplication.Invoke</c>; this class is safe to call from a background task.
/// </summary>
internal sealed class TuiEventHandler
{
    private readonly DmonWindow _window;
    private readonly IApplication _app;

    private readonly Func<object, CancellationToken, Task> _sendCommand;

    public TuiEventHandler(
        DmonWindow window,
        IApplication app,
        Func<object, CancellationToken, Task> sendCommand)
    {
        _window = window;
        _app = app;
        _sendCommand = sendCommand;
    }

    public async Task HandleAsync(Event @event, CancellationToken cancellationToken)
    {
        switch (@event)
        {
            case TurnStartEvent:
                _app.Invoke(() =>
                {
                    _window.ChatOutput.BeginAssistantTurn();
                    _window.LockInput();
                    _window.SetThinking(true);
                });
                break;

            case MessageDeltaEvent delta:
                string? text = ExtractDeltaText(delta.Delta);
                if (text is not null)
                {
                    _app.Invoke(() =>
                    {
                        _window.ChatOutput.AppendToken(text);
                    });
                }
                break;

            case TurnEndEvent:
                _app.Invoke(() =>
                {
                    _window.ChatOutput.SettleTurn();
                    _window.UnlockInput();
                    _window.SetThinking(false);
                });
                break;

            case ErrorEvent error:
                _app.Invoke(() =>
                {
                    _window.ChatOutput.AddSystemTurn($"[Error] {error.Message}");
                    if (!error.Recoverable)
                        _app.RequestStop();
                });
                break;

            case ResponseEvent response when !response.Success:
                _app.Invoke(() =>
                {
                    string message = response.Error is not null
                        ? $"[Failed] {response.Command}: {response.Error}"
                        : $"[Failed] {response.Command}";
                    _window.ChatOutput.AddSystemTurn(message);
                });
                break;

            case AgentReadyEvent ready:
                _app.Invoke(() =>
                {
                    _window.ChatOutput.AddSystemTurn(
                        $"[Ready] dmon core v{ready.CoreVersion} (protocol {ready.ProtocolVersion})");
                });
                break;

            case BootstrapNoticeEvent bootstrap:
                _app.Invoke(() =>
                {
                    _window.ChatOutput.AddSystemTurn($"[Bootstrap] Session: {bootstrap.Path}");
                });
                break;

            case ProviderSwitchedEvent switched:
                _app.Invoke(() =>
                {
                    _window.SetModel(switched.Model);
                    _window.ChatOutput.AddSystemTurn($"[Provider] Switched to {switched.Name} / {switched.Model}");
                });
                break;

            case RetryAttemptEvent retry:
                _app.Invoke(() =>
                {
                    _window.ChatOutput.AddSystemTurn(
                        $"[Retry] Attempt {retry.Attempt}/{retry.MaxAttempts} — {retry.Reason}");
                });
                break;

            case ExtensionErrorEvent extError:
                _app.Invoke(() =>
                {
                    _window.ChatOutput.AddSystemTurn($"[Extension Error] {extError.Source} ({extError.Phase})");
                });
                break;

            case SystemNoticeEvent notice:
                _app.Invoke(() =>
                {
                    _window.ChatOutput.AddSystemTurn($"[Notice] {notice.Message}");
                });
                break;

            case SessionUpdatedEvent session:
                _app.Invoke(() =>
                {
                    _window.ChatOutput.AddSystemTurn($"[Session] {session.Title}");
                });
                break;

            case ToolConfirmRequestEvent confirm:
                await HandleToolConfirmAsync(confirm, cancellationToken).ConfigureAwait(false);
                break;

            case UiInputRequestEvent uiInput:
                await HandleUiInputAsync(uiInput, cancellationToken).ConfigureAwait(false);
                break;

            case CompactionStartEvent:
            case CompactionEndEvent:
            case MessageStartEvent:
            case MessageEndEvent:
            case AgentStartEvent:
            case AgentEndEvent:
            case ToolExecutionStartEvent:
            case ToolExecutionEndEvent:
            case ExtensionLoadedEvent:
            case ExtensionUnloadedEvent:
            case SetupRequiredEvent:
            case ProviderConfiguredEvent:
            case AuthLoginCompleteEvent:
            case AuthLogoutCompleteEvent:
            case AuthLoginFailedEvent:
            case AuthStatusResultEvent:
            case ModelListResultEvent:
            case CapabilityIgnoredEvent:
            case ResponseEvent: // success responses — not surfaced in the TUI
            default:
                break;
        }
    }

    private async Task HandleToolConfirmAsync(ToolConfirmRequestEvent confirm, CancellationToken cancellationToken)
    {
        string argsText = confirm.Args?.ToString() ?? string.Empty;
        string riskText = confirm.Risk.ToString();

        TaskCompletionSource<ToolPermission?> bridgeTcs = new();
        _app.Invoke(() =>
        {
            _ = BridgeAsync(
                ToolConfirmDialog.ShowAsync(_app, confirm.Name, argsText, riskText, cancellationToken),
                bridgeTcs);
        });
        ToolPermission? permission = await bridgeTcs.Task.ConfigureAwait(false);

        string? scope = permission switch
        {
            ToolPermission.Once    => "once",
            ToolPermission.Project => "project",
            ToolPermission.Global  => "global",
            _                      => null,
        };

        ToolConfirmResponseCommand response = new()
        {
            Id = confirm.ConfirmId,
            Confirmed = permission is not null,
            Cancelled = false,
            Scope = scope,
        };

        await _sendCommand(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the add-provider wizard and, on completion, sends a
    /// <see cref="ProviderConfigureCommand"/> to the agent core. If the wizard is
    /// cancelled, logs a notice to the chat output.
    /// </summary>
    public async Task HandleAddProviderAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<WizardState?> bridgeTcs = new();
        _app.Invoke(() =>
        {
            _ = BridgeAsync(
                _window.RunSetupWizardAsync(_app, cancellationToken),
                bridgeTcs);
        });
        WizardState? result = await bridgeTcs.Task.ConfigureAwait(false);

        if (result is null)
        {
            _app.Invoke(() =>
            {
                _window.ChatOutput.AddSystemTurn("[Add Provider] Cancelled.");
            });
            return;
        }

        ProviderConfigureCommand command = new()
        {
            Id       = Guid.NewGuid().ToString("N"),
            Adapter  = result.Adapter  ?? string.Empty,
            ModelId  = result.ModelId  ?? string.Empty,
            EnvVar   = result.EnvVar   ?? string.Empty,
            Scope    = result.Scope    ?? "local",
        };

        await _sendCommand(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleUiInputAsync(UiInputRequestEvent uiInput, CancellationToken cancellationToken)
    {
        string kind = uiInput.Kind.ToString();

        TaskCompletionSource<string?> bridgeTcs = new();
        _app.Invoke(() =>
        {
            _ = BridgeAsync(
                UiInputDialog.ShowAsync(_app, uiInput.Prompt, kind, cancellationToken),
                bridgeTcs);
        });
        string? value = await bridgeTcs.Task.ConfigureAwait(false);

        UiInputResponseCommand response = new()
        {
            Id = uiInput.EventId,
            Value = value,
            Cancelled = value is null,
        };

        await _sendCommand(response, cancellationToken).ConfigureAwait(false);
    }

    // Awaits a dialog task started on the UI thread and forwards its outcome to the
    // background-thread awaiter via the bridge. All exceptions and cancellation are
    // captured into the bridge so the awaiter never hangs and nothing escapes as async void.
    private static async Task BridgeAsync<T>(Task<T> dialogTask, TaskCompletionSource<T> bridge)
    {
        try
        {
            bridge.TrySetResult(await dialogTask.ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            bridge.TrySetCanceled();
        }
        catch (Exception ex)
        {
            bridge.TrySetException(ex);
        }
    }

    // Extracts the text delta from the MessageDeltaEvent.Delta object.
    // At runtime, Delta is a JsonElement. We look for "type" == "textDelta" and a "delta" string.
    private static string? ExtractDeltaText(object delta)
    {
        if (delta is not JsonElement element)
            return null;

        if (!element.TryGetProperty("type", out JsonElement typeProp))
            return null;

        if (typeProp.GetString() is not "textDelta")
            return null;

        if (!element.TryGetProperty("delta", out JsonElement deltaProp))
            return null;

        return deltaProp.GetString();
    }
}
