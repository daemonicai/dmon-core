using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dcli;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Models;
using Dmon.Terminal.Tests.Fakes;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Tier-A tests for the <see cref="ConsoleEventHandler"/> dcli-event seam introduced in Phase 3.
/// All tests use <see cref="FakeTerminal"/> — no real console, no wall-clock waits.
/// </summary>
public sealed class ConsoleEventHandlerTests
{
    // ── helpers ────────────────────────────────────────────────────────────

    private static string LineText(Line l) =>
        string.Concat(l.Segments.Select(s => s.Text));

    private static ConsoleEventHandler BuildHandler(
        FakeTerminal fake,
        List<Command> sentCommands,
        CancellationTokenSource cts,
        Action? requestReload = null,
        InputStateLayer? inputLayer = null,
        string? coreVersion = null)
    {
        Func<Command, CancellationToken, Task> send = (cmd, _) =>
        {
            sentCommands.Add(cmd);
            return Task.CompletedTask;
        };

        TerminalRenderer renderer = new(fake);
        // Prime the readiness state so the status band renders when coreVersion is supplied.
        if (coreVersion is not null)
            renderer.SetReadiness(coreVersion);
        InputStateLayer input = inputLayer ?? new();
        return new ConsoleEventHandler(
            renderer,
            input,
            send,
            cts,
            requestReload ?? (() => { }),
            fake);
    }

    // ── HandleUiEventAsync — InputSubmitted ─────────────────────────────────

    [Fact]
    public async Task HandleAsync_InputSubmitted_ForwardsPlainMessageAsTurnSubmit()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        await handler.HandleUiEventAsync(new InputSubmitted("hello world"), CancellationToken.None);

