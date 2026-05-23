using Dmon.Core.Providers;
using Dmon.Protocol.Events;
using Dmon.Protocol.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Providers;

public sealed class ModelListHandlerTests
{
    private static ProviderConfig MakeConfig(
        string name,
        string? modelId = null,
        bool toolCalling = false,
        bool reasoning = false,
        int contextWindow = 8192,
        int maxTokens = 4096,
        string? baseUrl = null) =>
        new()
        {
            Name = name,
            Adapter = "openai",
            DefaultModelId = modelId,
            BaseUrl = baseUrl,
            Auth = new ProviderAuthConfig { Type = "none" },
            Capabilities = new ProviderCapabilities
            {
                ToolCalling = toolCalling,
                Reasoning = reasoning,
                ContextWindow = contextWindow,
                MaxTokens = maxTokens
            }
        };

    private static IProviderRegistry CreateRegistry(IEnumerable<ProviderConfig> configs)
    {
        return new ProviderRegistry(
            configs,
            new NullCredentialResolver(),
            NullLogger<ProviderRegistry>.Instance);
    }

    [Fact]
    public void Handle_SingleProvider_ReturnsOneModel()
    {
        ProviderConfig config = MakeConfig("alpha", modelId: "alpha-model", toolCalling: true);
        IProviderRegistry registry = CreateRegistry([config]);
        ModelListHandler handler = new(registry);

        ModelListResultEvent result = handler.Handle();

        Assert.Single(result.Models);
    }

    [Fact]
    public void Handle_MapsCapabilitiesCorrectly()
    {
        ProviderConfig config = MakeConfig(
            "alpha",
            modelId: "alpha-model",
            toolCalling: true,
            reasoning: true,
            contextWindow: 200000,
            maxTokens: 8192);
        IProviderRegistry registry = CreateRegistry([config]);
        ModelListHandler handler = new(registry);

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
        ModelListHandler handler = new(registry);

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

        ModelListHandler handler = new(registry);
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
        ModelListHandler handler = new(registry);

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
        ModelListHandler handler = new(registry);

        ModelListResultEvent result = handler.Handle();

        Assert.Equal("http://localhost:11434/v1", result.Models[0].BaseUrl);
    }

    private sealed class NullCredentialResolver : ICredentialResolver
    {
        public ValueTask<string?> ResolveAsync(string providerName, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }
}
