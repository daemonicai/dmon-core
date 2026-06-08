using System.Runtime.CompilerServices;
using Dmon.Abstractions;
using Dmon.Core.Extensions;
using Dmon.Core.Profiles;
using Dmon.Core.Rpc;
using Dmon.Protocol.Commands;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Rpc;

/// <summary>
/// Verifies that TurnHandler injects the system message at index 0 exactly
/// once and does not rebuild it on subsequent turns.
/// </summary>
public sealed class TurnHandlerSystemPromptTests
{
    private const string SystemPromptText = "You are a captured test assistant.";

    // ── 5.4 — system message at index 0 on first turn ───────────────────────

    [Fact]
    public async Task Submit_FirstTurn_SystemMessageAtIndexZero()
    {
        CapturingChatClient client = new();
        (TurnHandler handler, _) = MakeHandler(client);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "req-1", Message = "Hello" }, CancellationToken.None);

        IReadOnlyList<ChatMessage> messages = client.LastMessages;
        Assert.NotEmpty(messages);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(SystemPromptText, messages[0].Text);
    }

    // ── 5.4 — system message still at index 0 on second turn, same text ─────

    [Fact]
    public async Task Submit_SecondTurn_SameSystemMessageAtIndexZero()
    {
        CapturingChatClient client = new();
        (TurnHandler handler, _) = MakeHandler(client);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "req-1", Message = "First" }, CancellationToken.None);
        string firstTurnSystemText = client.LastMessages[0].Text ?? string.Empty;

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "req-2", Message = "Second" }, CancellationToken.None);
        string secondTurnSystemText = client.LastMessages[0].Text ?? string.Empty;

        Assert.Equal(ChatRole.System, client.LastMessages[0].Role);
        Assert.Equal(firstTurnSystemText, secondTurnSystemText);
    }

    // ── 5.4 — system prompt builder called exactly once across two turns ─────

    [Fact]
    public async Task Submit_TwoTurns_SystemPromptBuiltOnce()
    {
        CapturingChatClient client = new();
        CountingSystemPromptBuilder promptBuilder = new();
        (TurnHandler handler, _) = MakeHandler(client, promptBuilder);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "req-1", Message = "First" }, CancellationToken.None);
        await handler.SubmitAsync(new TurnSubmitCommand { Id = "req-2", Message = "Second" }, CancellationToken.None);

        Assert.Equal(1, promptBuilder.BuildCount);
    }

    // ── factory ─────────────────────────────────────────────────────────────

    private static (TurnHandler handler, TestEventEmitter emitter) MakeHandler(
        IChatClient client,
        ISystemPromptBuilder? systemPromptBuilder = null)
    {
        TestEventEmitter emitter = new();
        StubProviderRegistry providers = new(client);
        EmptyToolRegistry tools = new();
        PermitAllPolicy policy = new();
        NoopThinkingHandler thinking = new();
        StubSessionHandler sessionHandler = new();
        ISystemPromptBuilder promptBuilder = systemPromptBuilder ?? new FixedSystemPromptBuilder(SystemPromptText);
        IConfiguration configuration = new ConfigurationBuilder().Build();

        MiddlewarePipelineBuilder pipelineBuilder = new(new MiddlewareRegistry(), configuration);

        TurnHandler handler = new(
            providers,
            new NoopActiveModelStore(),
            tools,
            emitter,
            policy,
            thinking,
            sessionHandler,
            promptBuilder,
            pipelineBuilder,
            configuration,
            new StubAgentProfileResolver(),
            new AgentProfileContext(),
            new NoopSessionAssetProvisioner(),
            NullLogger<TurnHandler>.Instance);

        return (handler, emitter);
    }

    // ── test doubles ────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the messages list on each streaming call.
    /// </summary>
    private sealed class CapturingChatClient : IChatClient
    {
        private IReadOnlyList<ChatMessage> _lastMessages = [];

        public IReadOnlyList<ChatMessage> LastMessages => _lastMessages;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _lastMessages = messages.ToList();
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _lastMessages = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Returns a fixed system message and counts how many times it was called.
    /// </summary>
    private sealed class CountingSystemPromptBuilder : ISystemPromptBuilder
    {
        private int _count;
        public int BuildCount => _count;

        public Task<ChatMessage> BuildAsync(CancellationToken cancellationToken)
        {
            _count++;
            return Task.FromResult(new ChatMessage(ChatRole.System, SystemPromptText));
        }
    }

    /// <summary>
    /// Returns a fixed system message with the supplied text.
    /// </summary>
    private sealed class FixedSystemPromptBuilder(string text) : ISystemPromptBuilder
    {
        public Task<ChatMessage> BuildAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ChatMessage(ChatRole.System, text));
    }
}
