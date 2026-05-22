using Daemon.Core.Providers;
using Daemon.Protocol.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daemon.Core.Tests.Providers;

public sealed class ProviderRegistryTests
{
    private static ProviderConfig MakeConfig(string name, bool toolCalling = false, bool reasoning = false) =>
        new()
        {
            Name = name,
            Adapter = "openai",
            DefaultModelId = $"{name}-model",
            Auth = new ProviderAuthConfig { Type = "none" },
            Capabilities = new ProviderCapabilities
            {
                ToolCalling = toolCalling,
                Reasoning = reasoning,
                ContextWindow = 8192,
                MaxTokens = 4096
            }
        };

    private static IProviderRegistry CreateRegistry(
        IEnumerable<ProviderConfig> configs,
        ICredentialResolver? resolver = null)
    {
        resolver ??= new FakeCredentialResolver();
        return new ProviderRegistry(
            configs,
            resolver,
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
        ProviderSwitchedEvent? evt = registry.CommitPendingSwitch();

        Assert.NotNull(evt);
        Assert.Equal("beta", evt.Name);
        Assert.True(evt.EffectiveNextTurn);
        Assert.Equal("beta", registry.GetCurrentConfig().Name);
    }

    [Fact]
    public void SetProvider_WithModelId_OverridesModelInEvent()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha"), MakeConfig("beta")]);

        registry.SetProvider("beta", "override-model-id");
        ProviderSwitchedEvent? evt = registry.CommitPendingSwitch();

        Assert.NotNull(evt);
        Assert.Equal("override-model-id", evt.Model);
    }

    [Fact]
    public void CommitPendingSwitch_ReturnsNull_WhenNoPendingSwitch()
    {
        IProviderRegistry registry = CreateRegistry([MakeConfig("alpha")]);

        ProviderSwitchedEvent? evt = registry.CommitPendingSwitch();

        Assert.Null(evt);
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
    public void CurrentSupportsToolCalling_ReflectsActiveConfig()
    {
        ProviderConfig withTool = MakeConfig("a", toolCalling: true);
        ProviderConfig withoutTool = MakeConfig("b", toolCalling: false);
        IProviderRegistry registry = CreateRegistry([withTool, withoutTool]);

        Assert.True(registry.CurrentSupportsToolCalling);

        registry.SetProvider("b");
        registry.CommitPendingSwitch();

        Assert.False(registry.CurrentSupportsToolCalling);
    }

    [Fact]
    public void CurrentSupportsReasoning_ReflectsActiveConfig()
    {
        ProviderConfig withReasoning = MakeConfig("a", reasoning: true);
        ProviderConfig withoutReasoning = MakeConfig("b", reasoning: false);
        IProviderRegistry registry = CreateRegistry([withReasoning, withoutReasoning]);

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
    public void Constructor_EmptyConfigs_Throws()
    {
        void Act() => CreateRegistry([]);

        Assert.Throws<InvalidOperationException>(Act);
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

    private sealed class FakeCredentialResolver : ICredentialResolver
    {
        public ValueTask<string?> ResolveAsync(string providerName, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }
}
