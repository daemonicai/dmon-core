using System.Runtime.CompilerServices;
using Dmon.Abstractions;
using Dmon.Abstractions.Providers;
using Dmon.Core.Extensions;
using Dmon.Extensions;
using Dmon.Core.Permissions;
using Dmon.Core.Session;
using Dmon.Protocol.Permissions;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
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

internal static class TurnHandlerFactory
{
    public static (TurnHandler handler, TestEventEmitter emitter) Create(IChatClient client)
    {
        TestEventEmitter emitter = new();
        StubProviderRegistry providers = new(client);
        EmptyToolRegistry tools = new();
        PermitAllPolicy policy = new();
        NoopThinkingHandler thinking = new();
        StubSessionHandler sessionHandler = new();
        StubAttachmentStore attachmentStore = new();
        StubSystemPromptBuilder systemPromptBuilder = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        TurnHandler handler = new(
            providers,
            tools,
            emitter,
            policy,
            thinking,
            sessionHandler,
            attachmentStore,
            systemPromptBuilder,
            configuration,
            NullLogger<TurnHandler>.Instance);

        return (handler, emitter);
    }
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
}
