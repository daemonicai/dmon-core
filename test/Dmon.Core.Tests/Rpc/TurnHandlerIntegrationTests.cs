using System.Runtime.CompilerServices;
using Dmon.Abstractions;
using Dmon.Abstractions.Memory;
using Dmon.Abstractions.Profiles;
using Dmon.Abstractions.Providers;
using Dmon.Core.Extensions;
using Dmon.Extensions;
using Dmon.Core.Permissions;
using Dmon.Core.Profiles;
using Dmon.Core.Session;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Conversation;
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
        IActiveModelStore? store = null,
        ISessionHandler? sessionHandler = null,
        ISessionStore? sessionStore = null)
    {
        emitter ??= new TestEventEmitter();
        EmptyToolRegistry tools = new();
        PermitAllPolicy policy = new();
        NoopThinkingHandler thinking = new();
        sessionHandler ??= new StubSessionHandler();
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
            systemPromptBuilder,
            pipelineBuilder,
            configuration,
            new StubAgentProfileResolver(),
            new AgentProfileContext(),
            new NoopSessionAssetProvisioner(),
            NullLogger<TurnHandler>.Instance,
            sessionStore);

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

    // ── Turn persistence (Group 4.1) ─────────────────────────────────────────

    [Fact]
    public async Task Submit_WithActiveSession_PersistsUserAndAssistantMessages()
    {
        const string userMessage      = "Hello, what is two plus two?";
        const string assistantMessage = "Two plus two is four.";

        StubChatClient client = new(assistantMessage);
        SpySessionStore spyStore = new();
        ActiveSessionHandler sessionHandler = new("session-123");

        StubProviderRegistry providers = new(client);
        (TurnHandler handler, _) = TurnHandlerFactory.Create(
            providers,
            sessionHandler: sessionHandler,
            sessionStore: spyStore);

        TurnSubmitCommand cmd = new() { Id = "req-1", Message = userMessage };
        await handler.SubmitAsync(cmd, CancellationToken.None);

        // AppendMessagesAsync must have been called exactly once (one turn = one batch call).
        Assert.Equal(1, spyStore.AppendMessagesCallCount);

        IReadOnlyList<ChatMessage> persisted = spyStore.AppendMessagesCalls[0].Messages;

        // System message must be excluded.
        Assert.DoesNotContain(persisted, m => m.Role == ChatRole.System);

        // User message must be present.
        Assert.Contains(persisted, m => m.Role == ChatRole.User);

        // Assistant message must be present.
        Assert.Contains(persisted, m => m.Role == ChatRole.Assistant);

        // Session id must match the active session.
        Assert.Equal("session-123", spyStore.AppendMessagesCalls[0].SessionId);
    }

    [Fact]
    public async Task Submit_WithNoActiveSession_DoesNotCallSessionStore()
    {
        StubChatClient client = new("response");
        SpySessionStore spyStore = new();
        // StubSessionHandler returns CurrentSession = null.

        StubProviderRegistry providers = new(client);
        (TurnHandler handler, _) = TurnHandlerFactory.Create(
            providers,
            sessionStore: spyStore);

        TurnSubmitCommand cmd = new() { Id = "req-1", Message = "Hello" };
        await handler.SubmitAsync(cmd, CancellationToken.None);

        Assert.Equal(0, spyStore.AppendMessagesCallCount);
    }

    [Fact]
    public async Task Submit_WithNoSessionStore_CompletesNormally()
    {
        // TurnHandler without ISessionStore should still complete the turn.
        StubChatClient client = new("response");
        StubProviderRegistry providers = new(client);
        (TurnHandler handler, TestEventEmitter emitter) = TurnHandlerFactory.Create(providers);

        TurnSubmitCommand cmd = new() { Id = "req-1", Message = "Hello" };
        await handler.SubmitAsync(cmd, CancellationToken.None);

        Assert.Contains(emitter.Events, e => e is TurnEndEvent);
    }
}

// ---------------------------------------------------------------------------
// Group 2: structured tool-call history capture tests
// ---------------------------------------------------------------------------

/// <summary>
/// Inner provider stub for tool-call integration tests. On the first call it
/// emits a FunctionCallContent matching a registered AIFunction; on the second
/// call (after FunctionInvokingChatClient has appended the result to history)
/// it emits a final text response.
///
/// This is the innermost provider; FunctionInvokingChatClient wraps it and
/// handles actual tool dispatch, producing FunctionCallContent and
/// FunctionResultContent in the outer stream that TurnHandler observes.
/// </summary>
internal sealed class FunctionCallProviderStub : IChatClient
{
    private readonly string _toolName;
    private readonly string _finalText;
    private readonly bool _twoTools;
    private int _callCount;

