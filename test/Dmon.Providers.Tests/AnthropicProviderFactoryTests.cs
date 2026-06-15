using Dmon.Abstractions.Providers;
using Dmon.Providers.Anthropic;

namespace Dmon.Providers.Tests;

public sealed class AnthropicProviderFactoryTests
{
    private readonly AnthropicProviderFactory _factory = new();

    [Fact]
    public void GetCapabilities_KnownClaude4OpusModel_ReturnsReasoningSupport()
    {
        ChatClientCapabilities caps = _factory.GetCapabilities("claude-opus-4-7");
        Assert.True(caps.SupportsToolCalling);
        Assert.True(caps.SupportsReasoning);
    }

    [Fact]
    public void GetCapabilities_KnownClaude3Model_ReturnsNoReasoning()
    {
        ChatClientCapabilities caps = _factory.GetCapabilities("claude-3-5-sonnet-20241022");
        Assert.True(caps.SupportsToolCalling);
        Assert.False(caps.SupportsReasoning);
    }

    [Fact]
    public void GetCapabilities_UnknownModel_ReturnsConservativeDefaults()
    {
        ChatClientCapabilities caps = _factory.GetCapabilities("unknown-model-xyz");
        Assert.False(caps.SupportsToolCalling);
        Assert.False(caps.SupportsReasoning);
    }

    [Fact]
    public void AdapterName_IsAnthropicLowercase()
    {
        Assert.Equal("anthropic", _factory.AdapterName);
    }
}
