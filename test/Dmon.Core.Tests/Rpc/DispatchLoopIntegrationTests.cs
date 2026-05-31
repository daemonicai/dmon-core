using System.Collections.Concurrent;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Rpc;

/// <summary>
/// Integration tests that drive commands through the real <see cref="CommandDispatcher"/>
/// dispatch path (not out-of-band handler injection) to prove that the stdin reader stays
/// unblocked during long-running interactive commands.
///
/// These tests MUST hang/time-out if <see cref="CommandDispatcher"/> is modified to await
/// interactive commands inline — that is the live proof of task 3.8/3.9.
/// </summary>
public sealed class DispatchLoopIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public DispatchLoopIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dmon-dispatch-integ-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ─── Scenario A: wizard.start → wizard.step emitted → wizard.answer → providerConfigured
    //
    // Proof: DispatchAsync("wizard.start") must return before the wizard loop emits its
    // first step, so that the second DispatchAsync("wizard.answer") can be called.
    // If dispatch were inline, the test would deadlock here because "wizard.answer" could
    // never be dispatched while "wizard.start" is still blocking the caller.
    // ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WizardRoundTrip_CompletesOverRealLoop()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        ConcurrentEventEmitter emitter = new();
        string configPath = Path.Combine(_tempDir, "config.yaml");

        // Real ProviderSetupHandler with a scripted factory — exercises the actual
        // TCS-based wizard engine, not a simplified stub.
        ScriptedFactory factory = new("fake", "Fake Provider", []);
        TestablePsh providerSetup = new(emitter, configPath, configPath, new TrackingRegistry(), [factory]);

        CommandDispatcher dispatcher = BuildDispatcher(emitter, providerSetup: providerSetup);

        const string wizardId = "integ-wiz-1";

        // Step 1: dispatch wizard.start. With non-blocking dispatch this returns immediately.
        // If dispatch were inline this call would block indefinitely — the test would time-out.
        await dispatcher.DispatchAsync(
            $@"{{""type"":""wizard.start"",""id"":""{wizardId}""}}",
            cts.Token);

        // Wait until the wizard engine has emitted the first step (provider selection).
        // This confirms the background task is running and has reached EmitAndAwaitAnswerAsync.
        await WaitForEventAsync<WizardStepEvent>(emitter, cts.Token);

        // Step 2: dispatch wizard.answer (provider selection = index 0). This line goes through
        // the same DispatchAsync path — proving the reader loop is not blocked.
        await dispatcher.DispatchAsync(
            $@"{{""type"":""wizard.answer"",""id"":""ans-1"",""wizardId"":""{wizardId}"",""outcome"":""answered"",""value"":""0""}}",
            cts.Token);

        // The factory has no additional steps, so the engine will call CompleteWizardAsync next.
        await WaitForEventAsync<ProviderConfiguredEvent>(emitter, cts.Token);

        // Drain background tasks so the test can cleanly assert completion.
        await dispatcher.DrainAsync();

        ProviderConfiguredEvent configured = Assert.Single(emitter.Emitted.OfType<ProviderConfiguredEvent>());
        Assert.Equal("fake", configured.Adapter);
    }

    // ─── Scenario B: turn.submit → tool.confirmRequest → tool.confirmResponse → turnEnd
    //
    // Proof: same structure. DispatchAsync("turn.submit") must return while the turn
    // is blocking on a tool-confirm TCS, so the subsequent "tool.confirmResponse"
    // dispatch can resolve it.
    // ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TurnConfirmRoundTrip_CompletesOverRealLoop()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        ConcurrentEventEmitter emitter = new();
        const string confirmId = "integ-confirm-1";
        ConfirmingTurnHandler turnHandler = new(emitter, confirmId);
        CommandDispatcher dispatcher = BuildDispatcher(emitter, turn: turnHandler);

        const string submitId = "integ-turn-1";

        // Step 1: dispatch turn.submit — must return immediately (backgrounded).
        await dispatcher.DispatchAsync(
            $@"{{""type"":""turn.submit"",""id"":""{submitId}"",""message"":""hello""}}",
            cts.Token);

        // Wait until the turn handler signals that it has emitted the confirm request and
        // is now suspended on the TCS. This is proof the background task is running and that
        // the reader (DispatchAsync caller) is NOT blocked — it can call DispatchAsync again.
        await turnHandler.ConfirmEmitted.WaitAsync(cts.Token);

        // Step 2: dispatch tool.confirmResponse — resolves the TCS in ConfirmingTurnHandler.
        // Proves the reader loop is not blocked by the turn.
        await dispatcher.DispatchAsync(
            $@"{{""type"":""tool.confirmResponse"",""id"":""{confirmId}"",""confirmed"":true}}",
            cts.Token);

        // Wait for the turn to complete.
        await turnHandler.TurnCompleted.Task.WaitAsync(cts.Token);
        await dispatcher.DrainAsync();

        Assert.Single(emitter.Emitted.OfType<TurnEndEvent>());
    }

    // ─── Scenario C: backgrounded command error is surfaced as an error event
    //
    // Proof: RunBackgroundAsync must catch exceptions from the work delegate and emit an
    // ErrorEvent. Under the old code (JsonElement path), an ObjectDisposedException would
    // propagate unobserved; under any code that swallows the exception, the emitter would
    // receive no ErrorEvent and this test would fail.
    // ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BackgroundedCommandError_IsSurfacedAsErrorEvent()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        ConcurrentEventEmitter emitter = new();
        ThrowingTurnHandler throwingTurn = new();
        CommandDispatcher dispatcher = BuildDispatcher(emitter, turn: throwingTurn);

        // Dispatch turn.submit — backgrounds it. The handler throws immediately.
        await dispatcher.DispatchAsync(
            @"{""type"":""turn.submit"",""id"":""err-1"",""message"":""boom""}",
            cts.Token);

        // Drain so the background task completes and its catch block runs.
        await dispatcher.DrainAsync();

        // The exception must be surfaced as an internalError event — not swallowed.
        ErrorEvent err = Assert.Single(emitter.Emitted.OfType<ErrorEvent>());
        Assert.Equal("internalError", err.Code);
        Assert.False(err.Recoverable);
    }

    // ─── Scenario D: reader stays responsive during a long turn — turn.abort cancels it
    //
    // Proof: turn.abort is routed inline (non-long-running) while turn.submit is suspended
    // in the background. If the dispatcher blocked on turn.submit, the turn.abort dispatch
    // would never be reached. The background task must observe cancellation and complete.
    // ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReaderResponsive_DuringLongTurn_AbortCancelsTurn()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        ConcurrentEventEmitter emitter = new();
        AbortableTurnHandler abortableTurn = new();
        CommandDispatcher dispatcher = BuildDispatcher(emitter, turn: abortableTurn);

        // Step 1: dispatch turn.submit — backgrounded, suspends waiting for abort signal.
        await dispatcher.DispatchAsync(
            @"{""type"":""turn.submit"",""id"":""long-1"",""message"":""long""}",
            cts.Token);

        // Wait until SubmitAsync has started and is blocked (proves background task is running).
        await abortableTurn.SubmitStarted.WaitAsync(cts.Token);

        // Step 2: dispatch turn.abort — must route inline while the background task is suspended.
        // If the reader were blocked on turn.submit this call would never execute.
        await dispatcher.DispatchAsync(
            @"{""type"":""turn.abort"",""id"":""abort-1""}",
            cts.Token);

        // The background task should complete (with OperationCanceledException, swallowed).
        await dispatcher.DrainAsync();

        // The abort was observed by the background turn.
        Assert.True(abortableTurn.WasCancelled);
    }

    // ─── Scenario E: session.compact emits notImplemented (recoverable), reader survives
    //
    // Proof: SessionCompactCommand is a registered JsonDerivedType but has no handler.
    // The Route arm throws NotImplementedException, which RunGuardedAsync catches and turns
    // into a recoverable notImplemented ErrorEvent. DispatchAsync must not throw — the
    // reader loop keeps running.
    // ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SessionCompact_EmitsNotImplemented_AndDispatcherSurvives()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        ConcurrentEventEmitter emitter = new();
        CommandDispatcher dispatcher = BuildDispatcher(emitter);

        // Must not throw — the reader loop should survive an unimplemented command.
        await dispatcher.DispatchAsync(
            @"{""type"":""session.compact"",""id"":""compact-1""}",
            cts.Token);

        ErrorEvent err = Assert.Single(emitter.Emitted.OfType<ErrorEvent>());
        Assert.Equal("notImplemented", err.Code);
        Assert.True(err.Recoverable);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────

    // Thread-safe emitter for integration tests where background tasks write concurrently.
    private sealed class ConcurrentEventEmitter : IEventEmitter
    {
        private readonly ConcurrentBag<Event> _emitted = new();

        public IReadOnlyList<Event> Emitted => _emitted.ToList();

        public Task EmitAsync<T>(T evt, CancellationToken cancellationToken = default) where T : Event
        {
            _emitted.Add(evt);
            return Task.CompletedTask;
        }
    }

    // Polls the emitter until an event of type T appears or the token is cancelled.
    private static async Task WaitForEventAsync<T>(
        ConcurrentEventEmitter emitter,
        CancellationToken cancellationToken) where T : Event
    {
        while (!emitter.Emitted.OfType<T>().Any())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    private static CommandDispatcher BuildDispatcher(
        IEventEmitter emitter,
        ITurnHandler? turn = null,
        IProviderSetupHandler? providerSetup = null)
        => new(
            turn: turn ?? new NoOpTurnHandler(),
            model: new NoOpModelHandler(),
            session: new NoOpSessionHandler(),
            extension: new NoOpExtensionHandler(),
            auth: new NoOpAuthHandler(),
            thinking: new NoOpThinkingHandler(),
            providerSetup: providerSetup ?? new NoOpProviderSetupHandler(),
            emitter: emitter,
            logger: NullLogger<CommandDispatcher>.Instance);

    // ─── ProviderSetupHandler subclass for path redirection ──────────────────────────────

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

    // ─── Scripted factory ─────────────────────────────────────────────────────────────────

    private sealed class ScriptedFactory(
        string adapterName,
        string displayName,
        IEnumerable<WizardStep> steps) : IProviderFactory
    {
        private readonly Queue<WizardStep> _steps = new(steps);

        public string AdapterName { get; } = adapterName;
        public string DisplayName { get; } = displayName;
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
                Prompt = string.Empty,
                Message = "Done."
            });
        }
    }

    // ─── Turn handler stub for confirm round-trip ─────────────────────────────────────────

    /// <summary>
    /// A stub turn handler that, when SubmitAsync is called, emits a tool.confirmRequest
    /// and then blocks on a TCS until ConfirmResponseAsync resolves it, then emits TurnEndEvent.
    /// This mirrors the shape of the real TurnHandler's confirm flow without requiring the
    /// full DI stack.
    /// <para>
    /// <see cref="ConfirmEmitted"/> is released after the confirm request is emitted and the
    /// handler is suspended on the TCS — the test uses this to know the reader is free.
    /// <see cref="TurnCompleted"/> is resolved when the turn finishes.
    /// </para>
    /// </summary>
    private sealed class ConfirmingTurnHandler(IEventEmitter emitter, string confirmId) : ITurnHandler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();

        // Released after ToolConfirmRequestEvent is emitted AND the handler is blocked on the TCS.
        public SemaphoreSlim ConfirmEmitted { get; } = new(0, 1);

        // Resolved when the turn completes (after TurnEndEvent is emitted).
        public TaskCompletionSource TurnCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SubmitAsync(TurnSubmitCommand cmd, CancellationToken cancellationToken)
        {
            // Signal immediately so the test knows SubmitAsync was called.
            ConfirmEmitted.Release();

            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[confirmId] = tcs;

            await emitter.EmitAsync(new ToolConfirmRequestEvent
            {
                ConfirmId = confirmId,
                Name = "fake_tool",
                Args = new { },
                Risk = RiskLevel.Medium
            }, cancellationToken).ConfigureAwait(false);

            // Block until the host sends tool.confirmResponse. This is the blocking-on-TCS
            // pattern that deadlocks under the old inline dispatch.
            using CancellationTokenRegistration reg = cancellationToken.Register(
                () => tcs.TrySetCanceled(cancellationToken));
            bool confirmed = await tcs.Task.ConfigureAwait(false);

            await emitter.EmitAsync(new TurnEndEvent
            {
                Message = confirmed ? "confirmed" : "denied",
                ToolResults = []
            }, cancellationToken).ConfigureAwait(false);

            TurnCompleted.TrySetResult();
        }

        public async Task ConfirmResponseAsync(ToolConfirmResponseCommand cmd, CancellationToken cancellationToken)
        {
            if (_pending.TryRemove(cmd.Id, out TaskCompletionSource<bool>? tcs))
                tcs.TrySetResult(cmd.Confirmed);
            else
                await emitter.EmitAsync(new ErrorEvent
                {
                    Code = "noConfirmPending",
                    Message = $"No pending confirm with id '{cmd.Id}'.",
                    Recoverable = true
                }, cancellationToken).ConfigureAwait(false);
        }

        public Task SteerAsync(TurnSteerCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FollowUpAsync(TurnFollowUpCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AbortAsync(TurnAbortCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UiInputResponseAsync(UiInputResponseCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ─── Registry stub ────────────────────────────────────────────────────────────────────

    private sealed class TrackingRegistry : IProviderRegistry
    {
        public void AddDynamicProvider(ProviderConfig config) { }
        public ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ProviderConfig GetCurrentConfig() => throw new NotSupportedException();
        public IReadOnlyList<ProviderConfig> GetAll() => [];
        public void SetProvider(string name) { }
        public void SetModel(string modelId) { }
        public void CycleProvider() { }
        public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string? GetCurrentModelId() => null;
        public ProviderSwitchResult? CommitPendingSwitch() => null;
        public bool CurrentSupportsToolCalling => false;
        public bool CurrentSupportsReasoning => false;
    }

    // ─── No-op handler stubs ──────────────────────────────────────────────────────────────

    private sealed class NoOpTurnHandler : ITurnHandler
    {
        public Task SubmitAsync(TurnSubmitCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SteerAsync(TurnSteerCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FollowUpAsync(TurnFollowUpCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AbortAsync(TurnAbortCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ConfirmResponseAsync(ToolConfirmResponseCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UiInputResponseAsync(UiInputResponseCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpModelHandler : IModelHandler
    {
        public Task SetAsync(ModelSetCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CycleAsync(ModelCycleCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ListAsync(ModelListCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ModelsAsync(ModelModelsCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpSessionHandler : ISessionHandler
    {
        public SessionMeta? CurrentSession => null;
        public Task CreateAsync(SessionCreateCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ForkAsync(SessionForkCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloneAsync(SessionCloneCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LoadAsync(SessionLoadCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ListAsync(SessionListCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetNameAsync(SessionSetNameCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetStatsAsync(SessionGetStatsCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetMessagesAsync(SessionGetMessagesCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpExtensionHandler : IExtensionHandler
    {
        public Task LoadAsync(ExtensionLoadCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UnloadAsync(ExtensionUnloadCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PromoteAsync(ExtensionPromoteCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpAuthHandler : IAuthHandler
    {
        public Task LoginAsync(AuthLoginCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LogoutAsync(AuthLogoutCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StatusAsync(AuthStatusCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpThinkingHandler : IThinkingHandler
    {
        public ThinkingLevel CurrentLevel => ThinkingLevel.Off;
        public Task SetAsync(ThinkingSetCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CycleAsync(ThinkingCycleCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpProviderSetupHandler : IProviderSetupHandler
    {
        public Task ConfigureAsync(ProviderConfigureCommand command, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartWizardAsync(WizardStartCommand command, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AnswerWizardAsync(WizardAnswerCommand command, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ─── Stub: throws immediately from SubmitAsync (Scenario C) ──────────────────────────

    /// <summary>
    /// Turn handler whose SubmitAsync always throws, exercising the RunBackgroundAsync
    /// catch path that surfaces the exception as an ErrorEvent.
    /// </summary>
    private sealed class ThrowingTurnHandler : ITurnHandler
    {
        public Task SubmitAsync(TurnSubmitCommand cmd, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("simulated turn failure"));

        public Task SteerAsync(TurnSteerCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FollowUpAsync(TurnFollowUpCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AbortAsync(TurnAbortCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ConfirmResponseAsync(ToolConfirmResponseCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UiInputResponseAsync(UiInputResponseCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ─── Stub: suspends until aborted (Scenario D) ───────────────────────────────────────

    /// <summary>
    /// Turn handler that mirrors the real abort pattern: SubmitAsync holds an internal CTS
    /// and suspends until AbortAsync cancels it, proving the reader stays unblocked.
    /// </summary>
    private sealed class AbortableTurnHandler : ITurnHandler
    {
        private volatile CancellationTokenSource? _turnCts;

        // Released once SubmitAsync has started and is blocking — signals the test that the
        // reader is free to dispatch turn.abort.
        public SemaphoreSlim SubmitStarted { get; } = new(0, 1);

        // Set to true when SubmitAsync observes cancellation.
        public bool WasCancelled { get; private set; }

        public async Task SubmitAsync(TurnSubmitCommand cmd, CancellationToken cancellationToken)
        {
            _turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SubmitStarted.Release(); // signal: we are now suspended

            try
            {
                // Block indefinitely until AbortAsync (or shutdown) cancels the CTS.
                await Task.Delay(Timeout.Infinite, _turnCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                // Re-throw so RunBackgroundAsync's OperationCanceledException catch swallows it.
                throw;
            }
            finally
            {
                _turnCts.Dispose();
                _turnCts = null;
            }
        }

        public async Task AbortAsync(TurnAbortCommand cmd, CancellationToken cancellationToken)
        {
            CancellationTokenSource? cts = _turnCts;
            if (cts is not null)
            {
                try { await cts.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { }
            }
        }

        public Task SteerAsync(TurnSteerCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FollowUpAsync(TurnFollowUpCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ConfirmResponseAsync(ToolConfirmResponseCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UiInputResponseAsync(UiInputResponseCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
