using Dcli;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Protocol.Wizard;
using Dmon.Terminal.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Tests for WizardEngine navigation: cancel, back, and happy-path flows.
/// FakeTerminal scripts dialog responses so no real console is required.
/// </summary>
public sealed class WizardEngineTests
{
    // ------------------------------------------------------------------ //
    //  Cancel at provider-selection screen
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_CancelAtProviderSelection_ReturnsNull()
    {
        FakeTerminal fake = new();
        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default));

        WizardEngine engine = new(fake, [new FakeProviderFactory("fake", "Fake")]);

        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RunAsync_BackAtProviderSelection_ReturnsNull()
    {
        FakeTerminal fake = new();
        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Back, default));

        WizardEngine engine = new(fake, [new FakeProviderFactory("fake", "Fake")]);

        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        Assert.Null(result);
    }

    // ------------------------------------------------------------------ //
    //  AllowBack = true on every wizard pick step
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_ProviderSelection_SelectRequestHasAllowBackTrue()
    {
        FakeTerminal fake = new();
        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default));

        WizardEngine engine = new(fake, [new FakeProviderFactory("fake", "Fake")]);
        await engine.RunAsync(CancellationToken.None);

        SelectOpened call = Assert.IsType<SelectOpened>(fake.Calls[0]);
        Assert.True(call.Request.AllowBack);
    }

    // ------------------------------------------------------------------ //
    //  Back from first factory step returns to provider selection
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_BackFromFirstFactoryStep_ReturnsToProviderSelection_ThenCancel()
    {
        // Use ChooseOneFactory so the first factory step is a SelectAsync call (supports Back).
        // Round 1: provider selection → Submitted (pick index 0)
        // Round 2: first factory step (ChooseOneStep) → Back
        // Round 3: provider selection again → Cancelled
        ChooseOneFactory factory = new("fake", "Fake");
        FakeTerminal fake = new();

        int selectCount = 0;
        fake.OnSelectAsync = (req, _) =>
        {
            selectCount++;
            return selectCount switch
            {
                1 => Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0)), // pick provider
                2 => Task.FromResult(new DialogResult<int>(DialogOutcome.Back, default)), // back on factory step
                3 => Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default)), // cancel at re-shown provider select
                _ => throw new InvalidOperationException($"Unexpected select call #{selectCount}")
            };
        };

        WizardEngine engine = new(fake, [factory]);
        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(3, selectCount);
    }

    // ------------------------------------------------------------------ //
    //  Back from factory step — engine trims state and re-renders adapter step
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_BackFromFactoryStep_SelectCalledTwice()
    {
        // Use ChooseOneFactory so the first factory step is a SelectAsync call (supports Back).
        ChooseOneFactory factory = new("fake", "Fake");
        FakeTerminal fake = new();

        int selectCount = 0;
        fake.OnSelectAsync = (req, _) =>
        {
            selectCount++;
            return selectCount switch
            {
                1 => Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0)), // pick provider
                2 => Task.FromResult(new DialogResult<int>(DialogOutcome.Back, default)), // back on factory step
                _ => Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default))
            };
        };

        WizardEngine engine = new(fake, [factory]);
        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        Assert.Null(result);
        // At minimum: adapter select, factory step (Back), adapter select again
        Assert.True(selectCount >= 2);
        IReadOnlyList<FakeCall> selectCalls = fake.Calls.OfType<SelectOpened>().ToList();
        Assert.True(selectCalls.Count >= 2);
    }

    // ------------------------------------------------------------------ //
    //  Cancel during factory steps
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_CancelDuringFactoryStep_ReturnsNull()
    {
        FakeProviderFactory factory = new("fake", "Fake");
        FakeTerminal fake = new();

        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));
        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Cancelled, string.Empty));

        WizardEngine engine = new(fake, [factory]);
        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        Assert.Null(result);
    }

    // ------------------------------------------------------------------ //
    //  Cancellation token
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_CancellationRequested_ThrowsOrReturnsNull()
    {
        using CancellationTokenSource cts = new();
        FakeProviderFactory factory = new("fake", "Fake");
        FakeTerminal fake = new();

        fake.OnSelectAsync = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default));
        };

        WizardEngine engine = new(fake, [factory]);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => engine.RunAsync(cts.Token));
    }

    // ------------------------------------------------------------------ //
    //  Happy path: select provider → answer api-key → answer model → WizardResult
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsWizardResultWithCorrectAdapter()
    {
        FakeProviderFactory factory = new("myprovider", "MyProvider");
        FakeTerminal fake = BuildHappyPathFake(factory, apiKey: "sk-test", modelIndex: 0);

        WizardEngine engine = new(fake, [factory]);
        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("myprovider", result.Adapter);
    }

    [Fact]
    public async Task RunAsync_HappyPath_WizardResultModelIdMatchesSelectedOption()
    {
        FakeProviderFactory factory = new("myprovider", "MyProvider");
        FakeTerminal fake = BuildHappyPathFake(factory, apiKey: "sk-test", modelIndex: 0);

        WizardEngine engine = new(fake, [factory]);
        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        // FakeProviderFactory.ModelOptions[0] is "model-alpha"
        Assert.NotNull(result);
        Assert.Equal("model-alpha", result.ModelId);
    }

    // ------------------------------------------------------------------ //
    //  AllowBack on factory ChooseOne steps
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_FactoryChooseOneStep_SelectRequestHasAllowBackTrue()
    {
        FakeProviderFactory factory = new("myprovider", "MyProvider");
        FakeTerminal fake = BuildHappyPathFake(factory, apiKey: "sk-test", modelIndex: 0);

        WizardEngine engine = new(fake, [factory]);
        await engine.RunAsync(CancellationToken.None);

        // All SelectAsync calls should have AllowBack = true.
        foreach (SelectOpened call in fake.Calls.OfType<SelectOpened>())
        {
            Assert.True(call.Request.AllowBack, $"SelectRequest for step should have AllowBack = true");
        }
    }

    // ------------------------------------------------------------------ //
    //  Step ordering — multi-step factory flow calls Opened in order
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_HappyPath_StepOrderIsSelectThenInputThenSelect()
    {
        FakeProviderFactory factory = new("myprovider", "MyProvider");
        FakeTerminal fake = BuildHappyPathFake(factory, apiKey: "sk-test", modelIndex: 0);

        WizardEngine engine = new(fake, [factory]);
        await engine.RunAsync(CancellationToken.None);

        List<FakeCall> dialogCalls = fake.Calls
            .Where(c => c is SelectOpened or InputOpened or ChoiceOpened)
            .ToList();

        // adapter select, api-key input, model select
        Assert.Equal(3, dialogCalls.Count);
        Assert.IsType<SelectOpened>(dialogCalls[0]);
        Assert.IsType<InputOpened>(dialogCalls[1]);
        Assert.IsType<SelectOpened>(dialogCalls[2]);
    }

    // ------------------------------------------------------------------ //
    //  Helpers
    // ------------------------------------------------------------------ //

    private static FakeTerminal BuildHappyPathFake(
        FakeProviderFactory factory,
        string apiKey,
        int modelIndex)
    {
        FakeTerminal fake = new();

        int selectCount = 0;
        fake.OnSelectAsync = (req, _) =>
        {
            selectCount++;
            // Round 1: adapter selection → pick index 0
            // Round 2: model selection → pick modelIndex
            return Task.FromResult(new DialogResult<int>(
                DialogOutcome.Submitted,
                selectCount == 1 ? 0 : modelIndex));
        };

        fake.OnInputAsync = (req, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Submitted, apiKey));

        return fake;
    }

    // ------------------------------------------------------------------ //
    //  Test double
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Factory whose first step is a ChooseOneStep (so SelectAsync/Back navigation is testable).
    /// Completes after a single selection.
    /// </summary>
    private sealed class ChooseOneFactory(string adapterName, string displayName) : IProviderFactory
    {
        public string AdapterName    => adapterName;
        public string DisplayName    => displayName;
        public string DefaultModelId => "model-x";
        public string DefaultEnvVar  => "FAKE_KEY";

        public ChatClientCapabilities GetCapabilities(string modelId) =>
            new() { SupportsToolCalling = false, SupportsReasoning = false };

        public ValueTask<IChatClient> CreateAsync(
            ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<WizardStep> GetNextStepAsync(
            WizardState state, CancellationToken cancellationToken = default)
        {
            ChooseOneStep? pick = state.Steps.OfType<ChooseOneStep>().FirstOrDefault(s => s.Id == "pick");
            if (pick is null || !pick.IsAnswered)
            {
                return ValueTask.FromResult<WizardStep>(new ChooseOneStep
                {
                    Id      = "pick",
                    Prompt  = "Pick something",
                    Options = [new WizardOption("Option A", "a")],
                });
            }

            return ValueTask.FromResult<WizardStep>(new WizardCompletedStep
            {
                Id      = "done",
                Prompt  = string.Empty,
                Message = "Done.",
            });
        }
    }

    /// <summary>
    /// Minimal IProviderFactory that drives through a fixed two-step wizard:
    /// api-key (TextInputStep) → model (ChooseOneStep) → WizardCompletedStep.
    /// </summary>
    private sealed class FakeProviderFactory(string adapterName, string displayName) : IProviderFactory
    {
        public static readonly IReadOnlyList<WizardOption> ModelOptions =
        [
            new WizardOption("Model Alpha", "model-alpha"),
            new WizardOption("Model Beta",  "model-beta"),
        ];

        public string AdapterName    => adapterName;
        public string DisplayName    => displayName;
        public string DefaultModelId => "model-alpha";
        public string DefaultEnvVar  => "FAKE_API_KEY";

        public ChatClientCapabilities GetCapabilities(string modelId) =>
            new() { SupportsToolCalling = false, SupportsReasoning = false };

        public ValueTask<IChatClient> CreateAsync(
            ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not needed in tests.");

        public ValueTask<WizardStep> GetNextStepAsync(
            WizardState state, CancellationToken cancellationToken = default)
        {
            TextInputStep? apiKeyStep = state.Steps
                .OfType<TextInputStep>()
                .FirstOrDefault(s => s.Id == "api-key");

            if (apiKeyStep is null || !apiKeyStep.IsAnswered)
            {
                return ValueTask.FromResult<WizardStep>(new TextInputStep
                {
                    Id       = "api-key",
                    Prompt   = "API key",
                    Secret   = true,
                    Required = true,
                });
            }

            ChooseOneStep? modelStep = state.Steps
                .OfType<ChooseOneStep>()
                .FirstOrDefault(s => s.Id == "model");

            if (modelStep is null || !modelStep.IsAnswered)
            {
                return ValueTask.FromResult<WizardStep>(new ChooseOneStep
                {
                    Id      = "model",
                    Prompt  = "Select a model",
                    Options = ModelOptions,
                });
            }

            return ValueTask.FromResult<WizardStep>(new WizardCompletedStep
            {
                Id      = "completed",
                Prompt  = string.Empty,
                Message = $"Configured {displayName}.",
            });
        }
    }
}
