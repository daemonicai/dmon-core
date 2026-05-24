using Dmon.Abstractions.Providers;
using Dmon.Core.Providers;
using Dmon.Protocol.Events;
using Dmon.Protocol.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Providers;

public sealed class ModelListHandlerTests
{
    private static ProviderConfig MakeConfig(
        string name,
        string? modelId = null,
        string? baseUrl = null) =>
        new()
        {
            Name = name,
            Adapter = "openai",
            DefaultModelId = modelId,
            BaseUrl = baseUrl,
            Auth = new ProviderAuthConfig { Type = "none" }
        };

    private static IProviderRegistry CreateRegistry(IEnumerable<ProviderConfig> configs)
    {
        return new ProviderRegistry(
            configs,
            [new FakeProviderFactory()],
            new NullCredentialResolver(),
            NullLogger<ProviderRegistry>.Instance);
    }

    private static ModelListHandler CreateHandler(IProviderRegistry registry, IProviderFactory? factory = null)
    {
        IProviderFactory resolved = factory ?? new FakeProviderFactory();
        return new ModelListHandler(registry, [resolved]);
    }

    [Fact]
    public void Handle_SingleProvider_ReturnsOneModel()
    {
        ProviderConfig config = MakeConfig("alpha", modelId: "alpha-model");
        IProviderRegistry registry = CreateRegistry([config]);
        ModelListHandler handler = CreateHandler(registry);

        ModelListResultEvent result = handler.Handle();

        Assert.Single(result.Models);
    }

    [Fact]
    public void Handle_MapsCapabilitiesCorrectly()
    {
        ProviderConfig config = MakeConfig("alpha", modelId: "alpha-model");
        IProviderRegistry registry = CreateRegistry([config]);
        FakeProviderFactory factory = new()
        {
            Capabilities = new ChatClientCapabilities
            {
                SupportsToolCalling = true,
                SupportsReasoning = true,
                ContextWindow = 200000,
                MaxTokens = 8192
            }
        };
        ModelListHandler handler = CreateHandler(registry, factory);

        ModelListResultEvent result = handler.Handle();

        Model model = result.Models[0];
        Assert.True(model.ToolCalling);
        Assert.True(model.Reasoning);
        Assert.Equal(200000, model.ContextWindow);
        Assert.Equal(8192, model.MaxTokens);
    }

    [Fact]
    public void Handle_SetsActiveProviderAndModelId()
    {
        ProviderConfig first = MakeConfig("alpha", modelId: "alpha-model");
        ProviderConfig second = MakeConfig("beta", modelId: "beta-model");
        IProviderRegistry registry = CreateRegistry([first, second]);
        ModelListHandler handler = CreateHandler(registry);

        ModelListResultEvent result = handler.Handle();

        Assert.Equal("alpha", result.ActiveProvider);
        Assert.Equal("alpha-model", result.ActiveModelId);
    }

    [Fact]
    public void Handle_ActiveProviderUpdates_AfterSwitch()
    {
        ProviderConfig first = MakeConfig("alpha", modelId: "alpha-model");
        ProviderConfig second = MakeConfig("beta", modelId: "beta-model");
        IProviderRegistry registry = CreateRegistry([first, second]);

        registry.SetProvider("beta");
        registry.CommitPendingSwitch();

        ModelListHandler handler = CreateHandler(registry);
        ModelListResultEvent result = handler.Handle();

        Assert.Equal("beta", result.ActiveProvider);
        Assert.Equal("beta-model", result.ActiveModelId);
    }

    [Fact]
    public void Handle_MultipleProviders_MapsAllToModels()
    {
        ProviderConfig[] configs =
        [
            MakeConfig("a", modelId: "a-model"),
            MakeConfig("b", modelId: "b-model"),
            MakeConfig("c", modelId: "c-model")
        ];
        IProviderRegistry registry = CreateRegistry(configs);
        ModelListHandler handler = CreateHandler(registry);

        ModelListResultEvent result = handler.Handle();

        Assert.Equal(3, result.Models.Count);
        Assert.Equal("a", result.Models[0].Provider);
        Assert.Equal("b", result.Models[1].Provider);
        Assert.Equal("c", result.Models[2].Provider);
    }

    [Fact]
    public void Handle_ProviderWithBaseUrl_PassesBaseUrlToModel()
    {
        ProviderConfig config = MakeConfig("ollama", baseUrl: "http://localhost:11434/v1");
        IProviderRegistry registry = CreateRegistry([config]);
        ModelListHandler handler = CreateHandler(registry);

        ModelListResultEvent result = handler.Handle();

        Assert.Equal("http://localhost:11434/v1", result.Models[0].BaseUrl);
    }

    private sealed class NullCredentialResolver : ICredentialResolver
    {
        public ValueTask<string?> ResolveAsync(string providerName, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }

    private sealed class FakeProviderFactory : IProviderFactory
    {
        public string AdapterName => "openai";
        public string DefaultModelId => "gpt-4o";
        public string DefaultEnvVar => "OPENAI_API_KEY";

        public ChatClientCapabilities Capabilities { get; set; } = new ChatClientCapabilities();

        public ChatClientCapabilities GetCapabilities(string modelId) => Capabilities;

        public ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IChatClient>(new FakeChatClient());
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
            => Task.FromResult(new ChatResponse([]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