    public FunctionCallProviderStub(string toolName = "stub_tool", string finalText = "Done.", bool twoTools = false)
    {
        _toolName = toolName;
        _finalText = finalText;
        _twoTools = twoTools;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        int call = System.Threading.Interlocked.Increment(ref _callCount);
        if (call == 1)
        {
            // First call: emit function call(s) — FunctionInvokingChatClient will execute them.
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("cid-1", _toolName, new Dictionary<string, object?> { ["input"] = "test" })]);
            if (_twoTools)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("cid-2", _toolName + "_b", new Dictionary<string, object?> { ["input"] = "test2" })]);
        }
        else
        {
            // Subsequent calls: emit final text.
            yield return new ChatResponseUpdate(ChatRole.Assistant, _finalText);
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Provider stub that on first call emits the same callId twice (simulating
/// fragmented streaming of a single function call's arguments). FunctionInvokingChatClient
/// coalesces by callId naturally; the test verifies TurnHandler's capture layer
/// also sees only one call in _history.
/// </summary>
internal sealed class FragmentedCallProviderStub : IChatClient
{
    private readonly string _toolName;
    private int _callCount;

    public FragmentedCallProviderStub(string toolName = "stub_tool")
    {
        _toolName = toolName;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        int call = System.Threading.Interlocked.Increment(ref _callCount);
        if (call == 1)
        {
            // Two updates with the same callId — fragmented argument streaming.
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("cid-1", _toolName, new Dictionary<string, object?> { ["input"] = "par" })]);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("cid-1", _toolName, new Dictionary<string, object?> { ["input"] = "partial_then_complete" })]);
        }
        else
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Completed.");
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Provider registry that exposes CurrentSupportsToolCalling = true and a
/// fixed tool list so FunctionInvokingChatClient can actually execute the stub tool.
/// </summary>
internal sealed class ToolSupportingProviderRegistry : IProviderRegistry
{
    private readonly IChatClient _client;

    public ToolSupportingProviderRegistry(IChatClient client)
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
    public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void AddDynamicProvider(ProviderConfig config) { }
    public string? GetCurrentModelId() => null;
    public ProviderSwitchResult? CommitPendingSwitch() => null;
    public bool CurrentSupportsToolCalling => true;
    public bool CurrentSupportsReasoning => false;
}

/// <summary>
/// IDmonExtension stub that allows all tool calls unconditionally.
/// </summary>
internal sealed class AllowAllExtension : IDmonExtension
{
    public string Name => "allow-all";
    public string Description => "Test stub that allows all calls.";
    public IEnumerable<AIFunction> Tools => [];

    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
        => PermissionResult.Allow;
}

/// <summary>
/// Tool registry with one or two AIFunction stubs registered, so
/// FunctionInvokingChatClient can locate and execute the function by name.
/// FindExtension returns an AllowAll extension so PermissionGateChatClient
/// does not prompt for confirmation in tests.
/// </summary>
internal sealed class StubToolRegistry : IToolRegistry
{
    private readonly List<AIFunction> _tools;
    private readonly AllowAllExtension _extension = new();

    public StubToolRegistry(params AIFunction[] tools)
    {
        _tools = [.. tools];
    }

    public IReadOnlyList<AIFunction> GetAll() => _tools;
    public void Register(string extensionName, IDmonExtension extension, IEnumerable<AIFunction> tools) { }
    public IDmonExtension? FindExtension(string toolName) => _extension;
    public void Unregister(string extensionName) { }
    public IReadOnlyList<RegisteredExtensionSnapshot> GetSnapshot() => [];
    public void Clear() { }
}

