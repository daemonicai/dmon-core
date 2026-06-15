using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Protocol.Wizard;
using Dmon.Core.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Providers;

public sealed class ProviderRegistryTests
{
    private static ProviderConfig MakeConfig(string name) =>
        new()
        {
            Name = name,
            Adapter = "openai",
            DefaultModelId = $"{name}-model",
            Auth = new ProviderAuthConfig { Type = "none" }
        };

    private static IProviderRegistry CreateRegistry(
        IEnumerable<ProviderConfig> configs,
        IProviderFactory? factory = null,
        ICredentialResolver? resolver = null,
        IActiveModelStore? store = null)
    {
        resolver ??= new FakeCredentialResolver();
        factory ??= new FakeProviderFactory();
        store ??= new NullActiveModelStore();
        return new ProviderRegistry(
            configs,
            [factory],
            resolver,
            store,
            NullLogger<ProviderRegistry>.Instance);
    }

    [Fact]
    public void GetCurrentConfig_ReturnsFirstProviderByDefault()
    {
        ProviderConfig first = MakeConfig("alpha");
        ProviderConfig second = MakeConfig("beta");
        IProviderRegistry registry = CreateRegistry([first, second]);

        ProviderConfig current = registry.GetCurrentConfig();

        Assert.Equal("alpha", current.Name);
    }

    [Fact]
    public void GetAll_ReturnsAllConfiguredProviders()
    {
        ProviderConfig[] configs = [MakeConfig("a"), MakeConfig("b"), MakeConfig("c")];
        IProviderRegistry registry = CreateRegistry(configs);

        IReadOnlyList<ProviderConfig> all = registry.GetAll();

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void SetProvider_PendingSwitchCommits_ToNamedProvider()
    {
        ProviderConfig first = MakeConfig("alpha");
        ProviderConfig second = MakeConfig("beta");
        IProviderRegistry registry = CreateRegistry([first, second]);

        registry.SetProvider("beta");
        ProviderSwitchResult? result = registry.CommitPendingSwitch();

        Assert.NotNull(result);
        Assert.Equal("beta", result.ProviderName);
        Assert.Equal("beta", registry.GetCurrentConfig().Name);
    }

    [Fact]
    public void SetModel_OverridesModelId_InCommitResult()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha"), MakeConfig("beta")]);

        registry.SetProvider("beta");
        registry.SetModel("override-model-id");
        ProviderSwitchResult? result = registry.CommitPendingSwitch();

        Assert.NotNull(result);
        Assert.Equal("override-model-id", result.ModelId);
    }

    [Fact]
    public void CommitPendingSwitch_ReturnsNull_WhenNoPendingSwitch()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha")]);

        ProviderSwitchResult? result = registry.CommitPendingSwitch();

