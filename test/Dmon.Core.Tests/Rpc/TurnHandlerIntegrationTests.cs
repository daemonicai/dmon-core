using System.Runtime.CompilerServices;
using Dmon.Abstractions;
using Dmon.Abstractions.Profiles;
using Dmon.Abstractions.Providers;
using Dmon.Core.Extensions;
using Dmon.Extensions;
using Dmon.Core.Permissions;
using Dmon.Core.Profiles;
using Dmon.Core.Session;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Permissions;
using Dmon.Protocol.Sessions;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Rpc;

// ---------------------------------------------------------------------------
// Test infrastructure
// ---------------------------------------------------------------------------

/// <summary>
/// Captures emitted events in-memory instead of writing to stdout.
/// </summary>
internal sealed class TestEventEmitter : IEventEmitter
{
    private readonly List<Event> _events = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<Event> Events => _events;

    public async Task EmitAsync<T>(T evt, CancellationToken cancellationToken = default) where T : Event
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _events.Add(evt); }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// Returns a single text response immediately via streaming.
/// </summary>
internal sealed class StubChatClient : IChatClient
{
    private readonly string _text;

    public StubChatClient(string text = "Hello from stub.")
    {
        _text = text;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = new([new ChatMessage(ChatRole.Assistant, _text)]);
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, _text);
    }

    public void Dispose() { }
}

/// <summary>
/// Delays each call to simulate a slow provider.
/// </summary>
internal sealed class SlowStubChatClient : IChatClient
{
    private readonly TimeSpan _delay;

    public SlowStubChatClient(TimeSpan delay)
    {
        _delay = delay;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, "slow response")]);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "slow response");
    }

    public void Dispose() { }
}

/// <summary>
/// Minimal IProviderRegistry backed by a fixed IChatClient.
/// </summary>
internal sealed class StubProviderRegistry : IProviderRegistry
{
    private readonly IChatClient _client;

    public StubProviderRegistry(IChatClient client)
    {
        _client = client;
    }

    public ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_client);

    public ProviderConfig GetCurrentConfig() => new()
    {
        Name = "stub",
        Adapter = "stub",
        Auth = new ProviderAuthConfig { Type = "none" }
    };

    public IReadOnlyList<ProviderConfig> GetAll() => [GetCurrentConfig()];

    public void SetProvider(string name) { }

    public void SetModel(string modelId) { }

    public void CycleProvider() { }

    public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public void AddDynamicProvider(ProviderConfig config) { }

    public string? GetCurrentModelId() => null;

    public ProviderSwitchResult? CommitPendingSwitch() => null;

    public bool CurrentSupportsToolCalling => false;

    public bool CurrentSupportsReasoning => false;
}

/// <summary>
/// IToolRegistry with no registered tools.
/// </summary>
internal sealed class EmptyToolRegistry : IToolRegistry
{
    public IReadOnlyList<AIFunction> GetAll() => [];
    public void Register(string extensionName, IDmonExtension extension, IEnumerable<AIFunction> tools) { }
    public IDmonExtension? FindExtension(string toolName) => null;
    public void Unregister(string extensionName) { }
    public IReadOnlyList<RegisteredExtensionSnapshot> GetSnapshot() => [];
    public void Clear() { }
}

/// <summary>
/// IPermissionPolicy that auto-allows everything.
/// </summary>
internal sealed class PermitAllPolicy : IPermissionPolicy
{
    public IPermissionSettings ProjectSettings { get; } = new StubPermissionSettings();
    public IPermissionSettings? GlobalSettings => null;

    private sealed class StubPermissionSettings : IPermissionSettings
    {
        public PermissionSettings Settings => new();
        public Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

/// <summary>
/// IThinkingHandler always at Off.
/// </summary>
internal sealed class NoopThinkingHandler : IThinkingHandler
{
    public ThinkingLevel CurrentLevel => ThinkingLevel.Off;

