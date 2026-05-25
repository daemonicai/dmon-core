using System.Text.Json;
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

    // sendCommand is reserved for Group 6 (tool confirm and UI input responses).
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

    public Task HandleAsync(Event @event, CancellationToken cancellationToken)
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
            case UiInputRequestEvent:
            case ToolConfirmRequestEvent:
            case CapabilityIgnoredEvent:
            case ResponseEvent:
                // Silently ignored at this stage; handled in later groups or not surfaced.
                break;
        }

        return Task.CompletedTask;
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