/// <summary>
/// Creates a TurnHandler wired with real tool support (ToolSupportingProviderRegistry +
/// StubToolRegistry) so FunctionInvokingChatClient can execute the stub AIFunction.
/// </summary>
internal static class ToolTurnHandlerFactory
{
    public static (TurnHandler handler, TestEventEmitter emitter) Create(
        IChatClient providerClient,
        IToolRegistry tools,
        ISessionHandler? sessionHandler = null,
        ISessionStore? sessionStore = null)
    {
        TestEventEmitter emitter = new();
        ToolSupportingProviderRegistry providers = new(providerClient);
        PermitAllPolicy policy = new();
        NoopThinkingHandler thinking = new();
        sessionHandler ??= new StubSessionHandler();
        StubSystemPromptBuilder systemPromptBuilder = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        NoopActiveModelStore store = new();
        MiddlewarePipelineBuilder pipelineBuilder = new(new MiddlewareRegistry(), configuration);

        TurnHandler handler = new(
            providers,
            store,
            tools,
            emitter,
            policy,
            thinking,
            sessionHandler,
            systemPromptBuilder,
            pipelineBuilder,
            configuration,
            new StubAgentProfileResolver(),
            new AgentProfileContext(),
            new NoopSessionAssetProvisioner(),
            NullLogger<TurnHandler>.Instance,
            sessionStore);

        return (handler, emitter);
    }
}

public sealed class TurnHandlerToolHistoryTests
{
    // Build a deterministic AIFunction that returns a fixed string result.
    private static AIFunction MakeStubTool(string name, string result = "tool-result")
        => AIFunctionFactory.Create(
            (string input) => result,
            name,
            $"Stub tool {name}");

    // ── 2.2: canonical assistant + tool-role messages appended to history ─────