        Assert.Null(result);
    }

    [Fact]
    public void CycleProvider_AdvancesToNextProvider()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("a"), MakeConfig("b"), MakeConfig("c")]);

        registry.CycleProvider();
        registry.CommitPendingSwitch();

        Assert.Equal("b", registry.GetCurrentConfig().Name);
    }

    [Fact]
    public void CycleProvider_WrapsAround()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("a"), MakeConfig("b")]);

        // Move to "b"
        registry.CycleProvider();
        registry.CommitPendingSwitch();

        // Cycle again should wrap to "a"
        registry.CycleProvider();
        registry.CommitPendingSwitch();

        Assert.Equal("a", registry.GetCurrentConfig().Name);
    }

    [Fact]
    public void CurrentSupportsToolCalling_ReflectsFactory()
    {
        // FakeProviderFactory returns tool-calling=true for models ending in "-tool", false otherwise.
        FakeProviderFactory factory = new(modelId =>
            modelId.EndsWith("-tool", StringComparison.Ordinal)
                ? new ChatClientCapabilities { SupportsToolCalling = true }
                : new ChatClientCapabilities { SupportsToolCalling = false });

        ProviderConfig withTool = new()
        {
            Name = "a",
            Adapter = "openai",
            DefaultModelId = "a-tool",
            Auth = new ProviderAuthConfig { Type = "none" }
        };
        ProviderConfig withoutTool = MakeConfig("b");
        IProviderRegistry registry = CreateRegistry([withTool, withoutTool], factory);

        Assert.True(registry.CurrentSupportsToolCalling);

        registry.SetProvider("b");
        registry.CommitPendingSwitch();

        Assert.False(registry.CurrentSupportsToolCalling);
    }

    [Fact]
    public void CurrentSupportsReasoning_ReflectsFactory()
    {
        FakeProviderFactory factory = new(modelId =>
            modelId.EndsWith("-reason", StringComparison.Ordinal)
                ? new ChatClientCapabilities { SupportsReasoning = true }
                : new ChatClientCapabilities { SupportsReasoning = false });

        ProviderConfig withReasoning = new()
        {
            Name = "a",
            Adapter = "openai",
            DefaultModelId = "a-reason",
            Auth = new ProviderAuthConfig { Type = "none" }
        };
        ProviderConfig withoutReasoning = MakeConfig("b");
        IProviderRegistry registry = CreateRegistry([withReasoning, withoutReasoning], factory);

        Assert.True(registry.CurrentSupportsReasoning);

        registry.SetProvider("b");
        registry.CommitPendingSwitch();

        Assert.False(registry.CurrentSupportsReasoning);
    }

    [Fact]
    public void SetProvider_UnknownName_Throws()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha")]);

        void Act() => registry.SetProvider("nonexistent");

        Assert.Throws<InvalidOperationException>(Act);
    }

    [Fact]
    public void Constructor_EmptyConfigs_Succeeds()
    {
        // Zero providers is valid after the registry was softened (Group 3).
        // GetCurrentConfig() will throw at use-time, not at construction.
        IProviderRegistry registry = CreateRegistry([]);

        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void GetCurrentConfig_EmptyConfigs_ThrowsInvalidOperation()
    {
        IProviderRegistry registry = CreateRegistry([]);

        Assert.Throws<InvalidOperationException>(() => registry.GetCurrentConfig());
    }

    [Fact]
    public async Task GetCurrentAsync_EmptyConfigs_ThrowsInvalidOperation()
    {
        IProviderRegistry registry = CreateRegistry([]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await registry.GetCurrentAsync());
    }

    [Fact]
    public async Task GetCurrentAsync_ReturnsSameInstance_OnConsecutiveCalls()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha")]);

        IChatClient first = await registry.GetCurrentAsync();
        IChatClient second = await registry.GetCurrentAsync();

        Assert.True(ReferenceEquals(first, second));
    }

    [Fact]
    public async Task GetCurrentAsync_ReturnsSameInstance_AfterSetProviderButBeforeCommit()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha"), MakeConfig("beta")]);

        IChatClient before = await registry.GetCurrentAsync();
        registry.SetProvider("beta");
        IChatClient after = await registry.GetCurrentAsync();

        Assert.True(ReferenceEquals(before, after));
    }

    [Fact]
    public async Task GetCurrentAsync_ReturnsDifferentInstance_AfterCommitPendingSwitch()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha"), MakeConfig("beta")]);

        IChatClient original = await registry.GetCurrentAsync();
        registry.SetProvider("beta");
        registry.CommitPendingSwitch();
        IChatClient switched = await registry.GetCurrentAsync();

        Assert.False(ReferenceEquals(original, switched));
    }

    [Fact]
    public void Constructor_UnknownAdapter_ExcludesConfigAndDoesNotThrow()
    {
        // ADR-023 D7 / Group-4: the registry now warns-and-filters configs whose
        // adapter has no registered factory, instead of throwing. This lets the
        // runtime start when the composition root omits a provider that is in config.
        ProviderConfig config = new()
        {
            Name = "unknown",
            Adapter = "nonexistent-adapter",
            DefaultModelId = "some-model",
            Auth = new ProviderAuthConfig { Type = "none" }
        };

        IProviderRegistry registry = CreateRegistry([config]);

        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task CurrentSupportsToolCalling_UsesGetService_WhenClientIsActive()
    {
        FakeProviderFactory factory = new(modelId =>
            new ChatClientCapabilities { SupportsToolCalling = true });

        ProviderConfig config = MakeConfig("alpha");
        IProviderRegistry registry = CreateRegistry([config], factory);

        // Activate the client so _activeClient is populated with a FakeChatClient carrying caps.
        await registry.GetCurrentAsync();

        // GetService path is exercised: FakeChatClient returns its baked-in caps.
        Assert.True(registry.CurrentSupportsToolCalling);
    }

    [Fact]
    public void SetModel_Alone_CommitsWithCurrentProvider()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha")]);

        registry.SetModel("new-model-id");
        ProviderSwitchResult? result = registry.CommitPendingSwitch();

        Assert.NotNull(result);
        Assert.Equal("alpha", result.ProviderName);
        Assert.Equal("new-model-id", result.ModelId);
    }

    private sealed class FakeCredentialResolver : ICredentialResolver
    {
        public ValueTask<string?> ResolveAsync(string providerName, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }

    private sealed class FakeProviderFactory : IProviderFactory
    {
        private readonly Func<string, ChatClientCapabilities>? _capabilitiesFunc;

        public FakeProviderFactory(Func<string, ChatClientCapabilities>? capabilitiesFunc = null)
        {
            _capabilitiesFunc = capabilitiesFunc;
        }

        public string AdapterName => "openai";
        public string DisplayName => "OpenAI";
        public string DefaultModelId => "gpt-4o";
        public string DefaultEnvVar => "OPENAI_API_KEY";

        public ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not needed in provider-registry tests.");

        public ChatClientCapabilities GetCapabilities(string modelId) =>
            _capabilitiesFunc?.Invoke(modelId) ?? new ChatClientCapabilities();

        public ValueTask<IChatClient> CreateAsync(
            ProviderConfig config,
            string? apiKey,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IChatClient>(new FakeChatClient(GetCapabilities(config.DefaultModelId ?? string.Empty)));
    }

    private sealed class FakeChatClient(ChatClientCapabilities? caps = null) : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientCapabilities) ? caps : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public void Dispose() { }
    }

    private sealed class FakeProviderExtension(
        string providerName,
        IReadOnlyList<ModelInfo>? models = null) : IProviderExtension
    {
        private readonly IReadOnlyList<ModelInfo> _models = models ?? [];

        public string ProviderName => providerName;
        public bool IsApplicable() => true;
        public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_models);
        public IProviderFactory CreateFactory() => new FakeProviderFactory();
    }

    /// <summary>
    /// A provider extension that fails the test if <see cref="ListModelsAsync"/> is called.
    /// Used to assert that registration performs no network I/O (ADR-007).
    /// </summary>
    private sealed class NoNetworkProviderExtension(string providerName) : IProviderExtension
    {
        public string ProviderName => providerName;
        public bool IsApplicable() => true;
        public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ListModelsAsync must not be called during registration (ADR-007).");
        public IProviderFactory CreateFactory() => new FakeProviderFactory();
    }

    [Fact]
    public async Task RegisterExtensionAsync_AddsProviderToGetAll()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha")]);
        FakeProviderExtension extension = new("ext-provider",
            [new ModelInfo { Id = "ext-model-1", Capabilities = new ChatClientCapabilities() }]);

        await registry.RegisterExtensionAsync(extension);

        IReadOnlyList<ProviderConfig> all = registry.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, c => c.Name == "ext-provider");
        ProviderConfig extConfig = Assert.Single(all, c => c.Name == "ext-provider");
        // DefaultModelId comes from factory.DefaultModelId (no network I/O at registration time).
        Assert.Equal("gpt-4o", extConfig.DefaultModelId);
        Assert.Equal("none", extConfig.Auth.Type);
    }

    [Fact]
    public async Task RegisterExtensionAsync_SetProviderFindsExtensionProvider()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha")]);
        FakeProviderExtension extension = new("ext-provider");

        await registry.RegisterExtensionAsync(extension);

        // Should not throw
        registry.SetProvider("ext-provider");
        ProviderSwitchResult? result = registry.CommitPendingSwitch();
        Assert.NotNull(result);
        Assert.Equal("ext-provider", result.ProviderName);
    }

    [Fact]
    public async Task RegisterExtensionAsync_ReplacingDuplicateUpdatesEntry()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha")]);
        FakeProviderExtension first = new("ext-provider",
            [new ModelInfo { Id = "model-v1", Capabilities = new ChatClientCapabilities() }]);
        FakeProviderExtension second = new("ext-provider",
            [new ModelInfo { Id = "model-v2", Capabilities = new ChatClientCapabilities() }]);

        await registry.RegisterExtensionAsync(first);
        await registry.RegisterExtensionAsync(second);

        IReadOnlyList<ProviderConfig> all = registry.GetAll();
        // Only one entry for ext-provider
        Assert.Equal(2, all.Count);
        ProviderConfig extConfig = Assert.Single(all, c => c.Name == "ext-provider");
        // DefaultModelId comes from factory.DefaultModelId, not the model list (no network I/O).
        Assert.Equal("gpt-4o", extConfig.DefaultModelId);
    }

    // ADR-007: registration must not perform network I/O
    [Fact]
    public async Task RegisterExtensionAsync_DoesNotCallListModelsAsync()
    {
        // NoNetworkProviderExtension throws if ListModelsAsync is called.
        // If registration were to call it, this test would fail.
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha")]);
        NoNetworkProviderExtension extension = new("ollama");

        await registry.RegisterExtensionAsync(extension);

        // Provider must be registered and discoverable despite no model enumeration.
        Assert.Contains(registry.GetAll(), c => c.Name == "ollama");
    }

    // --- restore-from-store tests ---

    [Fact]
    public void Constructor_Store_RestoresConfiguredProvider()
    {
        ProviderConfig alpha = MakeConfig("alpha");
        ProviderConfig beta = MakeConfig("beta");
        FixedActiveModelStore store = new(new ModelRef("beta", "beta-override-model"));

        IProviderRegistry registry = CreateRegistry([alpha, beta], store: store);

        Assert.Equal("beta", registry.GetCurrentConfig().Name);
        Assert.Equal("beta-override-model", registry.GetCurrentModelId());
    }

    [Fact]
    public void Constructor_Store_CaseInsensitiveProviderMatch()
    {
        ProviderConfig alpha = MakeConfig("Alpha");
        FixedActiveModelStore store = new(new ModelRef("ALPHA", "some-model"));

        IProviderRegistry registry = CreateRegistry([alpha], store: store);

        Assert.Equal("Alpha", registry.GetCurrentConfig().Name);
        Assert.Equal("some-model", registry.GetCurrentModelId());
    }

    [Fact]
    public void Constructor_Store_FallsBackToIndexZero_WhenProviderNotConfigured()
    {
        ProviderConfig alpha = MakeConfig("alpha");
        FixedActiveModelStore store = new(new ModelRef("nonexistent-provider", "some-model"));

        IProviderRegistry registry = CreateRegistry([alpha], store: store);

        // Must not throw; falls back to index 0.
        Assert.Equal("alpha", registry.GetCurrentConfig().Name);
    }

    [Fact]
    public void Constructor_Store_ReturnsNull_DefaultsToIndexZero()
    {
        ProviderConfig alpha = MakeConfig("alpha");
        ProviderConfig beta = MakeConfig("beta");

        IProviderRegistry registry = CreateRegistry([alpha, beta]);

        Assert.Equal("alpha", registry.GetCurrentConfig().Name);
        Assert.Null(registry.GetCurrentModelId());
    }

    private sealed class NullActiveModelStore : IActiveModelStore
    {
        public ModelRef? Load() => null;
        public Task SaveAsync(ModelRef selection, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FixedActiveModelStore(ModelRef? selection) : IActiveModelStore
    {
        public ModelRef? Load() => selection;
        public Task SaveAsync(ModelRef selection, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
