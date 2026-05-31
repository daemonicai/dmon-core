using System.Text.Json;
using System.Threading.Channels;
using Dcli;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Wizard;

namespace Dmon.Terminal;

internal sealed class ConsoleEventHandler
{
    private readonly TerminalRenderer _renderer;
    private readonly InputStateLayer _input;
    private readonly Func<Command, CancellationToken, Task> _sendCommand;
    private readonly CancellationTokenSource _cts;
    private readonly Action _requestReload;
    private readonly ITerminal _terminal;

    private string _rawText = string.Empty;
    private string _modelName = string.Empty;
    private string _activeProvider = string.Empty;

    // Tracks whether a wizard session is currently active.
    // Set when WizardStartCommand is sent; cleared when ProviderConfiguredEvent arrives
    // or when the wizard is cancelled.
    private bool _wizardActive;

    /// <summary>
    /// The id of the active session directory, set when the core reports a successful
    /// session.create/load/fork/clone. Used to re-open the session after /reload.
    /// </summary>
    public string? ActiveSessionId { get; private set; }

    public ConsoleEventHandler(
        TerminalRenderer renderer,
        InputStateLayer input,
        Func<Command, CancellationToken, Task> sendCommand,
        CancellationTokenSource cts,
        Action requestReload,
        ITerminal terminal)
    {
        _renderer = renderer;
        _input = input;
        _sendCommand = sendCommand;
        _cts = cts;
        _requestReload = requestReload;
        _terminal = terminal;
    }

