using System.Runtime.CompilerServices;
using Dmon.Abstractions;
using Dmon.Abstractions.Providers;
using Dmon.Core.Extensions;
using Dmon.Core.Permissions;
using Dmon.Core.Profiles;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Protocol.Commands;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Rpc;

/// <summary>
/// Verifies that TurnHandler sets ChatOptions.ModelId from the active provider
/// registry on every turn, so providers that require an explicit model ID
/// (e.g. GeminiDotnet) receive it without relying on a baked-in client default.
/// </summary>
public sealed class TurnHandlerModelIdTests
{
    // ── 1.1 — model id from GetCurrentModelId() is forwarded to ChatOptions ─

    [Fact]
    public async Task Submit_SetsOptionsModelId_FromGetCurrentModelId()
    {
        const string expectedModelId = "gemini-2.0-flash";

        CapturingOptionsClient client = new();
        (TurnHandler handler, _) = MakeHandler(client, currentModelId: expectedModelId, defaultModelId: null);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "req-1", Message = "Hello" }, CancellationToken.None);

        Assert.Equal(expectedModelId, client.LastOptions?.ModelId);
    }

    // ── 1.1 — fallback to GetCurrentConfig().DefaultModelId when GetCurrentModelId() is null ─

    [Fact]
    public async Task Submit_SetsOptionsModelId_FromDefaultModelId_WhenGetCurrentModelIdIsNull()
    {
        const string expectedModelId = "gpt-4o";

        CapturingOptionsClient client = new();
        (TurnHandler handler, _) = MakeHandler(client, currentModelId: null, defaultModelId: expectedModelId);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "req-1", Message = "Hello" }, CancellationToken.None);

        Assert.Equal(expectedModelId, client.LastOptions?.ModelId);
    }

    // ── 1.1 — GetCurrentModelId() takes precedence over DefaultModelId ──────

    [Fact]
    public async Task Submit_PrefersGetCurrentModelId_OverDefaultModelId()
    {
        const string switchedModel = "gemini-1.5-pro";
        const string configDefault = "gemini-2.0-flash";

        CapturingOptionsClient client = new();
        (TurnHandler handler, _) = MakeHandler(client, currentModelId: switchedModel, defaultModelId: configDefault);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "req-1", Message = "Hello" }, CancellationToken.None);

        Assert.Equal(switchedModel, client.LastOptions?.ModelId);
    }

    // ── 1.1 — ModelId is left unset when both sources return null/empty ─────

    [Fact]
    public async Task Submit_LeavesOptionsModelIdUnset_WhenBothSourcesAreNull()
    {
        CapturingOptionsClient client = new();
        (TurnHandler handler, _) = MakeHandler(client, currentModelId: null, defaultModelId: null);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "req-1", Message = "Hello" }, CancellationToken.None);

        Assert.Null(client.LastOptions?.ModelId);
    }

    // ── factory ─────────────────────────────────────────────────────────────

    private static (TurnHandler handler, TestEventEmitter emitter) MakeHandler(
        IChatClient client,
        string? currentModelId,
        string? defaultModelId)
    {
        TestEventEmitter emitter = new();
        ConfigurableStubProviderRegistry providers = new(client, currentModelId, defaultModelId);
        EmptyToolRegistry tools = new();
        PermitAllPolicy policy = new();
        NoopThinkingHandler thinking = new();
        StubSessionHandler sessionHandler = new();
        StubAttachmentStore attachmentStore = new();
        StubSystemPromptBuilder systemPromptBuilder = new();
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
            attachmentStore,
            systemPromptBuilder,
            pipelineBuilder,
            configuration,
            new StubAgentProfileResolver(),
            new AgentProfileContext(),
            NullLogger<TurnHandler>.Instance);

        return (handler, emitter);
    }

    // ── test doubles ────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the ChatOptions passed on each streaming call.
    /// </summary>
    private sealed class CapturingOptionsClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public void Dispose() { }
    }

    /// <summary>
    /// IProviderRegistry with configurable GetCurrentModelId() and DefaultModelId.
    /// </summary>
    private sealed class ConfigurableStubProviderRegistry : IProviderRegistry
    {
        private readonly IChatClient _client;
        private readonly string? _currentModelId;
        private readonly string? _defaultModelId;

        public ConfigurableStubProviderRegistry(IChatClient client, string? currentModelId, string? defaultModelId)
        {
            _client = client;
            _currentModelId = currentModelId;
            _defaultModelId = defaultModelId;
        }

        public ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_client);

        public ProviderConfig GetCurrentConfig() => new()
        {
            Name = "stub",
            Adapter = "stub",
            DefaultModelId = _defaultModelId,
            Auth = new ProviderAuthConfig { Type = "none" }
        };

        public IReadOnlyList<ProviderConfig> GetAll() => [GetCurrentConfig()];

        public void SetProvider(string name) { }

        public void SetModel(string modelId) { }

        public void CycleProvider() { }

        public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void AddDynamicProvider(ProviderConfig config) { }

        public string? GetCurrentModelId() => _currentModelId;

        public ProviderSwitchResult? CommitPendingSwitch() => null;

        public bool CurrentSupportsToolCalling => false;

        public bool CurrentSupportsReasoning => false;
    }
}
