using System.ClientModel;
using System.ClientModel.Primitives;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Dmon.Providers.Mlx;

public sealed class MlxProviderFactory : IProviderFactory
{
    // gemma-4 emits a separate reasoning scratchpad before tool calls; a generous token
    // budget ensures reasoning clears before the tool-call payload is emitted. Applied
    // only when the caller leaves MaxOutputTokens unset — never overwrites an explicit cap.
    internal const int DefaultMaxOutputTokens = 8192;

    private readonly MlxRuntimeOptions _options;
    private readonly MlxRuntimeState _runtimeState;
    private readonly MlxProviderExtension _extension;
    private readonly HttpMessageHandler? _handlerOverride;

    public MlxProviderFactory(
        MlxRuntimeOptions options,
        MlxRuntimeState runtimeState,
        MlxProviderExtension extension)
    {
        _options = options;
        _runtimeState = runtimeState;
        _extension = extension;
    }

    // Internal constructor for testability — injects an HTTP handler to stub the OpenAI wire.
    internal MlxProviderFactory(
        MlxRuntimeOptions options,
        MlxRuntimeState runtimeState,
        MlxProviderExtension extension,
        HttpMessageHandler handlerOverride)
    {
        _options = options;
        _runtimeState = runtimeState;
        _extension = extension;
        _handlerOverride = handlerOverride;
    }

    public string AdapterName => "mlx";
    public string DisplayName => "MLX";
    public string DefaultModelId => _options.ModelId;
    public string DefaultEnvVar => "DMON_MLX_MODEL_ID";

    public ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("MLX provider is attach-based and zero-config; no wizard setup required.");

    public ChatClientCapabilities GetCapabilities(string modelId) =>
        new()
        {
            SupportsToolCalling = _runtimeState.ToolCallingVerified ?? false,
        };

    public async ValueTask<IChatClient> CreateAsync(
        ProviderConfig config,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        // Self-heal (design.md D2): start the runtime and run the one-time tool-calling probe
        // before snapshotting capabilities, so the probe's ToolCallingVerified result is
        // reflected in the client's advertised SupportsToolCalling rather than a pre-probe default.
        await _extension.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

        string modelId = config.DefaultModelId ?? _options.ModelId;
        string baseUrl = config.BaseUrl
            ?? (string.IsNullOrEmpty(_runtimeState.BaseUrl)
                ? $"http://{_options.Host}:{_options.Port}/v1"
                : _runtimeState.BaseUrl);

        OpenAIClientOptions openAiOptions = new() { Endpoint = new Uri(baseUrl) };
        if (_handlerOverride is not null)
            openAiOptions.Transport = new HttpClientPipelineTransport(
                new HttpClient(_handlerOverride, disposeHandler: false));

        OpenAI.Chat.ChatClient chatClient = new(modelId, new ApiKeyCredential(apiKey ?? "none"), openAiOptions);
        IChatClient client = chatClient.AsIChatClient();
        IChatClient withTokens = new MlxMaxTokensDefaulter(client);
        ChatClientCapabilities caps = GetCapabilities(modelId);
        IChatClient decorated = new CapabilitiesDecorator(withTokens, caps);
        return new EnsureRunningChatClient(decorated, _extension);
    }

    // Applies a generous default max_tokens so gemma-4 reasoning clears before tool calls.
    // stock mlx_lm.server drops the reasoning field as unknown; no interception needed.
    private sealed class MlxMaxTokensDefaulter(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options = options?.Clone() ?? new ChatOptions();
            options.MaxOutputTokens ??= DefaultMaxOutputTokens;
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options = options?.Clone() ?? new ChatOptions();
            options.MaxOutputTokens ??= DefaultMaxOutputTokens;
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
    }
}
