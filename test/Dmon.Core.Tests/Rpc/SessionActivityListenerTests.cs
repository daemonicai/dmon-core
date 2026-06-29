using Dmon.Abstractions.Hosting;
using Dmon.Core.Extensions;
using Dmon.Core.Rpc;
using Dmon.Core.Tests.Fakes;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Rpc;

// ---------------------------------------------------------------------------
// Shared fakes
// ---------------------------------------------------------------------------

/// <summary>Records each OnSessionActivated / OnTurnStarted call for assertion.</summary>
internal sealed class RecordingActivityListener : ISessionActivityListener
{
    public List<string> ActivatedIds { get; } = [];
    public List<string> TurnStartedIds { get; } = [];

    public void OnSessionActivated(string sessionId) => ActivatedIds.Add(sessionId);
    public void OnTurnStarted(string sessionId) => TurnStartedIds.Add(sessionId);
}

/// <summary>Always throws — used to verify per-listener exception isolation.</summary>
internal sealed class ThrowingActivityListener : ISessionActivityListener
{
    public void OnSessionActivated(string sessionId) => throw new InvalidOperationException("listener boom");
    public void OnTurnStarted(string sessionId) => throw new InvalidOperationException("listener boom");
}

// ---------------------------------------------------------------------------
// SessionHandler tests
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that <see cref="ISessionActivityListener"/> is invoked by
/// <see cref="SessionHandler"/> on session create and load, with correct
/// isolation semantics.
/// </summary>
public sealed class SessionHandlerActivityListenerTests
{
    private static SessionMeta MakeMeta(string id) => new()
    {
        Id       = id,
        Name     = null,
        Created  = DateTimeOffset.UtcNow,
        Modified = DateTimeOffset.UtcNow
    };

    // ── CreateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_WithListener_InvokesOnSessionActivated()
    {
        RecordingActivityListener listener = new();
        FakeSessionStore store = new();
        SessionHandler handler = new(store, new FakeEventEmitter(), NullLogger<SessionHandler>.Instance,
            listeners: [listener]);

        await handler.CreateAsync(new SessionCreateCommand { Id = "cmd-1" }, CancellationToken.None);

        string activatedId = Assert.Single(listener.ActivatedIds);
        Assert.NotEmpty(activatedId);
    }

    [Fact]
    public async Task CreateAsync_ActivatedId_MatchesSessionId()
    {
        RecordingActivityListener listener = new();
        FakeEventEmitter emitter = new();
        FakeSessionStore store = new();
        SessionHandler handler = new(store, emitter, NullLogger<SessionHandler>.Instance,
            listeners: [listener]);

        await handler.CreateAsync(new SessionCreateCommand { Id = "cmd-2" }, CancellationToken.None);

        SessionCreatedResultEvent created = Assert.Single(emitter.Emitted.OfType<SessionCreatedResultEvent>());
        Assert.Equal(created.Session.Id, listener.ActivatedIds[0]);
    }

    [Fact]
    public async Task CreateAsync_WithNoListeners_Succeeds()
    {
        FakeEventEmitter emitter = new();
        FakeSessionStore store = new();
        // No listener passed — must not throw.
        SessionHandler handler = new(store, emitter, NullLogger<SessionHandler>.Instance);

        await handler.CreateAsync(new SessionCreateCommand { Id = "cmd-noop" }, CancellationToken.None);

        Assert.Single(emitter.Emitted.OfType<SessionCreatedResultEvent>());
    }

    [Fact]
    public async Task CreateAsync_WithThrowingListener_CommandSucceeds()
    {
        FakeEventEmitter emitter = new();
        FakeSessionStore store = new();
        SessionHandler handler = new(store, emitter, NullLogger<SessionHandler>.Instance,
            listeners: [new ThrowingActivityListener()]);

        // Must not propagate the listener exception.
        await handler.CreateAsync(new SessionCreateCommand { Id = "cmd-throw" }, CancellationToken.None);

        Assert.Single(emitter.Emitted.OfType<SessionCreatedResultEvent>());
    }

    [Fact]
    public async Task CreateAsync_ThrowingListenerDoesNotStopSubsequentListeners()
    {
        RecordingActivityListener recording = new();
        FakeSessionStore store = new();
        SessionHandler handler = new(store, new FakeEventEmitter(), NullLogger<SessionHandler>.Instance,
            listeners: [new ThrowingActivityListener(), recording]);

        await handler.CreateAsync(new SessionCreateCommand { Id = "cmd-order" }, CancellationToken.None);

        // recording comes after the throwing listener — it must still be invoked.
        Assert.Single(recording.ActivatedIds);
    }

    // ── LoadAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_WithListener_InvokesOnSessionActivated()
    {
        using TempSessionDir tmp = new();
        RecordingActivityListener listener = new();
        FakeSessionStore store = new();
        store.MapDirectory(tmp.SessionId, tmp.Path);
        store.Preset(MakeMeta(tmp.SessionId));

        SessionHandler handler = new(store, new FakeEventEmitter(), NullLogger<SessionHandler>.Instance,
            listeners: [listener]);

        await handler.LoadAsync(new SessionLoadCommand { Id = "load-1", Path = tmp.Path }, CancellationToken.None);

        string activatedId = Assert.Single(listener.ActivatedIds);
        Assert.Equal(tmp.SessionId, activatedId);
    }

    [Fact]
    public async Task LoadAsync_WithThrowingListener_CommandSucceeds()
    {
        using TempSessionDir tmp = new();
        FakeEventEmitter emitter = new();
        FakeSessionStore store = new();
        store.MapDirectory(tmp.SessionId, tmp.Path);
        store.Preset(MakeMeta(tmp.SessionId));

        SessionHandler handler = new(store, emitter, NullLogger<SessionHandler>.Instance,
            listeners: [new ThrowingActivityListener()]);

        await handler.LoadAsync(new SessionLoadCommand { Id = "load-throw", Path = tmp.Path }, CancellationToken.None);

        Assert.Single(emitter.Emitted.OfType<SessionLoadedResultEvent>());
    }

    [Fact]
    public async Task LoadAsync_LockedSession_DoesNotFireListener()
    {
        // Lock failure path returns before setting _currentSession — listeners must not fire.
        using TempSessionDir tmp = new();
        RecordingActivityListener listener = new();
        FakeSessionStore store = new();
        store.MapDirectory(tmp.SessionId, tmp.Path);
        store.Preset(MakeMeta(tmp.SessionId));

        using FileStream externalLock = new(
            Path.Combine(tmp.Path, ".lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);

        SessionHandler handler = new(store, new FakeEventEmitter(), NullLogger<SessionHandler>.Instance,
            listeners: [listener]);

        await handler.LoadAsync(new SessionLoadCommand { Id = "load-locked", Path = tmp.Path }, CancellationToken.None);

        Assert.Empty(listener.ActivatedIds);
    }
}

// ---------------------------------------------------------------------------
// TurnHandler tests
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that <see cref="ISessionActivityListener"/> is invoked by
/// <see cref="TurnHandler"/> at turn start, with correct isolation semantics.
/// </summary>
public sealed class TurnHandlerActivityListenerTests
{
    private static TurnHandler BuildHandler(
        ISessionActivityListener? listener = null,
        string sessionId = "turn-session-1")
    {
        IEnumerable<ISessionActivityListener> listeners = listener is null
            ? []
            : [listener];

        StubChatClient client = new("response");
        StubProviderRegistry providers = new(client);
        IConfiguration configuration = new ConfigurationBuilder().Build();
        MiddlewarePipelineBuilder pipelineBuilder = new(new MiddlewareRegistry(), configuration);

        return new TurnHandler(
            providers,
            new NoopActiveModelStore(),
            new EmptyToolRegistry(),
            new TestEventEmitter(),
            new PermitAllPolicy(),
            new NoopThinkingHandler(),
            new ActiveSessionHandler(sessionId),
            new StubSystemPromptBuilder(),
            pipelineBuilder,
            configuration,
            new NoopSessionAssetProvisioner(),
            NullLogger<TurnHandler>.Instance,
            activityListeners: listeners);
    }

    [Fact]
    public async Task Submit_WithListener_InvokesOnTurnStarted()
    {
        RecordingActivityListener listener = new();
        TurnHandler handler = BuildHandler(listener);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "t1", Message = "hello" }, CancellationToken.None);

        Assert.Equal(["turn-session-1"], listener.TurnStartedIds);
    }

