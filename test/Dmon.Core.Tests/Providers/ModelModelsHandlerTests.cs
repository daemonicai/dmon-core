using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Core.Providers;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Providers;

public sealed class ModelModelsHandlerTests
{
    // 4.5 — ModelModelsHandler forwards configured ProviderConfig.BaseUrl to the factory
    [Fact]
    public async Task HandleAsync_ForwardsConfiguredBaseUrlToFactory()
    {
        const string expectedBaseUrl = "http://localhost:8080/v1";

        ProviderConfig config = new()
        {
            Name = "local-llm",
            Adapter = "openai",
            DefaultModelId = "llama3.2",
            BaseUrl = expectedBaseUrl,
            Auth = new ProviderAuthConfig { Type = "none" }
        };

        CapturingProviderFactory factory = new();
        FakeProviderRegistry registry = new([config]);
        NullCredentialResolver credentials = new();

        ModelModelsHandler handler = new(registry, [factory], credentials);
        ModelModelsCommand cmd = new() { Id = "cmd-1", Provider = "local-llm" };

        await handler.HandleAsync(cmd, CancellationToken.None);

        Assert.NotNull(factory.CapturedBaseUrl);
        Assert.Equal(expectedBaseUrl, factory.CapturedBaseUrl);
    }

    [Fact]
    public async Task HandleAsync_NullBaseUrl_ForwardsNullToFactory()
    {
        ProviderConfig config = new()
        {
            Name = "cloud-openai",
            Adapter = "openai",
            DefaultModelId = "gpt-4o",
            BaseUrl = null,
            Auth = new ProviderAuthConfig { Type = "none" }
        };

        CapturingProviderFactory factory = new();
        FakeProviderRegistry registry = new([config]);
        NullCredentialResolver credentials = new();

        ModelModelsHandler handler = new(registry, [factory], credentials);
        ModelModelsCommand cmd = new() { Id = "cmd-2", Provider = "cloud-openai" };

        await handler.HandleAsync(cmd, CancellationToken.None);

        Assert.True(factory.WasCalled);
        Assert.Null(factory.CapturedBaseUrl);
    }

    private sealed class CapturingProviderFactory : IProviderFactory
    {
        public string AdapterName => "openai";
        public string DisplayName => "OpenAI";
        public string DefaultModelId => "gpt-4o";
        public string DefaultEnvVar => "OPENAI_API_KEY";

        public bool WasCalled { get; private set; }
        public string? CapturedBaseUrl { get; private set; }

        public ChatClientCapabilities GetCapabilities(string modelId) => new();

        public ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(
            string? apiKey, string? baseUrl = null, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            CapturedBaseUrl = baseUrl;
            return ValueTask.FromResult<IReadOnlyList<ModelInfo>>([]);
        }

        public ValueTask<IChatClient> CreateAsync(
            ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not needed in ModelModelsHandler tests.");

        public ValueTask<WizardStep> GetNextStepAsync(
            WizardState state, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not needed in ModelModelsHandler tests.");
    }

    private sealed class FakeProviderRegistry : IProviderRegistry
    {
        private readonly IReadOnlyList<ProviderConfig> _configs;

        public FakeProviderRegistry(IReadOnlyList<ProviderConfig> configs)
        {
            _configs = configs;
        }

        public IReadOnlyList<ProviderConfig> GetAll() => _configs;
        public string? GetCurrentModelId() => null;

        public ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ProviderConfig GetCurrentConfig() => _configs[0];
        public void SetProvider(string name) { }
        public void SetModel(string modelId) { }
        public void CycleProvider() { }
        public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public void AddDynamicProvider(ProviderConfig config) { }
        public ProviderSwitchResult? CommitPendingSwitch() => null;
        public bool CurrentSupportsToolCalling => false;
        public bool CurrentSupportsReasoning => false;
    }

    private sealed class NullCredentialResolver : ICredentialResolver
    {
        public ValueTask<string?> ResolveAsync(string providerName, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);
    }
}
