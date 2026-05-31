using Dcli;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Wizard;
using Dmon.Terminal.Tests.Fakes;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Tests for the event-driven wizard rendering in <see cref="ConsoleEventHandler"/>.
/// Drives the handler with <see cref="WizardStepEvent"/> instances and asserts the correct
/// <see cref="WizardAnswerCommand"/> is sent and the terminal is prompted as expected.
/// </summary>
public sealed class WizardRendererTests
{
    // ── helpers ────────────────────────────────────────────────────────────

    private static string LineText(Line l) =>
        string.Concat(l.Segments.Select(s => s.Text));

    /// <summary>
    /// Builds a handler in wizard-active state by first sending a SetupRequiredEvent,
    /// which triggers HandleAddProviderAsync → sends WizardStartCommand → sets _wizardActive.
    /// </summary>
    private static async Task<(ConsoleEventHandler Handler, FakeTerminal Fake, List<Command> Sent)>
        BuildActiveWizardAsync()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();

        Func<Command, CancellationToken, Task> send = (cmd, _) =>
        {
            sentCommands.Add(cmd);
            return Task.CompletedTask;
        };

        TerminalRenderer renderer = new(fake);
        InputStateLayer input = new();
        ConsoleEventHandler handler = new(renderer, input, send, cts, () => { }, fake);

        // Activate the wizard: SetupRequiredEvent → HandleAddProviderAsync → WizardStartCommand sent.
        SetupRequiredEvent setup = new() { Adapters = [] };
        await handler.HandleRpcEventAsync((Event)setup, CancellationToken.None);

        // Clear the WizardStartCommand from the sent list so tests only see the answer commands.
        sentCommands.Clear();