    public Task SetAsync(ThinkingSetCommand cmd, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task CycleAsync(ThinkingCycleCommand cmd, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// ISessionHandler stub that always returns a null current session.
/// </summary>
internal sealed class StubSessionHandler : ISessionHandler
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

/// <summary>
/// IAttachmentStore stub that never offloads (always returns null).
/// </summary>
internal sealed class StubAttachmentStore : IAttachmentStore
{
    public Task<string?> StoreIfLargeAsync(
        string sessionId,
        string callId,
        string content,
        string extension = "txt",
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}

// ---------------------------------------------------------------------------
// Factory
// ---------------------------------------------------------------------------

internal sealed class StubSystemPromptBuilder : ISystemPromptBuilder
{
    public Task<ChatMessage> BuildAsync(CancellationToken cancellationToken)
        => Task.FromResult(new ChatMessage(ChatRole.System, "You are a test assistant."));
}

internal sealed class StubAgentProfileResolver : IAgentProfileResolver
{
    public Task<AgentProfile> ResolveAsync(string? requestedProfile, CancellationToken cancellationToken)
        => Task.FromResult(BuiltInProfiles.Coding);
}

internal static class TurnHandlerFactory
{
    public static (TurnHandler handler, TestEventEmitter emitter) Create(
        IChatClient client,
        IActiveModelStore? store = null)
    {
        TestEventEmitter emitter = new();
        StubProviderRegistry providers = new(client);
        return Create(providers, emitter, store);
    }

    public static (TurnHandler handler, TestEventEmitter emitter) Create(
        IProviderRegistry providers,
        TestEventEmitter? emitter = null,
        IActiveModelStore? store = null)
    {
        emitter ??= new TestEventEmitter();
        EmptyToolRegistry tools = new();
        PermitAllPolicy policy = new();
        NoopThinkingHandler thinking = new();
        StubSessionHandler sessionHandler = new();
        StubAttachmentStore attachmentStore = new();
        StubSystemPromptBuilder systemPromptBuilder = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        store ??= new NoopActiveModelStore();

        MiddlewarePipelineBuilder pipelineBuilder = new(new MiddlewareRegistry(), configuration);

        TurnHandler handler = new(
            providers,
            store,
            tools,
            emitter,
            policy,
            thinking,
            sessionHandler,
            attachmentStore,
            systemPromptBuilder,
            pipelineBuilder,
            configuration,
            new StubAgentProfileResolver(),
            new AgentProfileContext(),
            new NoopSessionAssetProvisioner(),
            NullLogger<TurnHandler>.Instance);

        return (handler, emitter);
    }
}

/// <summary>
/// IActiveModelStore that discards all saves and returns null on load.
/// </summary>
internal sealed class NoopActiveModelStore : IActiveModelStore
{
    public ModelRef? Load() => null;

    public Task SaveAsync(ModelRef selection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// IActiveModelStore that captures the last saved selection for assertion.
/// </summary>
internal sealed class CapturingActiveModelStore : IActiveModelStore
{
    public ModelRef? LastSaved { get; private set; }
    public int SaveCount { get; private set; }

    public ModelRef? Load() => null;

    public Task SaveAsync(ModelRef selection, CancellationToken cancellationToken = default)
    {
        LastSaved = selection;
        SaveCount++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// IProviderRegistry with two clients — starts on <c>first</c>, returns
/// <c>second</c> once a pending switch is committed. Records which client
/// was resolved by <see cref="GetCurrentAsync"/>.
/// </summary>
internal sealed class SwitchableStubProviderRegistry : IProviderRegistry
{
    private readonly IChatClient _first;
    private readonly IChatClient _second;
    private readonly string _secondName;
    private readonly string _secondModel;

    private bool _switchPending;
    private bool _switched;

    public IChatClient? LastResolved { get; private set; }

    public SwitchableStubProviderRegistry(
        IChatClient first,
        IChatClient second,
        string secondName = "second-provider",
        string secondModel = "second-model")
    {
        _first = first;
        _second = second;
        _secondName = secondName;
        _secondModel = secondModel;
    }

    public void QueueSwitch() => _switchPending = true;

    public ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        IChatClient resolved = _switched ? _second : _first;
        LastResolved = resolved;
        return ValueTask.FromResult(resolved);
    }

    public ProviderConfig GetCurrentConfig() => _switched
        ? new ProviderConfig { Name = _secondName, Adapter = "stub", Auth = new ProviderAuthConfig { Type = "none" } }
        : new ProviderConfig { Name = "first-provider", Adapter = "stub", Auth = new ProviderAuthConfig { Type = "none" } };

    public IReadOnlyList<ProviderConfig> GetAll() => [GetCurrentConfig()];

    public void SetProvider(string name) => _switchPending = true;

    public void SetModel(string modelId) { }

    public void CycleProvider() { }

    public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public void AddDynamicProvider(ProviderConfig config) { }

    public string? GetCurrentModelId() => _switched ? _secondModel : null;

    public ProviderSwitchResult? CommitPendingSwitch()
    {
        if (!_switchPending)
            return null;
        _switchPending = false;
        _switched = true;
        return new ProviderSwitchResult(_secondName, _secondModel);
    }

    public bool CurrentSupportsToolCalling => false;

    public bool CurrentSupportsReasoning => false;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class TurnHandlerIntegrationTests
{
    [Fact]
    public async Task Submit_EmitsTurnStartAndTurnEnd()
    {
        StubChatClient client = new("Test response.");
        (TurnHandler handler, TestEventEmitter emitter) = TurnHandlerFactory.Create(client);

        TurnSubmitCommand cmd = new() { Id = "req-1", Message = "Hello" };
        await handler.SubmitAsync(cmd, CancellationToken.None);

        Assert.Contains(emitter.Events, e => e is TurnStartEvent);
        Assert.Contains(emitter.Events, e => e is TurnEndEvent);
    }

    [Fact]
    public async Task Submit_WhileTurnInProgress_EmitsError()
    {
        SlowStubChatClient client = new(TimeSpan.FromSeconds(5));
        (TurnHandler handler, TestEventEmitter emitter) = TurnHandlerFactory.Create(client);

        TurnSubmitCommand cmd = new() { Id = "req-1", Message = "First" };
        TurnSubmitCommand cmd2 = new() { Id = "req-2", Message = "Second" };

        using CancellationTokenSource cts = new();

        Task firstTurn = Task.Run(async () =>
        {
            try { await handler.SubmitAsync(cmd, cts.Token); }
            catch (OperationCanceledException) { }
        });

        // Give the first turn time to acquire the gate.
        await Task.Delay(50);

        // The second submit should fail immediately with turnInProgress.
        await handler.SubmitAsync(cmd2, CancellationToken.None);

        await cts.CancelAsync();
        await firstTurn;

        ErrorEvent? error = emitter.Events.OfType<ErrorEvent>()
            .FirstOrDefault(e => e.Code == "turnInProgress");

        Assert.NotNull(error);
    }

    [Fact]
    public async Task Abort_CancelsActiveTurn()
    {
        SlowStubChatClient client = new(TimeSpan.FromSeconds(10));
        (TurnHandler handler, TestEventEmitter emitter) = TurnHandlerFactory.Create(client);

        TurnSubmitCommand submit = new() { Id = "req-1", Message = "Long request" };
        TurnAbortCommand abort = new() { Id = "req-abort" };

        Task turnTask = Task.Run(() => handler.SubmitAsync(submit, CancellationToken.None));

        // Give the turn time to start.
        await Task.Delay(100);

        await handler.AbortAsync(abort, CancellationToken.None);

        // The turn should complete (cancelled) promptly.
        await turnTask.WaitAsync(TimeSpan.FromSeconds(3));

        // TurnEnd is emitted even after cancellation — the pipeline catches OperationCanceledException.
        Assert.Contains(emitter.Events, e => e is TurnEndEvent);
    }

    [Fact]
    public async Task Submit_StreamsTextDeltaEvents()
    {
        StubChatClient client = new("Hello world");
        (TurnHandler handler, TestEventEmitter emitter) = TurnHandlerFactory.Create(client);

        TurnSubmitCommand cmd = new() { Id = "req-1", Message = "Hi" };
        await handler.SubmitAsync(cmd, CancellationToken.None);

        Assert.Contains(emitter.Events, e => e is MessageDeltaEvent);
    }

    [Fact]
    public async Task Submit_CommitsPendingSwitchBeforeResolvingClient()
    {
        // Arrange: registry has a pending switch to second-provider queued before the turn starts.
        StubChatClient first = new("response from first");
        StubChatClient second = new("response from second");
        SwitchableStubProviderRegistry providers = new(first, second, "second-provider", "second-model");
        providers.QueueSwitch();

        TestEventEmitter emitter = new();
        (TurnHandler handler, _) = TurnHandlerFactory.Create(providers, emitter);

        TurnSubmitCommand cmd = new() { Id = "req-1", Message = "Hello" };
        await handler.SubmitAsync(cmd, CancellationToken.None);

        // The turn must have resolved the second (switched-to) client.
        Assert.True(ReferenceEquals(providers.LastResolved, second),
            "GetCurrentAsync should return the switched-to client after commit-at-start.");
    }

    [Fact]
    public async Task Submit_EmitsProviderSwitchedEvent_WithEffectiveNextTurnFalse()
    {
        StubChatClient first = new("first");
        StubChatClient second = new("second");
        SwitchableStubProviderRegistry providers = new(first, second, "second-provider", "second-model");
        providers.QueueSwitch();

        TestEventEmitter emitter = new();
        (TurnHandler handler, _) = TurnHandlerFactory.Create(providers, emitter);

        TurnSubmitCommand cmd = new() { Id = "req-1", Message = "Hello" };
        await handler.SubmitAsync(cmd, CancellationToken.None);

        ProviderSwitchedEvent? switched = emitter.Events.OfType<ProviderSwitchedEvent>().FirstOrDefault();
        Assert.NotNull(switched);
        Assert.Equal("second-provider", switched.Name);
        Assert.False(switched.EffectiveNextTurn,
            "EffectiveNextTurn must be false — the switch applies to the turn now starting.");
    }

    [Fact]
    public async Task Submit_PersistsModelRefAfterCommit()
    {
        StubChatClient first = new("first");
        StubChatClient second = new("second");
        SwitchableStubProviderRegistry providers = new(first, second, "second-provider", "second-model");
        providers.QueueSwitch();

        CapturingActiveModelStore store = new();
        (TurnHandler handler, _) = TurnHandlerFactory.Create(providers, store: store);

        TurnSubmitCommand cmd = new() { Id = "req-1", Message = "Hello" };
        await handler.SubmitAsync(cmd, CancellationToken.None);

        Assert.Equal(1, store.SaveCount);
        Assert.NotNull(store.LastSaved);
        Assert.Equal("second-provider", store.LastSaved.Provider);
        Assert.Equal("second-model", store.LastSaved.Model);
    }

    [Fact]
    public async Task Submit_NoPendingSwitch_DoesNotCallSaveAsync()
    {
        StubChatClient client = new("hello");
        CapturingActiveModelStore store = new();
        (TurnHandler handler, _) = TurnHandlerFactory.Create(client, store);

        TurnSubmitCommand cmd = new() { Id = "req-1", Message = "Hello" };
        await handler.SubmitAsync(cmd, CancellationToken.None);

        Assert.Equal(0, store.SaveCount);
    }
}

/// <summary>
/// ISessionAssetProvisioner stub that is a no-op — used in tests that do not
/// exercise asset-directory provisioning.
/// </summary>
internal sealed class NoopSessionAssetProvisioner : ISessionAssetProvisioner
{
    public string? Provision(AgentProfile profile, string? sessionId) => null;
}
