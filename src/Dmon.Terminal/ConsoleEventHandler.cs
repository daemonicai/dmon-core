using System.Text.Json;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;

namespace Dmon.Terminal;

internal sealed class ConsoleEventHandler
{
    private readonly TerminalRenderer _renderer;
    private readonly InputReader _input;
    private readonly Func<Command, CancellationToken, Task> _sendCommand;
    private readonly CancellationTokenSource _cts;

    private string _rawText = string.Empty;
    private string _modelName = string.Empty;

    public ConsoleEventHandler(
        TerminalRenderer renderer,
        InputReader input,
        Func<Command, CancellationToken, Task> sendCommand,
        CancellationTokenSource cts)
    {
        _renderer = renderer;
        _input = input;
        _sendCommand = sendCommand;
        _cts = cts;
    }

    public async Task HandleAsync(Event @event, CancellationToken cancellationToken)
    {
        switch (@event)
        {
            case TurnStartEvent:
                _rawText = string.Empty;
                _input.IsLocked = true;
                _renderer.SetStatus(_modelName, thinking: true);
                _renderer.PrintSeparator("Thinking…");
                break;

            case MessageDeltaEvent delta:
                string? text = ExtractDeltaText(delta.Delta);
                if (text is not null)
                {
                    _rawText += text;
                    _renderer.AppendToken(text);
                }
                break;

            case TurnEndEvent:
                string spectreMarkup = MarkdownRenderer.Render(_rawText);
                _renderer.SettleTurn(spectreMarkup);
                _rawText = string.Empty;
                _input.IsLocked = false;
                _renderer.SetStatus(_modelName, thinking: false);
                _renderer.PrintSeparator();
                _renderer.PrintPrompt();
                break;

            case ErrorEvent error:
                _renderer.AddSystemLine($"[Error] {error.Message}");
                if (!error.Recoverable)
                    _cts.Cancel();
                break;

            case ResponseEvent response when !response.Success:
                string failMessage = response.Error is not null
                    ? $"[Failed] {response.Command}: {response.Error}"
                    : $"[Failed] {response.Command}";
                _renderer.AddSystemLine(failMessage);
                break;

            case AgentReadyEvent ready:
                _renderer.AddSystemLine(
                    $"[Ready] dmon core v{ready.CoreVersion} (protocol {ready.ProtocolVersion})");
                break;

            case BootstrapNoticeEvent bootstrap:
                _renderer.AddSystemLine($"[Bootstrap] Session: {bootstrap.Path}");
                break;

            case ProviderSwitchedEvent switched:
                _modelName = switched.Model;
                _renderer.SetStatus(_modelName, thinking: false);
                _renderer.AddSystemLine($"[Provider] Switched to {switched.Name} / {switched.Model}");
                break;

            case RetryAttemptEvent retry:
                _renderer.AddSystemLine(
                    $"[Retry] Attempt {retry.Attempt}/{retry.MaxAttempts} — {retry.Reason}");
                break;

            case ExtensionErrorEvent extError:
                _renderer.AddSystemLine($"[Extension Error] {extError.Source} ({extError.Phase})");
                break;

            case SystemNoticeEvent notice:
                _renderer.AddSystemLine($"[Notice] {notice.Message}");
                break;

            case SessionUpdatedEvent session:
                _renderer.AddSystemLine($"[Session] {session.Title}");
                break;

            case ToolConfirmRequestEvent confirm:
                await HandleToolConfirmAsync(confirm, cancellationToken).ConfigureAwait(false);
                break;

            case UiInputRequestEvent uiInput:
                await HandleUiInputAsync(uiInput, cancellationToken).ConfigureAwait(false);
                break;

            case SetupRequiredEvent:
                _input.IsLocked = true;
                _renderer.AddSystemLine(
                    "[Setup Required] No provider configured. Starting setup wizard…");
                await HandleAddProviderAsync(cancellationToken).ConfigureAwait(false);
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
            case ProviderConfiguredEvent:
            case AuthLoginCompleteEvent:
            case AuthLogoutCompleteEvent:
            case AuthLoginFailedEvent:
            case AuthStatusResultEvent:
            case ModelListResultEvent:
            case CapabilityIgnoredEvent:
            case ResponseEvent:
            default:
                break;
        }
    }

    private async Task HandleToolConfirmAsync(ToolConfirmRequestEvent confirm, CancellationToken cancellationToken)
    {
        string argsText = confirm.Args?.ToString() ?? string.Empty;
        string riskText = confirm.Risk.ToString();

        ToolPermission? permission = await ToolConfirmPrompt.ShowAsync(
            confirm.Name, argsText, riskText, cancellationToken).ConfigureAwait(false);

        string? scope = permission switch
        {
            ToolPermission.Once    => "once",
            ToolPermission.Project => "project",
            ToolPermission.Global  => "global",
            _                      => null,
        };

        ToolConfirmResponseCommand response = new()
        {
            Id        = confirm.ConfirmId,
            Confirmed = permission is not null,
            Cancelled = false,
            Scope     = scope,
        };

        await _sendCommand(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleUiInputAsync(UiInputRequestEvent uiInput, CancellationToken cancellationToken)
    {
        string? value = await InlinePrompt.ReadLineAsync(
            uiInput.Prompt, secret: false, cancellationToken).ConfigureAwait(false);

        UiInputResponseCommand response = new()
        {
            Id        = uiInput.EventId,
            Value     = value,
            Cancelled = value is null,
        };

        await _sendCommand(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleAddProviderAsync(CancellationToken cancellationToken)
    {
        WizardState? result = await WizardRunner.RunAsync(
            [
                AdapterSelectionStep.Create(cancellationToken),
                ModelSelectionStep.Create(cancellationToken),
                AuthConfigStep.Create(cancellationToken),
            ],
            cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            _renderer.AddSystemLine("[Add Provider] Cancelled.");
            _input.IsLocked = false;
            return;
        }

        ProviderConfigureCommand command = new()
        {
            Id      = Guid.NewGuid().ToString("N"),
            Adapter = result.Adapter ?? string.Empty,
            ModelId = result.ModelId ?? string.Empty,
            EnvVar  = result.EnvVar  ?? string.Empty,
            Scope   = result.Scope   ?? "local",
        };

        await _sendCommand(command, cancellationToken).ConfigureAwait(false);
        _renderer.AddSystemLine("[Add Provider] Configuring provider, waiting for agent ready…");
        _input.IsLocked = false;
    }

    public async Task HandleUserInputAsync(string input, CancellationToken cancellationToken)
    {
        SlashCommandParser.ParseResult result = SlashCommandParser.Parse(input);

        if (result.IsExit)
        {
            _cts.Cancel();
            return;
        }

        if (result.Error is not null)
        {
            _renderer.AddSystemLine($"[Error] {result.Error}");
            return;
        }

        if (result.ClientCommand is AddProviderCommand)
        {
            await HandleAddProviderAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.Command is not null)
        {
            await _sendCommand(result.Command, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Plain message
        _renderer.AddUserLine(input);
        TurnSubmitCommand submitCommand = new()
        {
            Id      = Guid.NewGuid().ToString("N"),
            Message = input,
        };
        await _sendCommand(submitCommand, cancellationToken).ConfigureAwait(false);
    }

    private static string? ExtractDeltaText(object delta)
    {
        if (delta is not JsonElement element) return null;
        if (!element.TryGetProperty("type", out JsonElement typeProp)) return null;
        if (typeProp.GetString() != "textDelta") return null;
        if (!element.TryGetProperty("delta", out JsonElement deltaProp)) return null;
        return deltaProp.GetString();
    }
}