        return (handler, fake, sentCommands);
    }

    private static WizardStepEvent StepEvt(string wizardId, WizardStep step) =>
        new() { WizardId = wizardId, Step = step };

    // ── ChooseOneStep ──────────────────────────────────────────────────────

    [Fact]
    public async Task WizardStep_ChooseOne_Answered_SendsAnsweredWithIndex()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 1));

        ChooseOneStep step = new()
        {
            Id      = "adapter",
            Prompt  = "Pick one",
            Options = [new WizardOption("Alpha", "alpha"), new WizardOption("Beta", "beta")],
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal("w1",                    cmd.WizardId);
        Assert.Equal(WizardAnswerOutcome.Answered, cmd.Outcome);
        Assert.Equal("1",                     cmd.Value);
    }

    [Fact]
    public async Task WizardStep_ChooseOne_Back_SendsBackOutcome()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Back, default));

        ChooseOneStep step = new()
        {
            Id      = "adapter",
            Prompt  = "Pick one",
            Options = [new WizardOption("Alpha", "alpha")],
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Back, cmd.Outcome);
        Assert.Null(cmd.Value);
    }

    [Fact]
    public async Task WizardStep_ChooseOne_Cancel_SendsCancelAndUnlocksInput()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default));

        ChooseOneStep step = new()
        {
            Id      = "adapter",
            Prompt  = "Pick one",
            Options = [new WizardOption("Alpha", "alpha")],
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Cancel, cmd.Outcome);

        // After cancel the wizard is inactive — a second WizardStepEvent should be ignored.
        sent.Clear();
        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));
        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);
        Assert.Empty(sent.OfType<WizardAnswerCommand>());
    }

    [Fact]
    public async Task WizardStep_ChooseOne_SelectRequest_HasAllowBackTrue()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default));

        ChooseOneStep step = new()
        {
            Id      = "adapter",
            Prompt  = "Pick one",
            Options = [new WizardOption("Alpha", "alpha")],
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        SelectOpened call = Assert.Single(fake.Calls.OfType<SelectOpened>());
        Assert.True(call.Request.AllowBack);
    }

    // ── ChooseManyStep ─────────────────────────────────────────────────────

    [Fact]
    public async Task WizardStep_ChooseMany_Answered_MultipleIndices_SendsCommaSeparatedValue()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnMultiSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int[]>(DialogOutcome.Submitted, [0, 2]));

        ChooseManyStep step = new()
        {
            Id      = "features",
            Prompt  = "Pick features",
            Options =
            [
                new WizardOption("A", "a"),
                new WizardOption("B", "b"),
                new WizardOption("C", "c"),
            ],
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Answered, cmd.Outcome);
        Assert.Equal("0,2", cmd.Value);
    }

    [Fact]
    public async Task WizardStep_ChooseMany_Back_SendsBackOutcome()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnMultiSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int[]>(DialogOutcome.Back, []));

        ChooseManyStep step = new()
        {
            Id      = "features",
            Prompt  = "Pick features",
            Options = [new WizardOption("A", "a"), new WizardOption("B", "b")],
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Back, cmd.Outcome);
        Assert.Null(cmd.Value);
    }

    [Fact]
    public async Task WizardStep_ChooseMany_MultiSelectRequest_HasAllowBackTrue()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnMultiSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int[]>(DialogOutcome.Submitted, [0]));

        ChooseManyStep step = new()
        {
            Id      = "features",
            Prompt  = "Pick features",
            Options = [new WizardOption("A", "a")],
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        MultiSelectOpened call = Assert.Single(fake.Calls.OfType<MultiSelectOpened>());
        Assert.True(call.Request.AllowBack);
    }

    // ── TextInputStep ──────────────────────────────────────────────────────

    [Fact]
    public async Task WizardStep_TextInput_Answered_SendsValue()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Submitted, "sk-test"));

        TextInputStep step = new()
        {
            Id       = "api-key",
            Prompt   = "API key",
            Secret   = false,
            Required = true,
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Answered, cmd.Outcome);
        Assert.Equal("sk-test", cmd.Value);
    }

    [Fact]
    public async Task WizardStep_TextInput_Secret_InputRequestIsSecret()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Submitted, "secret123"));

        TextInputStep step = new()
        {
            Id       = "api-key",
            Prompt   = "API key",
            Secret   = true,
            Required = true,
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        InputOpened call = Assert.Single(fake.Calls.OfType<InputOpened>());
        Assert.True(call.Request.IsSecret);
    }

    [Fact]
    public async Task WizardStep_TextInput_SecretWithDefault_ShowsMaskedHint()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Submitted, "new-value"));

        TextInputStep step = new()
        {
            Id       = "api-key",
            Prompt   = "API key",
            Secret   = true,
            Required = false,
            Default  = "existing-key",
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        // The scrollback should contain a masked hint, not the raw default.
        IEnumerable<string> scrollbackLines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));

        Assert.Contains(scrollbackLines, l => l.Contains("Default:") && l.Contains("********"));
        Assert.DoesNotContain(scrollbackLines, l => l.Contains("existing-key"));
    }

    [Fact]
    public async Task WizardStep_TextInput_NonSecretWithDefault_ShowsRawDefault()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Submitted, "new-val"));

        TextInputStep step = new()
        {
            Id       = "url",
            Prompt   = "URL",
            Secret   = false,
            Required = false,
            Default  = "http://localhost",
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        IEnumerable<string> scrollbackLines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));

        Assert.Contains(scrollbackLines, l => l.Contains("Default:") && l.Contains("http://localhost"));
    }

    [Fact]
    public async Task WizardStep_TextInput_EmptyInput_UsesDefault()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        // User submits empty string — should fall back to default.
        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Submitted, string.Empty));

        TextInputStep step = new()
        {
            Id       = "url",
            Prompt   = "URL",
            Secret   = false,
            Required = false,
            Default  = "http://localhost",
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Answered, cmd.Outcome);
        Assert.Equal("http://localhost", cmd.Value);
    }

    [Fact]
    public async Task WizardStep_TextInput_Required_EmptyAndNoDefault_SendsCancel()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Submitted, string.Empty));

        TextInputStep step = new()
        {
            Id       = "api-key",
            Prompt   = "API key",
            Secret   = false,
            Required = true,
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Cancel, cmd.Outcome);
    }

    [Fact]
    public async Task WizardStep_TextInput_Back_SendsBackOutcome()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Back, string.Empty));

        TextInputStep step = new()
        {
            Id       = "api-key",
            Prompt   = "API key",
            Secret   = false,
            Required = true,
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Back, cmd.Outcome);
        Assert.Null(cmd.Value);
    }

    [Fact]
    public async Task WizardStep_TextInput_InputRequest_HasAllowBackTrue()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Submitted, "value"));

        TextInputStep step = new()
        {
            Id       = "api-key",
            Prompt   = "API key",
            Secret   = false,
            Required = true,
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        InputOpened call = Assert.Single(fake.Calls.OfType<InputOpened>());
        Assert.True(call.Request.AllowBack);
    }

    // ── YesNoStep ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WizardStep_YesNo_DefaultTrue_SelectsYes_SendsTrueValue()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        // When Default=true: index 0 = Yes.
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));

        YesNoStep step = new()
        {
            Id      = "confirm",
            Prompt  = "Are you sure?",
            Default = true,
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Answered, cmd.Outcome);
        Assert.Equal("true", cmd.Value);
    }

    [Fact]
    public async Task WizardStep_YesNo_DefaultTrue_SelectsNo_SendsFalseValue()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        // When Default=true: index 1 = No.
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 1));

        YesNoStep step = new()
        {
            Id      = "confirm",
            Prompt  = "Are you sure?",
            Default = true,
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal("false", cmd.Value);
    }

    [Fact]
    public async Task WizardStep_YesNo_Cancel_SendsCancelOutcome()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default));

        YesNoStep step = new()
        {
            Id      = "confirm",
            Prompt  = "Are you sure?",
            Default = false,
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Cancel, cmd.Outcome);
    }

    [Fact]
    public async Task WizardStep_YesNo_Back_SendsBackOutcome()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Back, default));

        YesNoStep step = new()
        {
            Id      = "confirm",
            Prompt  = "Are you sure?",
            Default = false,
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Back, cmd.Outcome);
        Assert.Null(cmd.Value);
    }

    [Fact]
    public async Task WizardStep_YesNo_ChoiceRequest_HasAllowBackTrue()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));

        YesNoStep step = new()
        {
            Id      = "confirm",
            Prompt  = "Are you sure?",
            Default = true,
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        ChoiceOpened call = Assert.Single(fake.Calls.OfType<ChoiceOpened>());
        Assert.True(call.Request.AllowBack);
    }

    // ── InfoStep ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WizardStep_Info_AppendsToScrollbackAndSendsAnswered()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        InfoStep step = new()
        {
            Id     = "info-1",
            Prompt = "Please read this.",
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        IEnumerable<string> scrollbackLines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));

        Assert.Contains(scrollbackLines, l => l.Contains("Please read this."));

        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Answered, cmd.Outcome);
        Assert.Null(cmd.Value);
    }

    // ── WizardCompletedStep ────────────────────────────────────────────────

    [Fact]
    public async Task WizardStep_Completed_RendersMessageAndDoesNotSendAnswerCommand()
    {
        // WizardCompletedStep is a terminal/no-answer step: the core emits it fire-and-forget
        // and never awaits a WizardAnswerCommand for it. The terminal must NOT send one.
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        WizardCompletedStep step = new()
        {
            Id      = "done",
            Prompt  = string.Empty,
            Message = "Provider configured successfully!",
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        IEnumerable<string> scrollbackLines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));

        Assert.Contains(scrollbackLines, l => l.Contains("Provider configured successfully!"));

        // No WizardAnswerCommand must be sent — the step is terminal.
        Assert.Empty(sent.OfType<WizardAnswerCommand>());
    }

    // ── ProviderConfiguredEvent (completion) ───────────────────────────────

    [Fact]
    public async Task ProviderConfigured_ClearsWizardActiveAndUnlocksInput()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        ProviderConfiguredEvent configured = new()
        {
            Adapter = "anthropic",
            ModelId = "claude-3-7-sonnet",
            Scope   = "global",
        };

        await handler.HandleRpcEventAsync((Event)configured, CancellationToken.None);

        // After ProviderConfiguredEvent the wizard is no longer active.
        // A subsequent WizardStepEvent should NOT trigger a select/input call.
        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));

        ChooseOneStep staleStep = new()
        {
            Id      = "s",
            Prompt  = "p",
            Options = [new WizardOption("A", "a")],
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", staleStep), CancellationToken.None);

        Assert.Empty(fake.Calls.OfType<SelectOpened>());
    }

    [Fact]
    public async Task ProviderConfigured_LogsAdapterAndModel()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        ProviderConfiguredEvent configured = new()
        {
            Adapter = "openai",
            ModelId = "gpt-4o",
            Scope   = "global",
        };

        await handler.HandleRpcEventAsync((Event)configured, CancellationToken.None);

        IEnumerable<string> scrollbackLines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));

        Assert.Contains(scrollbackLines, l =>
            l.Contains("openai") && l.Contains("gpt-4o"));
    }

    // ── Re-prompt (same step emitted again after wizard.invalidAnswer) ─────

    [Fact]
    public async Task WizardStep_ReEmittedSameStep_RendersCleanlyWithoutError()
    {
        (ConsoleEventHandler handler, FakeTerminal fake, List<Command> sent) =
            await BuildActiveWizardAsync();

        int callCount = 0;
        fake.OnInputAsync = (_, _) =>
        {
            callCount++;
            return Task.FromResult(new DialogResult<string>(DialogOutcome.Submitted, "value"));
        };

        TextInputStep step = new()
        {
            Id       = "api-key",
            Prompt   = "API key",
            Secret   = false,
            Required = true,
        };

        // Simulate the core re-emitting the same step (e.g. after wizard.invalidAnswer).
        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);
        sent.Clear();
        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        // The step rendered twice without any error; two answer commands were sent in total.
        Assert.Equal(2, callCount);
        WizardAnswerCommand cmd = Assert.Single(sent.OfType<WizardAnswerCommand>());
        Assert.Equal(WizardAnswerOutcome.Answered, cmd.Outcome);
    }

    // ── WizardStartCommand sent on /add-provider ───────────────────────────

    [Fact]
    public async Task HandleAddProvider_SlashCommand_SendsWizardStartCommand()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();

        Func<Command, CancellationToken, Task> send = (cmd, _) =>
        {
            sentCommands.Add(cmd);
            return Task.CompletedTask;
        };

        TerminalRenderer renderer = new(fake);
        InputStateLayer input = new();
        ConsoleEventHandler handler = new(renderer, input, send, cts, () => { }, fake);

        await handler.HandleUserInputAsync("/add-provider", CancellationToken.None);

        Assert.Single(sentCommands.OfType<WizardStartCommand>());
    }

    // ── Stale event when wizard inactive ──────────────────────────────────

    [Fact]
    public async Task WizardStepEvent_WhenWizardNotActive_IsIgnored()
    {
        FakeTerminal fake = new();
        List<Command> sentCommands = [];
        using CancellationTokenSource cts = new();

        Func<Command, CancellationToken, Task> send = (cmd, _) =>
        {
            sentCommands.Add(cmd);
            return Task.CompletedTask;
        };

        TerminalRenderer renderer = new(fake);
        InputStateLayer input = new();
        ConsoleEventHandler handler = new(renderer, input, send, cts, () => { }, fake);

        // No wizard started — dispatch a WizardStepEvent directly.
        ChooseOneStep step = new()
        {
            Id      = "s",
            Prompt  = "p",
            Options = [new WizardOption("A", "a")],
        };

        await handler.HandleRpcEventAsync((Event)StepEvt("w1", step), CancellationToken.None);

        // Nothing was sent and no select was opened.
        Assert.Empty(sentCommands.OfType<WizardAnswerCommand>());
        Assert.Empty(fake.Calls.OfType<SelectOpened>());
    }
}
