using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Microsoft.Extensions.AI;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Tests for WizardEngine navigation: cancel, back, and happy-path flows.
/// The renderer delegate is replaced with a fake that drives the engine
/// programmatically, so no real console is required.
/// </summary>
public sealed class WizardEngineTests
{
    // ------------------------------------------------------------------ //
    //  Cancel at provider-selection screen
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_CancelAtProviderSelection_ReturnsNull()
    {
        WizardEngine engine = BuildEngine(
            [new FakeProviderFactory("fake", "Fake")],
            step => Task.FromResult(WizardStepOutcome.Cancel));

        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RunAsync_BackAtProviderSelection_ReturnsNull()
    {
        WizardEngine engine = BuildEngine(
            [new FakeProviderFactory("fake", "Fake")],
            step => Task.FromResult(WizardStepOutcome.Back));

        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        Assert.Null(result);
    }

    // ------------------------------------------------------------------ //
    //  Back from first factory step returns to provider selection
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_BackFromFirstFactoryStep_ReturnsToProviderSelection_ThenCancel()
    {
        // Round 1: provider selection → Answered (pick index 0)
        // Round 2: first factory step → Back
        // Round 3: provider selection again → Cancel
        int callCount = 0;
        WizardEngine engine = BuildEngine(
            [new FakeProviderFactory("fake", "Fake")],
            step =>
            {
                callCount++;
                return callCount switch
                {
                    1 => AnswerChooseOne(step, 0),           // pick provider
                    2 => Task.FromResult(WizardStepOutcome.Back),  // back on first step
                    3 => Task.FromResult(WizardStepOutcome.Cancel), // cancel at provider select
                    _ => throw new InvalidOperationException($"Unexpected render call #{callCount}")
                };
            });

        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(3, callCount);
    }

    // ------------------------------------------------------------------ //
    //  Cancel during factory steps
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_CancelDuringFactoryStep_ReturnsNull()
    {
        // Pick the provider, then cancel on the first factory step.
        int callCount = 0;
        WizardEngine engine = BuildEngine(
            [new FakeProviderFactory("fake", "Fake")],
            step =>
            {
                callCount++;
                return callCount switch
                {
                    1 => AnswerChooseOne(step, 0),
                    2 => Task.FromResult(WizardStepOutcome.Cancel),
                    _ => throw new InvalidOperationException($"Unexpected render call #{callCount}")
                };
            });

        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        Assert.Null(result);
    }

    // ------------------------------------------------------------------ //
    //  Happy path: select provider → answer api-key → answer model → WizardResult
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsWizardResultWithCorrectAdapter()
    {
        // Factory returns:
        //   Step 1 (api-key TextInputStep) → answered with "sk-test"
        //   Step 2 (model ChooseOneStep)   → answered with index 0
        //   Step 3 (WizardCompletedStep)   → engine returns result directly
        FakeProviderFactory factory = new("myprovider", "MyProvider");

        int callCount = 0;
        WizardEngine engine = BuildEngine(
            [factory],
            step =>
            {
                callCount++;
                return callCount switch
                {
                    1 => AnswerChooseOne(step, 0),              // pick provider
                    2 => AnswerTextInput(step, "sk-test"),       // api-key
                    3 => AnswerChooseOne(step, 0),              // model
                    _ => throw new InvalidOperationException($"Unexpected render call #{callCount}")
                };
            });

        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("myprovider", result.Adapter);
    }

    [Fact]
    public async Task RunAsync_HappyPath_WizardResultModelIdMatchesSelectedOption()
    {
        FakeProviderFactory factory = new("myprovider", "MyProvider");

        int callCount = 0;
        WizardEngine engine = BuildEngine(
            [factory],
            step =>
            {
                callCount++;
                return callCount switch
                {
                    1 => AnswerChooseOne(step, 0),
                    2 => AnswerTextInput(step, "sk-test"),
                    3 => AnswerChooseOne(step, 0),
                    _ => throw new InvalidOperationException($"Unexpected render call #{callCount}")
                };
            });

        WizardResult? result = await engine.RunAsync(CancellationToken.None);

        // FakeProviderFactory.ModelOptions[0] is "model-alpha"
        Assert.NotNull(result);
        Assert.Equal("model-alpha", result.ModelId);
    }

    // ------------------------------------------------------------------ //
    //  Helpers
    // ------------------------------------------------------------------ //

    private static WizardEngine BuildEngine(
        IReadOnlyList<IProviderFactory> factories,
        Func<WizardStep, Task<WizardStepOutcome>> renderer)
    {
        return new WizardEngine(factories, (step, _) => renderer(step));
    }

    private static Task<WizardStepOutcome> AnswerChooseOne(WizardStep step, int index)
    {
        if (step is ChooseOneStep s)
            s.SelectedIndex = index;
        return Task.FromResult(WizardStepOutcome.Answered);
    }

    private static Task<WizardStepOutcome> AnswerTextInput(WizardStep step, string value)
    {
        if (step is TextInputStep s)
            s.Value = value;
        return Task.FromResult(WizardStepOutcome.Answered);
    }

    // ------------------------------------------------------------------ //
    //  Test double
    // ------------------------------------------------------------------ //

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
