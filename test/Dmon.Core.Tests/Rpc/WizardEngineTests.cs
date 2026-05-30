using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Core.Tests.Fakes;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Rpc;

/// <summary>
/// Behavioural tests for the wizard engine embedded in <see cref="ProviderSetupHandler"/>.
/// Uses a scriptable fake factory that returns steps from a pre-configured queue.
/// </summary>
public sealed class WizardEngineTests : IDisposable
{
    private readonly string _tempDir;

    public WizardEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dmon-wizard-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ─── helpers ──────────────────────────────────────────────────────

    private string GlobalPath => Path.Combine(_tempDir, "global", ".dmon", "config.yaml");
    private string LocalPath  => Path.Combine(_tempDir, "local",  ".dmon", "config.yaml");

    private TestablePsh MakeHandler(
        FakeEventEmitter emitter,
        IEnumerable<IProviderFactory> factories,
        IProviderRegistry? registry = null)
        => new(emitter, GlobalPath, LocalPath, registry ?? new NoOpProviderRegistry(), factories);

    private static WizardStartCommand MakeStart(string id = "wizard-1") =>
        new() { Id = id };

    private static WizardAnswerCommand MakeAnswer(
        string wizardId,
        WizardAnswerOutcome outcome,
        string? value = null) =>
        new() { Id = Guid.NewGuid().ToString("N"), WizardId = wizardId, Outcome = outcome, Value = value };