        TurnSubmitCommand cmd = Assert.Single(sentCommands.OfType<TurnSubmitCommand>());
        Assert.Equal("hello world", cmd.Message);
    }

    [Fact]
    public async Task HandleAsync_InputSubmitted_SlashCommand_DispatchesToCore()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        await handler.HandleUiEventAsync(new InputSubmitted("/new"), CancellationToken.None);

        Assert.Single(sentCommands.OfType<SessionCreateCommand>());
    }

    [Fact]
    public async Task HandleAsync_InputSubmitted_SlashCommandClientReload_TriggersReload()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        bool reloadCalled = false;

        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts, requestReload: () => reloadCalled = true);

        await handler.HandleUiEventAsync(new InputSubmitted("/reload"), CancellationToken.None);

        Assert.True(reloadCalled);
        Assert.Empty(sentCommands);
    }

    [Fact]
    public async Task HandleAsync_InputSubmitted_SlashCommandExit_CancelsCts()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        await handler.HandleUiEventAsync(new InputSubmitted("/quit"), CancellationToken.None);

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task HandleAsync_InputSubmitted_UnknownSlashCommand_AppendsErrorToScrollback()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        await handler.HandleUiEventAsync(new InputSubmitted("/bogus"), CancellationToken.None);

        IEnumerable<string> lines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));

        Assert.Contains(lines, l => l.Contains("Unknown command") && l.Contains("bogus"));
        Assert.Empty(sentCommands);
    }

    // ── HandleUiEventAsync — no-op events ───────────────────────────────────

    [Fact]
    public async Task HandleAsync_InputChanged_MirrorsCurrentBuffer()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        InputStateLayer layer = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts, inputLayer: layer);

        int callsBefore = fake.Calls.Count;
        await handler.HandleUiEventAsync(new InputChanged("partial"), CancellationToken.None);

        // No dcli calls — InputChanged does not cause any terminal interaction.
        Assert.Equal(callsBefore, fake.Calls.Count);
        Assert.Empty(sentCommands);
        // State layer mirrors the editor text.
        Assert.Equal("partial", layer.CurrentBuffer);
    }

    [Fact]
    public async Task HandleAsync_Resized_NoOp()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        int callsBefore = fake.Calls.Count;
        await handler.HandleUiEventAsync(new Resized(120, 40), CancellationToken.None);

        Assert.Equal(callsBefore, fake.Calls.Count);
        Assert.Empty(sentCommands);
    }

    // ── HandleUiEventAsync — Ctrl+C ─────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_KeyPressedCtrlC_CancelsCts()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        KeyEvent key = new(KeyCode.FromRune(new Rune('c')), Modifiers.Ctrl);
        await handler.HandleUiEventAsync(new KeyPressed(key), CancellationToken.None);

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task HandleAsync_KeyPressedCtrlC_CancelsEvenWhenInputLocked()
    {
        // Spec scenario "Ctrl+C during streaming": input is locked while a turn is active,
        // but Ctrl+C must still cancel.
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        // Drive IsLocked=true via the same path production uses.
        await handler.HandleRpcEventAsync((Event)new TurnStartEvent(), CancellationToken.None);

        KeyEvent key = new(KeyCode.FromRune(new Rune('c')), Modifiers.Ctrl);
        await handler.HandleUiEventAsync(new KeyPressed(key), CancellationToken.None);

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task HandleAsync_KeyPressedCtrlOther_DoesNotCancel()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        int callsBefore = fake.Calls.Count;
        KeyEvent key = new(KeyCode.FromRune(new Rune('d')), Modifiers.Ctrl);
        await handler.HandleUiEventAsync(new KeyPressed(key), CancellationToken.None);

        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(callsBefore, fake.Calls.Count);
        Assert.Empty(sentCommands);
    }

    [Fact]
    public async Task HandleAsync_KeyPressedCwithoutCtrl_DoesNotCancel()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        int callsBefore = fake.Calls.Count;
        KeyEvent key = new(KeyCode.FromRune(new Rune('c')), Modifiers.None);
        await handler.HandleUiEventAsync(new KeyPressed(key), CancellationToken.None);

        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(callsBefore, fake.Calls.Count);
        Assert.Empty(sentCommands);
    }

    [Fact]
    public async Task HandleAsync_KeyPressedNamedKey_DoesNotCancel()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        // Ctrl+Enter — named key, not a UnicodeScalar 'c'
        int callsBefore = fake.Calls.Count;
        KeyEvent key = new(KeyCode.Named(NamedKey.Enter), Modifiers.Ctrl);
        await handler.HandleUiEventAsync(new KeyPressed(key), CancellationToken.None);

        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(callsBefore, fake.Calls.Count);
        Assert.Empty(sentCommands);
    }

    // ── HandleRpcEventAsync — provider picker (RunProviderPickerAsync) ───────

    [Fact]
    public async Task HandleAsync_ModelListResult_OpensProviderSelectAsyncWithAllowBack()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();

        // Script: select the second item (index 1 = "openai")
        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 1));

        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        ModelListResultEvent evt = new()
        {
            CommandId = "cmd-list-1",
            Models =
            [
                new Model { Id = "claude-3", Name = "Claude 3", Provider = "anthropic", Input = [InputType.Text] },
                new Model { Id = "gpt-4o",   Name = "GPT-4o",   Provider = "openai",    Input = [InputType.Text] },
            ],
            ActiveProvider = "openai",
            ActiveModelId  = "gpt-4o",
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        SelectOpened call = Assert.Single(fake.Calls.OfType<SelectOpened>());
        Assert.Equal(2, call.Request.Items.Count);
        Assert.Contains(call.Request.Items, l => LineText(l) == "anthropic");
        Assert.Contains(call.Request.Items, l => LineText(l) == "openai");
        Assert.True(call.Request.AllowBack);

        ModelModelsCommand cmd = Assert.Single(sentCommands.OfType<ModelModelsCommand>());
        Assert.Equal("openai", cmd.Provider);
    }

    [Fact]
    public async Task HandleAsync_ModelListResult_Cancelled_NoCommand()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();

        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default));

        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        ModelListResultEvent evt = new()
        {
            CommandId = "cmd-list-2",
            Models = [new Model { Id = "claude-3", Name = "Claude 3", Provider = "anthropic", Input = [InputType.Text] }],
            ActiveProvider = "anthropic",
            ActiveModelId  = "claude-3",
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.Empty(sentCommands.OfType<ModelModelsCommand>());

        IEnumerable<string> lines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));
        Assert.Contains(lines, l => l.Contains("[Model]") && l.Contains("Cancelled"));
    }

    [Fact]
    public async Task HandleAsync_ModelListResult_Empty_NoSelect()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        ModelListResultEvent evt = new()
        {
            CommandId = "cmd-list-3",
            Models         = [],
            ActiveProvider = string.Empty,
            ActiveModelId  = string.Empty,
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.Empty(fake.Calls.OfType<SelectOpened>());
        Assert.Empty(sentCommands);

        IEnumerable<string> lines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));
        Assert.Contains(lines, l => l.Contains("No providers available"));
    }

    // ── HandleRpcEventAsync — model picker (RunModelPickerAsync) ────────────

    [Fact]
    public async Task HandleAsync_ModelModelsResult_OpensModelSelectAsync()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();

        // Script: select index 1 = "gpt-4o-mini"
        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 1));

        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        ModelModelsResultEvent evt = new()
        {
            CommandId = "cmd-models-1",
            Provider      = "openai",
            Models        = ["gpt-4o", "gpt-4o-mini"],
            ActiveModelId = "gpt-4o",
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.Single(fake.Calls.OfType<SelectOpened>());

        ModelSetCommand cmd = Assert.Single(sentCommands.OfType<ModelSetCommand>());
        Assert.Equal("openai",      cmd.Provider);
        Assert.Equal("gpt-4o-mini", cmd.ModelId);
    }

    // ── HandleRpcEventAsync — UiInputRequest (deferred from Phase 2) ─────────

    [Fact]
    public async Task HandleAsync_UiInputRequest_ForwardsValueOnSubmit()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();

        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Submitted, "abc"));

        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        UiInputRequestEvent evt = new()
        {
            EventId = "id-1",
            Prompt  = "Enter token",
            Kind    = UiInputKind.Text,
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        InputOpened call = Assert.Single(fake.Calls.OfType<InputOpened>());
        string promptText = string.Concat(call.Request.Prompt?.Select(LineText) ?? []);
        Assert.Contains("Enter token", promptText);
        Assert.False(call.Request.IsSecret);

        UiInputResponseCommand cmd = Assert.Single(sentCommands.OfType<UiInputResponseCommand>());
        Assert.Equal("id-1", cmd.Id);
        Assert.Equal("abc",  cmd.Value);
        Assert.False(cmd.Cancelled);
    }

    [Fact]
    public async Task HandleAsync_UiInputRequest_SecretKind_OpensSecretInput()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();

        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Submitted, "secret"));

        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        UiInputRequestEvent evt = new()
        {
            EventId = "id-2",
            Prompt  = "Enter password",
            Kind    = UiInputKind.Secret,
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        InputOpened call = Assert.Single(fake.Calls.OfType<InputOpened>());
        Assert.True(call.Request.IsSecret);
    }

    [Fact]
    public async Task HandleAsync_UiInputRequest_CancelledOutcome_SendsCancelled()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();

        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Cancelled, string.Empty));

        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        UiInputRequestEvent evt = new()
        {
            EventId = "id-3",
            Prompt  = "Enter token",
            Kind    = UiInputKind.Text,
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        UiInputResponseCommand cmd = Assert.Single(sentCommands.OfType<UiInputResponseCommand>());
        Assert.True(cmd.Cancelled);
        Assert.Null(cmd.Value);
    }

    // ── HandleRpcEventAsync — ToolConfirmRequest smoke test ─────────────────

    [Fact]
    public async Task HandleAsync_ToolConfirmRequest_OpensChoiceAndForwardsResponse()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();

        // Allow once = index 0 in ToolConfirmPrompt
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));

        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        ToolConfirmRequestEvent evt = new()
        {
            ConfirmId = "c1",
            Name      = "fs.write",
            Args      = new object(),
            Risk      = RiskLevel.Low,
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.Single(fake.Calls.OfType<ChoiceOpened>());

        ToolConfirmResponseCommand cmd = Assert.Single(sentCommands.OfType<ToolConfirmResponseCommand>());
        Assert.Equal("c1",   cmd.Id);
        Assert.True(cmd.Confirmed);
        Assert.Equal("once", cmd.Scope);
    }

    // ── HandleRpcEventAsync — turn lifecycle smoke test ─────────────────────

    [Fact]
    public async Task HandleAsync_TurnStartThenEnd_UpdatesStatusAndCommitsLiveBlock()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        // Supply a core version so the status band renders (guard keys on _coreVersion).
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts, coreVersion: "1.0.0");

        // Establish model name so the renderer produces a non-empty status row.
        ProviderSwitchedEvent providerSwitched = new()
        {
            Name  = "anthropic",
            Model = "claude-3-7-sonnet",
        };
        await handler.HandleRpcEventAsync((Event)providerSwitched, CancellationToken.None);

        // TurnStart — status should get thinking=true
        await handler.HandleRpcEventAsync((Event)new TurnStartEvent(), CancellationToken.None);

        StatusSet? afterStart = fake.Calls.OfType<StatusSet>().LastOrDefault();
        Assert.NotNull(afterStart);
        // Status band is two rows: rule row + readiness row.
        Assert.NotEmpty(afterStart.Rows);
        string statusText = string.Concat(afterStart.Rows.SelectMany(r => r.Segments.Select(s => s.Text)));
        Assert.Contains("Thinking", statusText);

        // MessageDelta — text appended to live block
        JsonElement delta = JsonSerializer.SerializeToElement(new { type = "textDelta", delta = "hi" });
        MessageDeltaEvent deltaEvt = new()
        {
            Message = JsonSerializer.SerializeToElement(new { }),
            Delta   = delta,
        };
        await handler.HandleRpcEventAsync((Event)deltaEvt, CancellationToken.None);

        // Confirm live-block open + text appended
        Assert.NotEmpty(fake.Calls.OfType<LiveBegun>());
        LiveAppendText? appendCall = fake.Calls.OfType<LiveAppendText>().LastOrDefault();
        Assert.NotNull(appendCall);
        Assert.Equal("hi", appendCall.Text);

        // TurnEnd — live block committed, status clears thinking
        TurnEndEvent turnEnd = new()
        {
            Message     = JsonSerializer.SerializeToElement(new { }),
            ToolResults = [],
        };
        await handler.HandleRpcEventAsync((Event)turnEnd, CancellationToken.None);

        Assert.NotEmpty(fake.Calls.OfType<LiveCommitted>());

        StatusSet? afterEnd = fake.Calls.OfType<StatusSet>().LastOrDefault();
        Assert.NotNull(afterEnd);
    }

    [Fact]
    public async Task HandleAsync_TurnEnd_SettlesWithMarkdownRenderBeforeCommit()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        // Establish model so TurnStart status call succeeds.
        await handler.HandleRpcEventAsync(
            (Event)new ProviderSwitchedEvent { Name = "anthropic", Model = "claude-3-7-sonnet" },
            CancellationToken.None);

        await handler.HandleRpcEventAsync((Event)new TurnStartEvent(), CancellationToken.None);

        // Append a heading token.
        JsonElement delta = System.Text.Json.JsonSerializer.SerializeToElement(
            new { type = "textDelta", delta = "# Result" });
        MessageDeltaEvent deltaEvt = new()
        {
            Message = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            Delta   = delta,
        };
        await handler.HandleRpcEventAsync((Event)deltaEvt, CancellationToken.None);

        TurnEndEvent turnEnd = new()
        {
            Message     = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            ToolResults = [],
        };
        await handler.HandleRpcEventAsync((Event)turnEnd, CancellationToken.None);

        // TurnEnd calls: SetContent, Committed (from SettleTurn), then SetStatus.
        LiveSetContent setContent = Assert.Single(fake.Calls.OfType<LiveSetContent>());
        LiveCommitted committed = Assert.Single(fake.Calls.OfType<LiveCommitted>());

        // SetContent arrives before Committed — use positional index via ToList.
        List<FakeCall> callList = fake.Calls.ToList();
        int setContentIdx = callList.IndexOf(setContent);
        int committedIdx  = callList.IndexOf(committed);
        Assert.True(setContentIdx < committedIdx, "SetContent must precede Committed.");

        // The rendered heading has text "Result" with Bold + Underline.
        Assert.True(setContent.Lines.Count >= 1);
        string headingText = string.Concat(setContent.Lines[0].Segments.Select(s => s.Text));
        Assert.Equal("Result", headingText);
        Assert.All(setContent.Lines[0].Segments, seg =>
        {
            Assert.True(seg.Style.Format.HasFlag(Format.Bold));
            Assert.True(seg.Style.Format.HasFlag(Format.Underline));
        });
    }

    // ── Locked-input drop (spec scenario: "Locked input dropped during a turn") ─

    [Fact]
    public async Task HandleAsync_InputSubmitted_WhileLocked_DropsAndDoesNotForward()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        InputStateLayer layer = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts, inputLayer: layer);

        // Set buffer text via InputChanged so we can verify it is NOT cleared by the locked submit.
        await handler.HandleUiEventAsync(new InputChanged("hello"), CancellationToken.None);

        // Lock via the production path.
        await handler.HandleRpcEventAsync((Event)new TurnStartEvent(), CancellationToken.None);
        sentCommands.Clear(); // discard any commands from TurnStart

        await handler.HandleUiEventAsync(new InputSubmitted("hello"), CancellationToken.None);

        // No TurnSubmitCommand (or any other command) forwarded to core.
        Assert.Empty(sentCommands);
        // State layer did not append to History.
        Assert.Empty(layer.History);
        // InputSubmitted does not touch CurrentBuffer — only InputChanged does.
        // The buffer remains what InputChanged last set it to.
        Assert.Equal("hello", layer.CurrentBuffer);
    }

    [Fact]
    public async Task HandleAsync_InputSubmitted_WhileLocked_StateLayerHistoryNotAppended()
    {
        // Dedicated History invariant test so a regression that only breaks one assertion is obvious.
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        InputStateLayer layer = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts, inputLayer: layer);

        await handler.HandleRpcEventAsync((Event)new TurnStartEvent(), CancellationToken.None);

        await handler.HandleUiEventAsync(new InputSubmitted("dropped"), CancellationToken.None);

        Assert.Empty(layer.History);
    }

    [Fact]
    public async Task HandleAsync_InputSubmitted_AfterTurnEnd_ForwardsAgain()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        InputStateLayer layer = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts, inputLayer: layer);

        // Lock via TurnStart, then release via TurnEnd.
        await handler.HandleRpcEventAsync((Event)new TurnStartEvent(), CancellationToken.None);

        TurnEndEvent turnEnd = new()
        {
            Message     = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            ToolResults = [],
        };
        await handler.HandleRpcEventAsync((Event)turnEnd, CancellationToken.None);

        sentCommands.Clear(); // discard anything from the lifecycle events

        await handler.HandleUiEventAsync(new InputSubmitted("ready now"), CancellationToken.None);

        TurnSubmitCommand cmd = Assert.Single(sentCommands.OfType<TurnSubmitCommand>());
        Assert.Equal("ready now", cmd.Message);
        // State layer also appended to History now that the lock is released.
        Assert.Contains("ready now", layer.History);
    }

    // ── DrainAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DrainAsync_NonCancelException_LogsAndCancels()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        Channel<TerminalEvent> channel = Channel.CreateUnbounded<TerminalEvent>();
        channel.Writer.TryWrite(new InputChanged("x"));
        channel.Writer.Complete(new InvalidOperationException("synthetic"));

        // Must return normally — no exception escapes.
        await handler.DrainAsync(channel.Reader, cts.Token);

        IEnumerable<string> lines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));

        Assert.Contains(lines, l =>
            l.StartsWith("[Drain Error]", StringComparison.Ordinal)
            && l.Contains("InvalidOperationException")
            && l.Contains("synthetic"));

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task DrainAsync_ProcessesAllEventsThenCompletes()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        Channel<TerminalEvent> channel = Channel.CreateUnbounded<TerminalEvent>();
        channel.Writer.TryWrite(new InputSubmitted("first message"));
        channel.Writer.TryWrite(new InputSubmitted("second message"));
        channel.Writer.Complete();

        await handler.DrainAsync(channel.Reader, CancellationToken.None);

        IReadOnlyList<TurnSubmitCommand> cmds = sentCommands.OfType<TurnSubmitCommand>().ToList();
        Assert.Equal(2, cmds.Count);
        Assert.Equal("first message",  cmds[0].Message);
        Assert.Equal("second message", cmds[1].Message);
    }

    // ── Bug B: WizardCompletedStep — no answer command sent, success line rendered ─

    private static WizardStepEvent MakeStepEvent<T>(string wizardId, T step) where T : Dmon.Protocol.Wizard.WizardStep =>
        new() { WizardId = wizardId, Step = step };

    [Fact]
    public async Task WizardCompletedStep_RendersSuccessLine_DoesNotSendAnswerCommand()
    {
        // The terminal must render the completion message and must NOT send a WizardAnswerCommand.
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts);

        // Activate wizard state.
        await handler.HandleRpcEventAsync(
            (Event)new SetupRequiredEvent { Adapters = [] }, CancellationToken.None);
        // The WizardStartCommand is sent; clear it so assertions are clean.
        sentCommands.Clear();

        Dmon.Protocol.Wizard.WizardCompletedStep completedStep = new()
        {
            Id = "done",
            Prompt = "Complete",
            Message = "Provider configured successfully!"
        };

        await handler.HandleRpcEventAsync(
            (Event)MakeStepEvent("wiz-1", completedStep), CancellationToken.None);

        // No WizardAnswerCommand must have been sent.
        Assert.Empty(sentCommands.OfType<WizardAnswerCommand>());

        // The completion message must appear in scrollback.
        IEnumerable<string> lines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));
        Assert.Contains(lines, l => l.Contains("Provider configured successfully!"));
    }

    [Fact]
    public async Task ProviderConfiguredEvent_ShowsConfirmationLine_ClearsWizardState()
    {
        // ProviderConfiguredEvent while wizard is active renders a confirmation line
        // and unlocks input.
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();
        InputStateLayer layer = new();
        ConsoleEventHandler handler = BuildHandler(fake, sentCommands, cts, inputLayer: layer);

        // Lock input and activate wizard (simulating SetupRequired flow).
        await handler.HandleRpcEventAsync(
            (Event)new SetupRequiredEvent { Adapters = [] }, CancellationToken.None);

        ProviderConfiguredEvent evt = new()
        {
            Adapter = "openai",
            ModelId = "gpt-4o",
            Scope   = "global",
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        // Confirmation line must appear in scrollback.
        IEnumerable<string> lines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));
        Assert.Contains(lines, l => l.Contains("openai") && l.Contains("gpt-4o"));

        // Input must be unlocked.
        Assert.False(layer.IsLocked);
    }
}