    [Fact]
    public async Task Submit_WithToolCall_PersistsAssistantMessageWithFunctionCallContent()
    {
        AIFunction tool = MakeStubTool("stub_tool");
        StubToolRegistry tools = new(tool);
        FunctionCallProviderStub provider = new("stub_tool", "Done.");
        SpySessionStore spyStore = new();
        ActiveSessionHandler sessionHandler = new("sess-1");

        (TurnHandler handler, _) = ToolTurnHandlerFactory.Create(provider, tools, sessionHandler, spyStore);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "go" }, CancellationToken.None);

        // Flatten all persisted batches (may be called once per turn or per follow-up).
        List<ChatMessage> allPersisted = spyStore.AppendMessagesCalls
            .SelectMany(c => c.Messages)
            .ToList();

        // There must be at least one assistant message carrying a FunctionCallContent.
        Assert.Contains(allPersisted,
            m => m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Any());
    }

    [Fact]
    public async Task Submit_WithToolCall_PersistsToolRoleMessageWithFunctionResultContent()
    {
        AIFunction tool = MakeStubTool("stub_tool");
        StubToolRegistry tools = new(tool);
        FunctionCallProviderStub provider = new("stub_tool", "Done.");
        SpySessionStore spyStore = new();
        ActiveSessionHandler sessionHandler = new("sess-1");

        (TurnHandler handler, _) = ToolTurnHandlerFactory.Create(provider, tools, sessionHandler, spyStore);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "go" }, CancellationToken.None);

        List<ChatMessage> allPersisted = spyStore.AppendMessagesCalls
            .SelectMany(c => c.Messages)
            .ToList();

        // There must be a tool-role message carrying a FunctionResultContent.
        Assert.Contains(allPersisted,
            m => m.Role == ChatRole.Tool && m.Contents.OfType<FunctionResultContent>().Any());
    }

    [Fact]
    public async Task Submit_TextOnly_DoesNotAppendToolRoleMessage()
    {
        StubChatClient client = new("just text");
        SpySessionStore spyStore = new();
        ActiveSessionHandler sessionHandler = new("sess-1");
        StubProviderRegistry providers = new(client);

        (TurnHandler handler, _) = TurnHandlerFactory.Create(providers, sessionHandler: sessionHandler, sessionStore: spyStore);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "hello" }, CancellationToken.None);

        List<ChatMessage> allPersisted = spyStore.AppendMessagesCalls
            .SelectMany(c => c.Messages)
            .ToList();
        Assert.DoesNotContain(allPersisted, m => m.Role == ChatRole.Tool);
    }

    // ── 2.1: fragmented call coalescing ──────────────────────────────────────

    [Fact]
    public async Task Submit_WithFragmentedCall_CoalescesToSingleFunctionCallContentInHistory()
    {
        // FragmentedCallProviderStub emits two updates with the same callId on the first call.
        // FunctionInvokingChatClient sees one logical call (it coalesces by callId); the outer
        // stream surfaced to TurnHandler should therefore contain only one FunctionCallContent.
        AIFunction tool = MakeStubTool("stub_tool");
        StubToolRegistry tools = new(tool);
        FragmentedCallProviderStub provider = new("stub_tool");
        SpySessionStore spyStore = new();
        ActiveSessionHandler sessionHandler = new("sess-1");

        (TurnHandler handler, TestEventEmitter emitter) = ToolTurnHandlerFactory.Create(provider, tools, sessionHandler, spyStore);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "go" }, CancellationToken.None);

        List<ChatMessage> allPersisted = spyStore.AppendMessagesCalls
            .SelectMany(c => c.Messages)
            .ToList();

        // At most one FunctionCallContent per distinct callId across all assistant messages.
        int functionCallCount = allPersisted
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Count(fc => fc.CallId == "cid-1");
        Assert.Equal(1, functionCallCount);

        // toolExecutionStart must have been emitted exactly once for callId cid-1.
        int startCount = emitter.Events.OfType<ToolExecutionStartEvent>()
            .Count(e => e.CallId == "cid-1");
        Assert.Equal(1, startCount);
    }

    // ── 2.4: two-tool turn ordering guarantee ────────────────────────────────

    [Fact]
    public async Task Submit_WithTwoToolCalls_PreservesCallOrderInHistoryAndEmitsUniqueStartEvents()
    {
        // FunctionCallProviderStub with twoTools=true emits cid-1 (stub_tool) then cid-2 (stub_tool_b).
        AIFunction toolA = MakeStubTool("stub_tool");
        AIFunction toolB = MakeStubTool("stub_tool_b");
        StubToolRegistry tools = new(toolA, toolB);
        FunctionCallProviderStub provider = new("stub_tool", "Done.", twoTools: true);
        SpySessionStore spyStore = new();
        ActiveSessionHandler sessionHandler = new("sess-two");

        (TurnHandler handler, TestEventEmitter emitter) = ToolTurnHandlerFactory.Create(provider, tools, sessionHandler, spyStore);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "go" }, CancellationToken.None);

        List<ChatMessage> allPersisted = spyStore.AppendMessagesCalls
            .SelectMany(c => c.Messages)
            .ToList();

        // The assistant message must carry both FunctionCallContents.
        List<FunctionCallContent> assistantCalls = allPersisted
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .ToList();

        Assert.Contains(assistantCalls, fc => fc.CallId == "cid-1");
        Assert.Contains(assistantCalls, fc => fc.CallId == "cid-2");

        // cid-1 must appear before cid-2 (first-seen call order preserved).
        int idx1 = assistantCalls.FindIndex(fc => fc.CallId == "cid-1");
        int idx2 = assistantCalls.FindIndex(fc => fc.CallId == "cid-2");
        Assert.True(idx1 < idx2, $"Expected cid-1 (index {idx1}) before cid-2 (index {idx2}).");

        // The tool-role message must carry FunctionResultContents for both calls
        // in the same order (cid-1 result before cid-2 result).
        List<FunctionResultContent> toolResults = allPersisted
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .ToList();

        Assert.Contains(toolResults, fr => fr.CallId == "cid-1");
        Assert.Contains(toolResults, fr => fr.CallId == "cid-2");

        int ridx1 = toolResults.FindIndex(fr => fr.CallId == "cid-1");
        int ridx2 = toolResults.FindIndex(fr => fr.CallId == "cid-2");
        Assert.True(ridx1 < ridx2, $"Expected cid-1 result (index {ridx1}) before cid-2 result (index {ridx2}).");

        // Exactly one ToolExecutionStartEvent per distinct callId — no duplicates.
        int startCid1 = emitter.Events.OfType<ToolExecutionStartEvent>().Count(e => e.CallId == "cid-1");
        int startCid2 = emitter.Events.OfType<ToolExecutionStartEvent>().Count(e => e.CallId == "cid-2");
        Assert.Equal(1, startCid1);
        Assert.Equal(1, startCid2);
    }

    // ── 2.3: persistence round-trip via ConversationMapper ───────────────────

    [Fact]
    public async Task Submit_WithToolCall_PersistenceRoundTripMapsToToolCallAndResultParts()
    {
        AIFunction tool = MakeStubTool("stub_tool", "42");
        StubToolRegistry tools = new(tool);
        FunctionCallProviderStub provider = new("stub_tool", "Computed.");
        RoundTripSpySessionStore rtStore = new();
        ActiveSessionHandler sessionHandler = new("sess-rt");

        (TurnHandler handler, _) = ToolTurnHandlerFactory.Create(provider, tools, sessionHandler, rtStore);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "compute" }, CancellationToken.None);

        // At least one assistant record must contain a ToolCallPart.
        Assert.Contains(rtStore.Records,
            r => r.Role == "assistant" && r.Parts.OfType<ToolCallPart>().Any());

        // At least one tool record must contain a ToolResultPart.
        Assert.Contains(rtStore.Records,
            r => r.Role == "tool" && r.Parts.OfType<ToolResultPart>().Any());
    }
}

