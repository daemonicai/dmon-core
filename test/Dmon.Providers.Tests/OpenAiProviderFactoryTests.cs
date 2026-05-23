using Dmon.Core.Providers;
using Dmon.Providers;

namespace Dmon.Providers.Tests;

public sealed class OpenAiProviderFactoryTests
{
    private readonly OpenAiProviderFactory _factory = new();

    [Fact]
    public void GetCapabilities_O1Model_ReturnsReasoningSupport()
    {
        ChatClientCapabilities caps = _factory.GetCapabilities("o1-preview");
        Assert.True(caps.SupportsToolCalling);
        Assert.True(caps.SupportsReasoning);
    }

    [Fact]
    public void GetCapabilities_O3Model_ReturnsReasoningSupport()
    {
        ChatClientCapabilities caps = _factory.GetCapabilities("o3-mini");
        Assert.True(caps.SupportsToolCalling);
        Assert.True(caps.SupportsReasoning);
    }

    [Fact]
    public void GetCapabilities_Gpt4oModel_ReturnsToolCallingNoReasoning()
    {
        ChatClientCapabilities caps = _factory.GetCapabilities("gpt-4o");
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
}