    [Fact]
    public async Task Submit_WithNoListeners_Succeeds()
    {
        // Zero listeners — must not throw.
        TurnHandler handler = BuildHandler(listener: null);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "t-noop", Message = "hello" }, CancellationToken.None);
    }

    [Fact]
    public async Task Submit_WithThrowingListener_TurnCompletes()
    {
        TestEventEmitter emitter = new();

        // Build a handler that shares the same emitter so we can observe TurnEndEvent.
        StubChatClient client = new("ok");
        StubProviderRegistry providers = new(client);
        IConfiguration cfg = new ConfigurationBuilder().Build();
        MiddlewarePipelineBuilder pipeline = new(new MiddlewareRegistry(), cfg);

        TurnHandler handlerWithEmitter = new(
            providers,
            new NoopActiveModelStore(),
            new EmptyToolRegistry(),
            emitter,
            new PermitAllPolicy(),
            new NoopThinkingHandler(),
            new ActiveSessionHandler("turn-throw-1"),
            new StubSystemPromptBuilder(),
            pipeline,
            cfg,
            new NoopSessionAssetProvisioner(),
            NullLogger<TurnHandler>.Instance,
            activityListeners: [new ThrowingActivityListener()]);

        // Must not propagate the listener exception — turn must complete normally.
        await handlerWithEmitter.SubmitAsync(new TurnSubmitCommand { Id = "t-throw", Message = "hello" }, CancellationToken.None);

        Assert.Contains(emitter.Events, e => e is TurnEndEvent);
    }

    [Fact]
    public async Task Submit_MultipleListeners_ThrowingOneDoesNotStopOthers()
    {
        RecordingActivityListener recording = new();
        StubChatClient client = new("ok");
        StubProviderRegistry providers = new(client);
        IConfiguration cfg = new ConfigurationBuilder().Build();
        MiddlewarePipelineBuilder pipeline = new(new MiddlewareRegistry(), cfg);

        TurnHandler handler = new(
            providers,
            new NoopActiveModelStore(),
            new EmptyToolRegistry(),
            new TestEventEmitter(),
            new PermitAllPolicy(),
            new NoopThinkingHandler(),
            new ActiveSessionHandler("turn-multi-1"),
            new StubSystemPromptBuilder(),
            pipeline,
            cfg,
            new NoopSessionAssetProvisioner(),
            NullLogger<TurnHandler>.Instance,
            activityListeners: [new ThrowingActivityListener(), recording]);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "t-multi", Message = "hello" }, CancellationToken.None);

        Assert.Equal(["turn-multi-1"], recording.TurnStartedIds);
    }

    [Fact]
    public async Task Submit_TurnInProgress_DoesNotFireListenerForRejectedTurn()
    {
        RecordingActivityListener listener = new();
        // TCS completed by GateSignalListener when the first turn's OnTurnStarted fires —
        // guarantees the gate is held before the second submit attempt.
        TaskCompletionSource gateAcquired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SlowStubChatClient slowClient = new(TimeSpan.FromSeconds(5));
        StubProviderRegistry providers = new(slowClient);
        IConfiguration cfg = new ConfigurationBuilder().Build();
        MiddlewarePipelineBuilder pipeline = new(new MiddlewareRegistry(), cfg);
        TestEventEmitter emitter = new();

        TurnHandler handler = new(
            providers,
            new NoopActiveModelStore(),
            new EmptyToolRegistry(),
            emitter,
            new PermitAllPolicy(),
            new NoopThinkingHandler(),
            new ActiveSessionHandler("turn-gate-1"),
            new StubSystemPromptBuilder(),
            pipeline,
            cfg,
            new NoopSessionAssetProvisioner(),
            NullLogger<TurnHandler>.Instance,
            activityListeners: [new GateSignalListener(gateAcquired), listener]);

        using CancellationTokenSource cts = new();

        Task firstTurn = Task.Run(async () =>
        {
            try { await handler.SubmitAsync(new TurnSubmitCommand { Id = "t-first", Message = "slow" }, cts.Token); }
            catch (OperationCanceledException) { }
        });

        // Wait until the first turn has actually acquired the gate (listener fired).
        await gateAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Second submit: rejected because gate is held — must NOT fire listener again.
        await handler.SubmitAsync(new TurnSubmitCommand { Id = "t-second", Message = "second" }, CancellationToken.None);

        await cts.CancelAsync();
        await firstTurn;

        // Only the first turn fires OnTurnStarted; the rejected second does not.
        Assert.Single(listener.TurnStartedIds);
    }
}

/// <summary>
/// Signals a <see cref="TaskCompletionSource"/> on the first <see cref="OnTurnStarted"/>
/// call. Used to synchronise tests that need to know the turn gate is held.
/// </summary>
internal sealed class GateSignalListener(TaskCompletionSource tcs) : ISessionActivityListener
{
    public void OnSessionActivated(string sessionId) { }
    public void OnTurnStarted(string sessionId) => tcs.TrySetResult();
}