/// <summary>
/// Session store spy that exposes the MessageRecord list produced by
/// ConversationMapper.ToParts, allowing assertions on the persisted Part shape.
/// </summary>
internal sealed class RoundTripSpySessionStore : ISessionStore
{
    private readonly List<MessageRecord> _records = [];

    public IReadOnlyList<MessageRecord> Records => _records;

    public Task<IReadOnlyList<MessageRecord>> AppendMessagesAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        MessageRecord[] records = messages
            .Where(m => m.Role != ChatRole.System)
            .Select(m =>
            {
                (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(m);
                return new MessageRecord
                {
                    EntryId = Guid.NewGuid().ToString(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Role = role,
                    Parts = parts
                };
            })
            .ToArray();
        _records.AddRange(records);
        return Task.FromResult<IReadOnlyList<MessageRecord>>(records);
    }

    public Task<SessionMeta> CreateAsync(string? name = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<SessionMeta> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<SessionMeta>> ListAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task UpdateMetaAsync(SessionMeta meta, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public string GetSessionDirectory(string sessionId) => throw new NotSupportedException();
    public Task<SessionMeta> ForkAsync(string sourceSessionId, string entryId, string? name = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<SessionMeta> CloneAsync(string sourceSessionId, string? name = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<object>> ReadMessagesAsync(string sessionId, bool applyCompaction = true, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<string> AppendMessageAsync(string sessionId, string role, IReadOnlyList<Part> parts, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<SessionLogLine>> ReadRecordsAsync(string sessionId, bool applyCompaction = true, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>
/// ISessionAssetProvisioner stub that is a no-op — used in tests that do not
/// exercise asset-directory provisioning.
/// </summary>
internal sealed class NoopSessionAssetProvisioner : ISessionAssetProvisioner
{
    public string? Provision(AgentProfile profile, string? sessionId) => null;
}

/// <summary>
/// ISessionHandler stub that returns a fixed non-null current session.
/// Used in turn-persistence tests to make TurnHandler believe a session is active.
/// </summary>
internal sealed class ActiveSessionHandler : ISessionHandler
{
    public ActiveSessionHandler(string sessionId)
    {
        CurrentSession = new SessionMeta { Id = sessionId, Created = DateTimeOffset.UtcNow, Modified = DateTimeOffset.UtcNow };
    }

    public SessionMeta? CurrentSession { get; }

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
/// ISessionStore spy that captures all AppendMessagesAsync calls.
/// Canonical writes are no-ops (no disk); memory is not tested here.
/// </summary>
internal sealed class SpySessionStore : ISessionStore
{
    private readonly List<(string SessionId, IReadOnlyList<ChatMessage> Messages, MemoryScope Scope)> _calls = [];

    public IReadOnlyList<(string SessionId, IReadOnlyList<ChatMessage> Messages, MemoryScope Scope)> AppendMessagesCalls => _calls;

    public int AppendMessagesCallCount => _calls.Count;

    public Task<IReadOnlyList<MessageRecord>> AppendMessagesAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        _calls.Add((sessionId, messages, scope));
        // Return a fake MessageRecord per non-system message.
        MessageRecord[] records = messages
            .Where(m => m.Role != ChatRole.System)
            .Select(m =>
            {
                (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(m);
                return new MessageRecord
                {
                    EntryId = Guid.NewGuid().ToString(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Role = role,
                    Parts = parts
                };
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<MessageRecord>>(records);
    }

    // ── Remaining ISessionStore members (not exercised by TurnHandler) ────────

    public Task<SessionMeta> CreateAsync(string? name = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SessionMeta> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<SessionMeta>> ListAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task UpdateMetaAsync(SessionMeta meta, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public string GetSessionDirectory(string sessionId)
        => throw new NotSupportedException();

    public Task<SessionMeta> ForkAsync(string sourceSessionId, string entryId, string? name = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SessionMeta> CloneAsync(string sourceSessionId, string? name = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<object>> ReadMessagesAsync(string sessionId, bool applyCompaction = true, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string> AppendMessageAsync(string sessionId, string role, IReadOnlyList<Part> parts, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<SessionLogLine>> ReadRecordsAsync(string sessionId, bool applyCompaction = true, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
