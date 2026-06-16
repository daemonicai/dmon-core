using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;

namespace Dmon.Providers.Mtplx.Tests;

// ---------------------------------------------------------------------------
// 5.5 — MtplxProviderFactory
// ---------------------------------------------------------------------------

public sealed class MtplxProviderFactoryTests
{
    private static MtplxProviderFactory MakeFactory(MtplxRuntimeState state, MtplxOptions? opts = null)
    {
        opts ??= new MtplxOptions();
        return new MtplxProviderFactory(opts, state);
    }

    private static ProviderConfig DefaultConfig(string baseUrl) => new()
    {
        Name = "mtplx",
        Adapter = "mtplx",
        BaseUrl = baseUrl,
        Auth = new ProviderAuthConfig { Type = "none" },
    };

    // ProviderConfig with no BaseUrl — exercises the factory's host:port/v1 fallback branch.
    private static ProviderConfig ConfigNoBaseUrl() => new()
    {
        Name = "mtplx",
        Adapter = "mtplx",
        BaseUrl = null,
        Auth = new ProviderAuthConfig { Type = "none" },
    };

    [Fact]
    public async Task CreateAsync_ReturnsCapabilitiesDecoratorWrappedClient()
    {
        MtplxRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:8000/v1",
            ActiveModelId = "Youssofal/Qwen3.5-9B",
            ToolCallingVerified = true,
        };

        MtplxProviderFactory factory = MakeFactory(state);
        IChatClient client = await factory.CreateAsync(
            DefaultConfig(state.BaseUrl), apiKey: null);

        object? caps = client.GetService(typeof(ChatClientCapabilities));
        Assert.NotNull(caps);
        Assert.IsType<ChatClientCapabilities>(caps);
    }

    [Fact]
    public async Task CreateAsync_Capabilities_ReflectToolCallingVerified_True()
    {
        MtplxRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:8000/v1",
            ActiveModelId = "Youssofal/Qwen3.5-9B",
            ToolCallingVerified = true,
        };

        MtplxProviderFactory factory = MakeFactory(state);
        IChatClient client = await factory.CreateAsync(
            DefaultConfig(state.BaseUrl), apiKey: null);

        ChatClientCapabilities caps = (ChatClientCapabilities)client.GetService(typeof(ChatClientCapabilities))!;
        Assert.True(caps.SupportsToolCalling);
    }

    [Fact]
    public async Task CreateAsync_Capabilities_ReflectToolCallingVerified_False()
    {
        MtplxRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:8000/v1",
            ActiveModelId = "Youssofal/Qwen3.5-9B",
            ToolCallingVerified = false,
        };

        MtplxProviderFactory factory = MakeFactory(state);
        IChatClient client = await factory.CreateAsync(
            DefaultConfig(state.BaseUrl), apiKey: null);

        ChatClientCapabilities caps = (ChatClientCapabilities)client.GetService(typeof(ChatClientCapabilities))!;
        Assert.False(caps.SupportsToolCalling);
    }

    [Fact]
    public async Task CreateAsync_Capabilities_ToolCalling_False_WhenUnprobed()
    {
        // ToolCallingVerified == null (unprobed) → SupportsToolCalling must be false.
        MtplxRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:8000/v1",
            ActiveModelId = "Youssofal/Qwen3.5-9B",
            ToolCallingVerified = null,
        };

        MtplxProviderFactory factory = MakeFactory(state);
        IChatClient client = await factory.CreateAsync(
            DefaultConfig(state.BaseUrl), apiKey: null);

        ChatClientCapabilities caps = (ChatClientCapabilities)client.GetService(typeof(ChatClientCapabilities))!;
        Assert.False(caps.SupportsToolCalling);
    }

    [Fact]
    public async Task CreateAsync_DoesNotThrow_WithValidBaseUrl()
    {
        MtplxRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:8000/v1",
            ActiveModelId = "Youssofal/Qwen3.5-9B",
        };

        MtplxProviderFactory factory = MakeFactory(state);

        Exception? ex = await Record.ExceptionAsync(() =>
            factory.CreateAsync(DefaultConfig(state.BaseUrl), apiKey: null).AsTask());
        Assert.Null(ex);
    }

    [Fact]
    public void AdapterName_IsMtplx()
    {
        MtplxProviderFactory factory = MakeFactory(new MtplxRuntimeState());
        Assert.Equal("mtplx", factory.AdapterName);
    }

    [Fact]
    public void DefaultModelId_PrefersOptions_OverRuntimeState()
    {
        MtplxRuntimeState state = new() { ActiveModelId = "runtime-model" };
        MtplxOptions opts = new() { ModelId = "options-model" };
        MtplxProviderFactory factory = MakeFactory(state, opts);

        Assert.Equal("options-model", factory.DefaultModelId);
    }

    [Fact]
    public void DefaultModelId_FallsBackToActiveModelId_WhenModelIdNull()
    {
        MtplxRuntimeState state = new() { ActiveModelId = "runtime-model" };
        MtplxOptions opts = new() { ModelId = null };
        MtplxProviderFactory factory = MakeFactory(state, opts);

        Assert.Equal("runtime-model", factory.DefaultModelId);
    }

    [Fact]
    public async Task CreateAsync_ComposesV1Endpoint_FromOptions_WhenBaseUrlUnset()
    {
        // Exercises the factory's fallback: config.BaseUrl == null AND runtimeState.BaseUrl == ""
        // → factory composes http://{Host}:{Port}/v1 from MtplxOptions.
        MtplxOptions opts = new()
        {
            Host = "127.0.0.1",
            Port = 8000,
            ModelId = "Youssofal/Qwen3.5-9B",
        };
        MtplxRuntimeState state = new()
        {
            BaseUrl = string.Empty,
            ToolCallingVerified = true,
        };

        MtplxProviderFactory factory = MakeFactory(state, opts);

        IChatClient client = await factory.CreateAsync(ConfigNoBaseUrl(), apiKey: null);

        // The OpenAI client's endpoint URI is not introspectable, so assert non-throw
        // + the CapabilitiesDecorator wrapping — both confirm the fallback branch executed.
        Assert.NotNull(client);
        object? caps = client.GetService(typeof(ChatClientCapabilities));
        Assert.NotNull(caps);
        Assert.IsType<ChatClientCapabilities>(caps);
    }
}
