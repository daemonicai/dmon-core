using System.Text.Json;
using Dmon.Abstractions.Providers;
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
    private readonly IReadOnlyList<IProviderFactory> _providerFactories;
    private readonly Action _requestReload;

    private string _rawText = string.Empty;
    private string _modelName = string.Empty;
    private string _activeProvider = string.Empty;

    /// <summary>
    /// The id of the active session directory, set when the core reports a successful
    /// session.create/load/fork/clone. Used to re-open the session after /reload.
    /// </summary>
    public string? ActiveSessionId { get; private set; }

    public ConsoleEventHandler(
        TerminalRenderer renderer,
        InputReader input,
        Func<Command, CancellationToken, Task> sendCommand,
        CancellationTokenSource cts,
        IReadOnlyList<IProviderFactory> providerFactories,
        Action requestReload)
    {
        _renderer = renderer;
        _input = input;
        _sendCommand = sendCommand;
        _cts = cts;
        _providerFactories = providerFactories;
        _requestReload = requestReload;
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
                _renderer.PrintPrompt();
                break;

            case BootstrapNoticeEvent bootstrap:
                _renderer.AddSystemLine($"[Bootstrap] Session: {bootstrap.Path}");
                break;

            case ProviderSwitchedEvent switched:
                _modelName = switched.Model;
                _activeProvider = switched.Name;
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

            case ModelListResultEvent listResult:
                await RunProviderPickerAsync(listResult, cancellationToken).ConfigureAwait(false);
                break;

            case ModelModelsResultEvent modelsResult:
                await RunModelPickerAsync(modelsResult, cancellationToken).ConfigureAwait(false);
                break;

            case ResponseEvent sessionResponse when sessionResponse.Success
                && sessionResponse.Command is "session.create" or "session.load" or "session.fork" or "session.clone":
                TrackActiveSession(sessionResponse);
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
            // ExtensionUnloadedEvent: intentionally silent — tools are deregistered (no longer
            // offered to the LLM) but the assembly remains resident until the core restarts.
            case ExtensionUnloadedEvent:
            case ProviderConfiguredEvent:
            case AuthLoginCompleteEvent:
            case AuthLogoutCompleteEvent:
            case AuthLoginFailedEvent:
            case AuthStatusResultEvent:
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
        WizardEngine engine = new(_providerFactories);
        WizardResult? result = await engine.RunAsync(cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            _renderer.AddSystemLine("[Add Provider] Cancelled.");
            _input.IsLocked = false;
            return;
        }

        ProviderConfigureCommand command = new()
        {
            Id      = Guid.NewGuid().ToString("N"),
            Adapter = result.Adapter,
            ModelId = result.ModelId,
            EnvVar  = result.EnvVar,
            Scope   = "global",
        };

        await _sendCommand(command, cancellationToken).ConfigureAwait(false);
        _renderer.AddSystemLine("[Add Provider] Configuring provider, waiting for agent ready…");
        _input.IsLocked = false;
    }

    private async Task RunProviderPickerAsync(ModelListResultEvent evt, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> names = evt.Models.Select(m => m.Provider).Distinct().ToList();
        if (names.Count == 0)
        {
            _renderer.AddSystemLine("[Model] No providers available.");
            _input.IsLocked = false;
            return;
        }

        int preSelect = names
            .Select((name, i) => (name, i))
            .Where(t => string.Equals(t.name, evt.ActiveProvider, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.i)
            .FirstOrDefault();

        string? selected = await Task.Run(() => ConsolePicker.Run(names, preSelect), cancellationToken)
            .ConfigureAwait(false);

        if (selected is null)
        {
            _renderer.AddSystemLine("[Model] Cancelled.");
            _input.IsLocked = false;
            return;
        }

        _renderer.AddSystemLine($"[Model] Fetching models for {selected}…");
        ModelModelsCommand cmd = new() { Id = Guid.NewGuid().ToString("N"), Provider = selected };
        await _sendCommand(cmd, cancellationToken).ConfigureAwait(false);
        // Stay locked — waiting for ModelModelsResultEvent
    }

    private async Task RunModelPickerAsync(ModelModelsResultEvent evt, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> modelIds = evt.Models;

        if (modelIds.Count == 0)
        {
            _renderer.AddSystemLine($"[Model] No models available for {evt.Provider}.");
            _input.IsLocked = false;
            return;
        }

        int preSelect = 0;
        if (string.Equals(evt.Provider, _activeProvider, StringComparison.OrdinalIgnoreCase)
            && evt.ActiveModelId is not null)
        {
            int idx = modelIds.ToList().IndexOf(evt.ActiveModelId);
            if (idx >= 0) preSelect = idx;
        }

        string? selected = await Task.Run(() => ConsolePicker.Run(modelIds, preSelect), cancellationToken)
            .ConfigureAwait(false);

        if (selected is null)
        {
            _renderer.AddSystemLine("[Model] Cancelled.");
            _input.IsLocked = false;
            return;
        }

        ModelSetCommand cmd = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Provider = evt.Provider,
            ModelId = selected
        };
        await _sendCommand(cmd, cancellationToken).ConfigureAwait(false);
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

        if (result.ClientCommand is ReloadCommand)
        {
            if (_input.IsLocked)
            {
                _renderer.AddSystemLine("[Reload] Ignored — a turn is in progress.");
                return;
            }
            _renderer.AddSystemLine("[Reload] Restarting core…");
            _requestReload();
            return;
        }

        if (result.Command is ModelListCommand modelListCommand)
        {
            _input.IsLocked = true;
            await _sendCommand(modelListCommand, cancellationToken).ConfigureAwait(false);
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

    private void TrackActiveSession(ResponseEvent response)
    {
        if (response.Data is not JsonElement element) return;
        if (!element.TryGetProperty("id", out JsonElement idProp)) return;
        string? id = idProp.GetString();
        if (!string.IsNullOrEmpty(id))
            ActiveSessionId = id;
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