    /// <summary>
    /// Test seam: dispatches a single dcli terminal event without blocking on the channel.
    /// </summary>
    public Task HandleUiEventAsync(TerminalEvent @event, CancellationToken cancellationToken)
    {
        switch (@event)
        {
            case InputSubmitted submitted:
                _input.OnInputSubmitted(submitted.Text);
                // Drop the submission while a turn is in progress — the state layer already
                // skipped the History append above; here we skip forwarding to core as well.
                if (_input.IsLocked)
                    return Task.CompletedTask;
                return HandleUserInputAsync(submitted.Text, cancellationToken);

            case InputChanged changed:
                _input.OnInputChanged(changed.Text);
                return Task.CompletedTask;

            case KeyPressed kp:
                if ((kp.Key.Modifiers & Modifiers.Ctrl) != Modifiers.None
                    && kp.Key.Code.Kind == KeyCode.KeyCodeKind.UnicodeScalar
                    && kp.Key.Code.RuneValue.Value == 'c')
                {
                    _cts.Cancel();
                }
                return Task.CompletedTask;

            case Resized:
            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Production drain: reads dcli terminal events from <paramref name="reader"/> and
    /// dispatches each via <see cref="HandleUiEventAsync(TerminalEvent, CancellationToken)"/>.
    /// </summary>
    public async Task DrainAsync(
        ChannelReader<TerminalEvent> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TerminalEvent ev in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await HandleUiEventAsync(ev, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _renderer.AddSystemLine($"[Drain Error] {ex.GetType().Name}: {ex.Message}");
            _cts.Cancel();
        }
    }

    public async Task HandleRpcEventAsync(Event @event, CancellationToken cancellationToken)
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
                _renderer.SettleTurn(_rawText);
                _rawText = string.Empty;
                _input.IsLocked = false;
                _renderer.SetStatus(_modelName, thinking: false);
                break;

            case ErrorEvent error:
                if (_wizardActive && error.Code == "wizard.invalidAnswer")
                {
                    // Core will re-emit a WizardStepEvent for the same step; just surface the message.
                    _renderer.AddSystemLine($"[Wizard] {error.Message}");
                }
                else
                {
                    _renderer.AddSystemLine($"[Error] {error.Message}");
                    if (!error.Recoverable)
                        _cts.Cancel();
                }
                break;

            case ResponseEvent response when !response.Success:
                string failMessage = response.Error is not null
                    ? $"[Failed] {response.Command}: {response.Error}"
                    : $"[Failed] {response.Command}";
                _renderer.AddSystemLine(failMessage);
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

            case WizardStepEvent wizardStep when _wizardActive:
                await RenderAndAnswerStepAsync(wizardStep, cancellationToken).ConfigureAwait(false);
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

            case ProviderConfiguredEvent configured when _wizardActive:
                _wizardActive = false;
                _renderer.AddSystemLine(
                    $"[Provider] Configured {configured.Adapter} / {configured.ModelId}");
                _input.IsLocked = false;
                break;

            case WizardStepEvent:
                // Wizard step arrived but no wizard is active — ignore (stale event).
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
            _terminal, confirm.Name, argsText, riskText, cancellationToken).ConfigureAwait(false);

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
        DialogResult<string> inputResult = await _terminal.InputAsync(
            new InputRequest(uiInput.Prompt, IsSecret: uiInput.Kind == UiInputKind.Secret),
            cancellationToken).ConfigureAwait(false);

        UiInputResponseCommand response = new()
        {
            Id        = uiInput.EventId,
            Value     = inputResult.Outcome == DialogOutcome.Submitted ? inputResult.Value : null,
            Cancelled = inputResult.Outcome != DialogOutcome.Submitted,
        };

        await _sendCommand(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleAddProviderAsync(CancellationToken cancellationToken)
    {
        _wizardActive = true;

        WizardStartCommand startCommand = new()
        {
            Id = Guid.NewGuid().ToString("N"),
        };

        await _sendCommand(startCommand, cancellationToken).ConfigureAwait(false);
        // Wizard steps arrive as WizardStepEvent; completion arrives as ProviderConfiguredEvent.
        // Both are handled in HandleRpcEventAsync — this method returns and the event loop drives the rest.
    }

    /// <summary>
    /// Renders one wizard step via the terminal, encodes the answer, and sends a
    /// <see cref="WizardAnswerCommand"/> back to core. Called for every <see cref="WizardStepEvent"/>
    /// while a wizard session is active, including re-emitted steps after a validation error.
    /// <see cref="WizardCompletedStep"/> is terminal — the core sends no follow-up; no answer command is sent.
    /// </summary>
    private async Task RenderAndAnswerStepAsync(WizardStepEvent evt, CancellationToken cancellationToken)
    {
        // WizardCompletedStep is a no-answer terminal step: render and return without sending a command.
        if (evt.Step is WizardCompletedStep completed)
        {
            _terminal.Scrollback.Append(new LineBuilder()
                .Fg(completed.Message, Color.Named(Color.AnsiColor.Green))
                .Build());
            // _wizardActive is cleared by the subsequent ProviderConfiguredEvent handler;
            // do not clear it here to avoid a race where ProviderConfiguredEvent arrives first.
            return;
        }

        WizardAnswerCommand answer = evt.Step switch
        {
            ChooseOneStep s  => await RenderChooseOneAsync(evt.WizardId, s, cancellationToken).ConfigureAwait(false),
            ChooseManyStep s => await RenderChooseManyAsync(evt.WizardId, s, cancellationToken).ConfigureAwait(false),
            TextInputStep s  => await RenderTextInputAsync(evt.WizardId, s, cancellationToken).ConfigureAwait(false),
            YesNoStep s      => await RenderYesNoAsync(evt.WizardId, s, cancellationToken).ConfigureAwait(false),
            InfoStep s       => RenderInfo(evt.WizardId, s),
            _ => CancelUnknownStep(evt.WizardId),
        };

        // Cancel outcome from the terminal means the user aborted; clear wizard state.
        if (answer.Outcome == WizardAnswerOutcome.Cancel)
        {
            _wizardActive = false;
            _renderer.AddSystemLine("[Add Provider] Cancelled.");
            _input.IsLocked = false;
        }

        await _sendCommand(answer, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WizardAnswerCommand> RenderChooseOneAsync(
        string wizardId, ChooseOneStep step, CancellationToken cancellationToken)
    {
        List<Line> items = step.Options
            .Select(o => Line.FromText(o.Label))
            .ToList();

        DialogResult<int> result = await _terminal.SelectAsync(
            new SelectRequest(
                Items: items,
                Title: new LineBuilder().Bold(step.Prompt).Build(),
                AllowBack: true),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == DialogOutcome.Back)
            return Answer(wizardId, WizardAnswerOutcome.Back, null);

        if (result.Outcome == DialogOutcome.Cancelled)
            return Answer(wizardId, WizardAnswerOutcome.Cancel, null);

        return Answer(wizardId, WizardAnswerOutcome.Answered, result.Value.ToString());
    }

    private async Task<WizardAnswerCommand> RenderChooseManyAsync(
        string wizardId, ChooseManyStep step, CancellationToken cancellationToken)
    {
        List<Line> items = step.Options
            .Select(o => Line.FromText(o.Label))
            .ToList();

        DialogResult<int[]> result = await _terminal.MultiSelectAsync(
            new MultiSelectRequest(
                Items: items,
                Title: new LineBuilder().Bold(step.Prompt).Build(),
                AllowBack: true),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == DialogOutcome.Back)
            return Answer(wizardId, WizardAnswerOutcome.Back, null);

        if (result.Outcome == DialogOutcome.Cancelled)
            return Answer(wizardId, WizardAnswerOutcome.Cancel, null);

        return Answer(wizardId, WizardAnswerOutcome.Answered, string.Join(",", result.Value));
    }

    private async Task<WizardAnswerCommand> RenderTextInputAsync(
        string wizardId, TextInputStep step, CancellationToken cancellationToken)
    {
        if (step.Default is not null)
        {
            string shown = step.Secret ? new string('*', 8) : step.Default;
            _terminal.Scrollback.Append(new LineBuilder()
                .Dim($"Default: {shown}")
                .Build());
        }

        DialogResult<string> result = await _terminal.InputAsync(
            new InputRequest(
                Prompt: new LineBuilder().Bold(step.Prompt).Build(),
                Default: step.Default,
                IsSecret: step.Secret,
                AllowBack: true),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == DialogOutcome.Back)
            return Answer(wizardId, WizardAnswerOutcome.Back, null);

        if (result.Outcome == DialogOutcome.Cancelled)
            return Answer(wizardId, WizardAnswerOutcome.Cancel, null);

        string value = result.Value ?? string.Empty;

        if (value.Length == 0 && step.Default is not null)
            value = step.Default;

        if (value.Length == 0 && step.Required)
            return Answer(wizardId, WizardAnswerOutcome.Cancel, null);

        return Answer(wizardId, WizardAnswerOutcome.Answered, value.Length > 0 ? value : null);
    }

    private async Task<WizardAnswerCommand> RenderYesNoAsync(
        string wizardId, YesNoStep step, CancellationToken cancellationToken)
    {
        List<Line> options = step.Default
            ? [new LineBuilder().Fg("Yes", Color.Named(Color.AnsiColor.Green)).Build(),
               new LineBuilder().Fg("No",  Color.Named(Color.AnsiColor.Red)).Build()]
            : [new LineBuilder().Fg("No",  Color.Named(Color.AnsiColor.Red)).Build(),
               new LineBuilder().Fg("Yes", Color.Named(Color.AnsiColor.Green)).Build()];

        string hint = step.Default ? "[Y/n]" : "[y/N]";

        DialogResult<int> result = await _terminal.ChoiceAsync(
            new ChoiceRequest(
                Options: options,
                Prompt: new LineBuilder().Bold($"{step.Prompt} {hint}").Build())
            { AllowBack = true },
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == DialogOutcome.Back)
            return Answer(wizardId, WizardAnswerOutcome.Back, null);

        if (result.Outcome == DialogOutcome.Cancelled)
            return Answer(wizardId, WizardAnswerOutcome.Cancel, null);

        // When default=true, index 0 = Yes; when default=false, index 0 = No.
        bool selectedYes = step.Default ? result.Value == 0 : result.Value == 1;
        return Answer(wizardId, WizardAnswerOutcome.Answered, selectedYes.ToString().ToLowerInvariant());
    }

    private WizardAnswerCommand RenderInfo(string wizardId, InfoStep step)
    {
        _terminal.Scrollback.Append(new LineBuilder()
            .Dim(step.Prompt)
            .Build());
        return Answer(wizardId, WizardAnswerOutcome.Answered, null);
    }

    private WizardAnswerCommand CancelUnknownStep(string wizardId)
    {
        // Unknown step subtype indicates a protocol desync (core is newer than this terminal build).
        _renderer.AddSystemLine("[Wizard] Unknown step type — cancelling wizard to avoid desync.");
        return Answer(wizardId, WizardAnswerOutcome.Cancel, null);
    }

    private static WizardAnswerCommand Answer(string wizardId, WizardAnswerOutcome outcome, string? value) =>
        new()
        {
            Id       = Guid.NewGuid().ToString("N"),
            WizardId = wizardId,
            Outcome  = outcome,
            Value    = value,
        };

    private async Task RunProviderPickerAsync(ModelListResultEvent evt, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> names = evt.Models.Select(m => m.Provider).Distinct().ToList();
        if (names.Count == 0)
        {
            _renderer.AddSystemLine("[Model] No providers available.");
            _input.IsLocked = false;
            return;
        }

        Line title = new LineBuilder().Bold("Select provider").Build();
        DialogResult<int> result = await _terminal.SelectAsync(
            new SelectRequest(names, title, allowBack: true),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome != DialogOutcome.Submitted)
        {
            _renderer.AddSystemLine("[Model] Cancelled.");
            _input.IsLocked = false;
            return;
        }

        string selected = names[result.Value];
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

        Line title = new LineBuilder().Bold($"Select model ({evt.Provider})").Build();
        DialogResult<int> result = await _terminal.SelectAsync(
            new SelectRequest(modelIds, title, allowBack: true),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome != DialogOutcome.Submitted)
        {
            _renderer.AddSystemLine("[Model] Cancelled.");
            _input.IsLocked = false;
            return;
        }

        ModelSetCommand cmd = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Provider = evt.Provider,
            ModelId = modelIds[result.Value]
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

        if (result.ClientCommand == SlashCommandParser.ClientCommandKind.AddProvider)
        {
            await HandleAddProviderAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.ClientCommand == SlashCommandParser.ClientCommandKind.Reload)
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