    // Delivers an answer to the handler after a short delay so the engine loop
    // has had a chance to reach EmitAndAwaitAnswerAsync.
    private static Task DeliverAsync(
        ProviderSetupHandler handler,
        WizardAnswerCommand answer,
        int delayMs = 20)
        => Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            await handler.AnswerWizardAsync(answer, CancellationToken.None);
        });

    // ─── fake factory ─────────────────────────────────────────────────

    /// <summary>
    /// A scriptable factory whose <see cref="GetNextStepAsync"/> returns steps
    /// from a pre-configured queue, ending with <see cref="WizardCompletedStep"/>.
    /// </summary>
    private sealed class ScriptedFactory : IProviderFactory
    {
        private readonly Queue<WizardStep> _steps;

        public ScriptedFactory(
            string adapterName,
            string displayName,
            IEnumerable<WizardStep> steps)
        {
            AdapterName = adapterName;
            DisplayName = displayName;
            _steps = new Queue<WizardStep>(steps);
        }

        public string AdapterName { get; }
        public string DisplayName { get; }
        public string DefaultModelId => "default-model";
        public string DefaultEnvVar => "FAKE_API_KEY";

        public ChatClientCapabilities GetCapabilities(string modelId) => new();

        public ValueTask<IChatClient> CreateAsync(
            ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<WizardStep> GetNextStepAsync(
            WizardState state, CancellationToken cancellationToken = default)
        {
            if (_steps.TryDequeue(out WizardStep? step))
                return ValueTask.FromResult(step);

            return ValueTask.FromResult<WizardStep>(new WizardCompletedStep
            {
                Id = "done",
                Prompt = "Setup complete",
                Message = "Provider configured!"
            });
        }
    }

    private static ScriptedFactory MakeFactory(
        string adapter = "fake",
        string display = "Fake Provider",
        IEnumerable<WizardStep>? steps = null)
        => new(adapter, display, steps ?? []);

    private static ChooseOneStep ProviderSelectionStep(IReadOnlyList<IProviderFactory> factories) =>
        new()
        {
            Id = "adapter",
            Prompt = "Select a provider:",
            Options = factories.Select(f => new WizardOption(f.DisplayName, f.AdapterName)).ToList(),
        };

    // ─── test doubles ─────────────────────────────────────────────────

    private sealed class TestablePsh(
        IEventEmitter emitter,
        string globalConfigPath,
        string localConfigPath,
        IProviderRegistry registry,
        IEnumerable<IProviderFactory> factories)
        : ProviderSetupHandler(emitter, registry, factories)
    {
        protected override string? ResolveConfigPath(string scope) =>
            scope switch
            {
                "global" => globalConfigPath,
                "local"  => localConfigPath,
                _        => null
            };
    }

    private sealed class TrackingProviderRegistry : IProviderRegistry
    {
        public List<ProviderConfig> Added { get; } = [];

        public void AddDynamicProvider(ProviderConfig config) => Added.Add(config);

        public ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ProviderConfig GetCurrentConfig() => throw new NotSupportedException();
        public IReadOnlyList<ProviderConfig> GetAll() => throw new NotSupportedException();
        public void SetProvider(string name) { }
        public void SetModel(string modelId) { }
        public void CycleProvider() { }
        public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string? GetCurrentModelId() => null;
        public ProviderSwitchResult? CommitPendingSwitch() => null;
        public bool CurrentSupportsToolCalling => false;
        public bool CurrentSupportsReasoning => false;
    }

    private sealed class NoOpProviderRegistry : IProviderRegistry
    {
        public ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ProviderConfig GetCurrentConfig() => throw new NotSupportedException();
        public IReadOnlyList<ProviderConfig> GetAll() => throw new NotSupportedException();
        public void SetProvider(string name) { }
        public void SetModel(string modelId) { }
        public void CycleProvider() { }
        public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void AddDynamicProvider(ProviderConfig config) { }
        public string? GetCurrentModelId() => null;
        public ProviderSwitchResult? CommitPendingSwitch() => null;
        public bool CurrentSupportsToolCalling => false;
        public bool CurrentSupportsReasoning => false;
    }

    // ─── tests ────────────────────────────────────────────────────────

    /// <summary>
    /// Happy path: start → provider-select answered → factory emits api-key step →
    /// api-key answered → factory emits model step (ChooseOne) → model answered →
    /// factory emits WizardCompletedStep → ProviderConfiguredEvent emitted +
    /// provider added to registry + config file written.
    /// </summary>
    [Fact]
    public async Task HappyPath_ProviderSelect_ApiKey_Model_Completes()
    {
        const string wizardId = "wiz-1";
        FakeEventEmitter emitter = new();
        TrackingProviderRegistry registry = new();

        TextInputStep apiKeyStep = new()
        {
            Id = "api-key",
            Prompt = "API key",
            Secret = true,
            Required = true,
        };

        ChooseOneStep modelStep = new()
        {
            Id = "model",
            Prompt = "Choose model:",
            Options = [new WizardOption("Model A", "model-a"), new WizardOption("Model B", "model-b")],
        };

        ScriptedFactory factory = MakeFactory(steps: [apiKeyStep, modelStep]);
        TestablePsh handler = MakeHandler(emitter, [factory], registry);

        // Kick off the wizard (runs asynchronously in the background).
        Task wizardTask = handler.StartWizardAsync(MakeStart(wizardId), CancellationToken.None);

        // Step 0: provider selection — answer with index 0 (the only factory).
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "0"));

        // Step 1: api-key.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "sk-test-key"));

        // Step 2: model — choose index 1 = "model-b".
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "1"));

        await wizardTask;

        // ProviderConfiguredEvent must have been emitted.
        ProviderConfiguredEvent evt = Assert.Single(emitter.Emitted.OfType<ProviderConfiguredEvent>());
        Assert.Equal("fake", evt.Adapter);
        Assert.Equal("model-b", evt.ModelId);
        Assert.Equal("global", evt.Scope);

        // Provider must have been registered.
        ProviderConfig added = Assert.Single(registry.Added);
        Assert.Equal("fake", added.Adapter);
        Assert.Equal("model-b", added.DefaultModelId);

        // Config file must have been written.
        Assert.True(File.Exists(GlobalPath), "Global config file was not created.");
    }

    /// <summary>
    /// Back navigation re-asks the prior step.
    /// Start → select provider → api-key step emitted → Back → api-key step emitted again.
    /// </summary>
    [Fact]
    public async Task Back_FromFactoryStep_ReEmitsStep()
    {
        const string wizardId = "wiz-back";
        FakeEventEmitter emitter = new();

        // Factory: api-key step repeated (once for original, once after Back).
        TextInputStep apiKeyStep1 = new() { Id = "api-key", Prompt = "API key", Secret = true, Required = true };
        TextInputStep apiKeyStep2 = new() { Id = "api-key", Prompt = "API key", Secret = true, Required = true };
        ScriptedFactory factory = MakeFactory(steps: [apiKeyStep1, apiKeyStep2]);
        TestablePsh handler = MakeHandler(emitter, [factory]);

        Task wizardTask = handler.StartWizardAsync(MakeStart(wizardId), CancellationToken.None);

        // Confirm provider selection.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "0"));

        // Back from api-key step — returns to provider selection re-emit.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Back));

        // Cancel from provider selection to exit.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Cancel));

        await wizardTask;

        // Three WizardStepEvents: provider-select, api-key, provider-select (re-emitted after Back).
        IReadOnlyList<WizardStepEvent> stepEvents = emitter.Emitted.OfType<WizardStepEvent>().ToList();
        Assert.Equal(3, stepEvents.Count);
        Assert.Equal("adapter", stepEvents[0].Step.Id);
        Assert.Equal("api-key", stepEvents[1].Step.Id);
        Assert.Equal("adapter", stepEvents[2].Step.Id);

        // No ProviderConfiguredEvent — cancelled after Back.
        Assert.Empty(emitter.Emitted.OfType<ProviderConfiguredEvent>());
    }

    /// <summary>
    /// Cancel abandons the wizard without persisting anything.
    /// </summary>
    [Fact]
    public async Task Cancel_PersistsNothing()
    {
        const string wizardId = "wiz-cancel";
        FakeEventEmitter emitter = new();
        TrackingProviderRegistry registry = new();
        ScriptedFactory factory = MakeFactory();
        TestablePsh handler = MakeHandler(emitter, [factory], registry);

        Task wizardTask = handler.StartWizardAsync(MakeStart(wizardId), CancellationToken.None);

        // Cancel from provider-selection.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Cancel));

        await wizardTask;

        Assert.Empty(registry.Added);
        Assert.Empty(emitter.Emitted.OfType<ProviderConfiguredEvent>());
        Assert.False(File.Exists(GlobalPath), "Config file must not be written on cancel.");
    }

    /// <summary>
    /// A <c>wizard.answer</c> whose WizardId does not match the active session
    /// must be silently ignored — no state mutation, no events emitted.
    /// </summary>
    [Fact]
    public async Task StaleWizardId_IsIgnored()
    {
        const string wizardId    = "wiz-active";
        const string staleId     = "wiz-stale";
        FakeEventEmitter emitter = new();
        ScriptedFactory factory  = MakeFactory();
        TestablePsh handler      = MakeHandler(emitter, [factory]);

        Task wizardTask = handler.StartWizardAsync(MakeStart(wizardId), CancellationToken.None);

        // Deliver a stale answer — must be ignored.
        await DeliverAsync(handler, MakeAnswer(staleId, WizardAnswerOutcome.Answered, "0"), delayMs: 20);

        // Now cancel the real wizard.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Cancel), delayMs: 40);

        await wizardTask;

        // Only the provider-select WizardStepEvent should have been emitted.
        IReadOnlyList<WizardStepEvent> stepEvents = emitter.Emitted.OfType<WizardStepEvent>().ToList();
        Assert.Single(stepEvents);

        // No ProviderConfiguredEvent.
        Assert.Empty(emitter.Emitted.OfType<ProviderConfiguredEvent>());
    }

    /// <summary>
    /// A dynamically-registered (extension) factory appears in the provider-selection step.
    /// </summary>
    [Fact]
    public async Task ExtensionFactory_AppearsInProviderSelection()
    {
        const string wizardId = "wiz-ext";
        FakeEventEmitter emitter = new();

        // Two factories: a built-in and a "dynamically registered" extension factory.
        ScriptedFactory builtIn   = MakeFactory("anthropic", "Anthropic");
        ScriptedFactory extension = MakeFactory("myext", "My Extension");
        TestablePsh handler       = MakeHandler(emitter, [builtIn, extension]);

        Task wizardTask = handler.StartWizardAsync(MakeStart(wizardId), CancellationToken.None);

        // Cancel immediately after the first step is emitted.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Cancel));

        await wizardTask;

        WizardStepEvent providerSelectEvent = Assert.Single(emitter.Emitted.OfType<WizardStepEvent>());
        ChooseOneStep selectionStep = Assert.IsType<ChooseOneStep>(providerSelectEvent.Step);

        Assert.Equal(2, selectionStep.Options.Count);
        Assert.Contains(selectionStep.Options, o => o.Value == "anthropic");
        Assert.Contains(selectionStep.Options, o => o.Value == "myext");
    }

    // ─── N2: ChooseMany invalid answer re-prompts ──────────────────────────────────────────

    /// <summary>
    /// An invalid ChooseMany answer (out-of-range index) must NOT unwind the wizard.
    /// Instead an ErrorEvent with Recoverable=true is emitted and the same step is re-emitted.
    /// </summary>
    [Fact]
    public async Task InvalidChooseManyAnswer_RePromptsStep()
    {
        const string wizardId = "wiz-choosemany";
        FakeEventEmitter emitter = new();

        ChooseManyStep chooseManyStep = new()
        {
            Id = "features",
            Prompt = "Select features:",
            Options = [new WizardOption("Feature A", "a"), new WizardOption("Feature B", "b")],
            MinSelections = 0,
        };

        ScriptedFactory factory = MakeFactory(steps: [chooseManyStep]);
        TestablePsh handler = MakeHandler(emitter, [factory]);

        Task wizardTask = handler.StartWizardAsync(MakeStart(wizardId), CancellationToken.None);

        // Provider selection — choose factory at index 0.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "0"));

        // ChooseMany step — send out-of-range index (99).
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "99"), delayMs: 40);

        // ErrorEvent with Recoverable=true should be emitted; step is re-emitted.
        // Give the engine a moment to process.
        await Task.Delay(80);

        // Cancel to exit cleanly.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Cancel), delayMs: 20);

        await wizardTask;

        // An ErrorEvent with Recoverable=true must have been emitted (not an unwind).
        ErrorEvent? errorEvent = emitter.Emitted.OfType<ErrorEvent>()
            .FirstOrDefault(e => e.Code == "wizard.invalidAnswer");
        Assert.NotNull(errorEvent);
        Assert.True(errorEvent.Recoverable);

        // The step must have been re-emitted after the invalid answer.
        // Sequence: provider-select → features → (invalid) → features (re-prompt) → cancel.
        IReadOnlyList<WizardStepEvent> stepEvents = emitter.Emitted.OfType<WizardStepEvent>().ToList();
        Assert.True(stepEvents.Count >= 3, $"Expected at least 3 step events, got {stepEvents.Count}");
        Assert.Equal("features", stepEvents[1].Step.Id); // first features emit
        Assert.Equal("features", stepEvents[2].Step.Id); // re-prompt

        // Wizard must not have persisted anything.
        Assert.Empty(emitter.Emitted.OfType<ProviderConfiguredEvent>());
    }

    // ─── N3: ChooseOne invalid answer re-prompts ────────────────────────────────────────────

    /// <summary>
    /// An invalid ChooseOne answer (out-of-range or unparseable) must NOT unwind the wizard
    /// or dereference a null SelectedIndex. Instead the same step is re-emitted.
    /// </summary>
    [Fact]
    public async Task InvalidChooseOneAnswer_RePromptsStep()
    {
        const string wizardId = "wiz-chooseone";
        FakeEventEmitter emitter = new();

        ChooseOneStep modelStep = new()
        {
            Id = "model",
            Prompt = "Pick model:",
            Options = [new WizardOption("Model A", "model-a")],
        };

        ScriptedFactory factory = MakeFactory(steps: [modelStep]);
        TestablePsh handler = MakeHandler(emitter, [factory]);

        Task wizardTask = handler.StartWizardAsync(MakeStart(wizardId), CancellationToken.None);

        // Provider selection.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "0"));

        // ChooseOne model step — send unparseable value.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "not-a-number"), delayMs: 40);

        // Wait for engine to process.
        await Task.Delay(80);

        // Cancel to exit.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Cancel), delayMs: 20);

        await wizardTask;

        // An ErrorEvent with Recoverable=true must have been emitted.
        ErrorEvent? errorEvent = emitter.Emitted.OfType<ErrorEvent>()
            .FirstOrDefault(e => e.Code == "wizard.invalidAnswer");
        Assert.NotNull(errorEvent);
        Assert.True(errorEvent.Recoverable);

        // Step must have been re-emitted.
        IReadOnlyList<WizardStepEvent> stepEvents = emitter.Emitted.OfType<WizardStepEvent>().ToList();
        Assert.True(stepEvents.Count >= 3, $"Expected at least 3 step events, got {stepEvents.Count}");
        Assert.Equal("model", stepEvents[1].Step.Id);
        Assert.Equal("model", stepEvents[2].Step.Id);

        // No persistence.
        Assert.Empty(emitter.Emitted.OfType<ProviderConfiguredEvent>());
    }

    /// <summary>
    /// An invalid provider-selection ChooseOne answer (out-of-range) must NOT null-deref.
    /// The wizard keeps running and accepts a valid answer on re-prompt.
    /// </summary>
    [Fact]
    public async Task InvalidProviderSelectionAnswer_RePromptsAndAcceptsValidAnswer()
    {
        const string wizardId = "wiz-provsel-invalid";
        FakeEventEmitter emitter = new();
        TrackingProviderRegistry registry = new();
        ScriptedFactory factory = MakeFactory(adapter: "fake", display: "Fake");
        TestablePsh handler = MakeHandler(emitter, [factory], registry);

        Task wizardTask = handler.StartWizardAsync(MakeStart(wizardId), CancellationToken.None);

        // Send out-of-range index to provider-selection (only 1 option, so "5" is invalid).
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "5"));

        // Wait for re-prompt.
        await Task.Delay(60);

        // Now send the correct index.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "0"), delayMs: 20);

        await wizardTask;

        // Wizard completed successfully.
        Assert.Single(emitter.Emitted.OfType<ProviderConfiguredEvent>());

        // ErrorEvent with invalidAnswer was emitted.
        Assert.Contains(emitter.Emitted.OfType<ErrorEvent>(), e => e.Code == "wizard.invalidAnswer");
    }

    // ─── Bug B: WizardCompletedStep must be emitted to host ──────────────────

    /// <summary>
    /// When the factory returns a <see cref="WizardCompletedStep"/>, the engine must emit
    /// a <see cref="WizardStepEvent"/> carrying it to the host BEFORE emitting
    /// <see cref="ProviderConfiguredEvent"/> and persisting.
    /// </summary>
    [Fact]
    public async Task HappyPath_EmitsWizardStepEventForCompletedStep()
    {
        const string wizardId = "wiz-completed-emit";
        FakeEventEmitter emitter = new();
        TrackingProviderRegistry registry = new();

        // No intermediate steps — factory immediately returns WizardCompletedStep.
        ScriptedFactory factory = MakeFactory(adapter: "fake", display: "Fake", steps: []);
        TestablePsh handler = MakeHandler(emitter, [factory], registry);

        Task wizardTask = handler.StartWizardAsync(MakeStart(wizardId), CancellationToken.None);

        // Answer provider selection — factory then returns WizardCompletedStep.
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "0"));

        await wizardTask;

        // A WizardStepEvent carrying WizardCompletedStep must have been emitted.
        IReadOnlyList<WizardStepEvent> stepEvents = emitter.Emitted.OfType<WizardStepEvent>().ToList();
        WizardStepEvent? completedEvent = stepEvents.FirstOrDefault(e => e.Step is WizardCompletedStep);
        Assert.NotNull(completedEvent);
        Assert.Equal(wizardId, completedEvent.WizardId);

        // ProviderConfiguredEvent must still be emitted.
        Assert.Single(emitter.Emitted.OfType<ProviderConfiguredEvent>());
    }

    /// <summary>
    /// The <see cref="WizardStepEvent"/> for <see cref="WizardCompletedStep"/> must arrive
    /// before <see cref="ProviderConfiguredEvent"/> in the emitted sequence.
    /// </summary>
    [Fact]
    public async Task CompletedStepEvent_PrecedesProviderConfiguredEvent()
    {
        const string wizardId = "wiz-order";
        FakeEventEmitter emitter = new();
        TrackingProviderRegistry registry = new();
        ScriptedFactory factory = MakeFactory(adapter: "fake", display: "Fake", steps: []);
        TestablePsh handler = MakeHandler(emitter, [factory], registry);

        Task wizardTask = handler.StartWizardAsync(MakeStart(wizardId), CancellationToken.None);
        await DeliverAsync(handler, MakeAnswer(wizardId, WizardAnswerOutcome.Answered, "0"));
        await wizardTask;

        List<Event> all = emitter.Emitted.ToList();
        int completedIdx = all.FindIndex(e => e is WizardStepEvent ws && ws.Step is WizardCompletedStep);
        int configuredIdx = all.FindIndex(e => e is ProviderConfiguredEvent);

        Assert.True(completedIdx >= 0, "WizardStepEvent(WizardCompletedStep) was not emitted.");
        Assert.True(configuredIdx >= 0, "ProviderConfiguredEvent was not emitted.");
        Assert.True(completedIdx < configuredIdx,
            $"WizardStepEvent(completed) must precede ProviderConfiguredEvent. Indices: {completedIdx} vs {configuredIdx}.");
    }
}
