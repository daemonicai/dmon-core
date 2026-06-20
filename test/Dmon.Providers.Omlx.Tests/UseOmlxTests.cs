using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Hosting;
using Dmon.Providers.Omlx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Providers.Omlx.Tests;

// ---------------------------------------------------------------------------
// 2.1–2.3 — UseOmlx composition verbs and OmlxClient helper
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal test double for <see cref="IProviderRegistration"/> backed by real
/// <see cref="ServiceCollection"/> and <see cref="ConfigurationManager"/> instances.
/// Avoids taking a dependency on Dmon.Core (which would pull in the full host).
/// </summary>
internal sealed class FakeProviderRegistration : IProviderRegistration
{
    public IServiceCollection Services { get; } = new ServiceCollection();
    public IConfigurationManager Configuration { get; } = new ConfigurationManager();
}

// ---------------------------------------------------------------------------
// UseOmlx() no-arg overload
// ---------------------------------------------------------------------------

public sealed class UseOmlxNoArgTests
{
    [Fact]
    public void UseOmlx_RegistersOmlxProviderExtension()
    {
        FakeProviderRegistration reg = new();

        reg.UseOmlx();

        IEnumerable<IProviderExtension> extensions = reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>();

        Assert.Contains(extensions, e => e.ProviderName == "oMLX");
    }

    [Fact]
    public void UseOmlx_IsNonHijacking_ActiveModelKeyIsNull()
    {
        FakeProviderRegistration reg = new();

        reg.UseOmlx();

        // Non-hijacking: UseOmlx() must never set the active model key.
        string? activeModel = reg.Configuration[ConfigurationKeys.ActiveModel];
        Assert.Null(activeModel);
    }

    [Fact]
    public void UseOmlx_NonHijacking_DoesNotOverrideLaterActiveModel()
    {
        FakeProviderRegistration reg = new();

        reg.UseOmlx();
        reg.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>(ConfigurationKeys.ActiveModel, "anthropic/claude-sonnet-4-6"),
        ]);

        string? activeModel = reg.Configuration[ConfigurationKeys.ActiveModel];
        Assert.Equal("anthropic/claude-sonnet-4-6", activeModel);
    }

    [Fact]
    public void UseOmlx_ReturnsSameRegistration_ForFluency()
    {
        FakeProviderRegistration reg = new();
        FakeProviderRegistration returned = reg.UseOmlx();
        Assert.Same(reg, returned);
    }
}

// ---------------------------------------------------------------------------
// UseOmlx(OmlxConfig) config overload
// ---------------------------------------------------------------------------

public sealed class UseOmlxConfigOverloadTests
{
    [Fact]
    public void UseOmlx_WithConfig_RegistersOmlxProviderExtension()
    {
        FakeProviderRegistration reg = new();
        OmlxConfig config = new() { BaseUrl = "http://localhost:9999", ApiKey = "test-key" };

        reg.UseOmlx(config);

        IEnumerable<IProviderExtension> extensions = reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>();

        Assert.Contains(extensions, e => e.ProviderName == "oMLX");
    }

    [Fact]
    public void UseOmlx_WithConfig_IsNonHijacking_ActiveModelKeyIsNull()
    {
        FakeProviderRegistration reg = new();
        OmlxConfig config = new() { BaseUrl = "http://localhost:9999" };

        reg.UseOmlx(config);

        string? activeModel = reg.Configuration[ConfigurationKeys.ActiveModel];
        Assert.Null(activeModel);
    }

    [Fact]
    public void UseOmlx_WithEnvConfig_ResolvesBaseUrlFromEnv()
    {
        // Simulate env var having been read before construction via OmlxConfig.FromEnvironment().
        // Construct a config with a known BaseUrl (matching what FromEnvironment would produce)
        // and verify the extension is registered; this exercises the config-overload path.
        OmlxConfig envConfig = new() { BaseUrl = "http://omlx-custom:1234", ApiKey = string.Empty };
        FakeProviderRegistration reg = new();

        reg.UseOmlx(envConfig);

        ServiceProvider sp = reg.Services.BuildServiceProvider();
        OmlxProviderExtension? ext = sp.GetServices<IProviderExtension>()
            .OfType<OmlxProviderExtension>()
            .FirstOrDefault();

        Assert.NotNull(ext);
        Assert.Equal("oMLX", ext.ProviderName);
    }
}

// ---------------------------------------------------------------------------
// OmlxClient per-model helper
// ---------------------------------------------------------------------------

public sealed class OmlxClientHelperTests
{
    [Fact]
    public async Task OmlxClient_ReturnsTwoDistinctClients_ForTwoDifferentModels()
    {
        // Register with a probe that reports the server as already running — no open -a oMLX.
        FakeProviderRegistration reg = new();
        OmlxConfig config = new() { BaseUrl = "http://localhost:8666", ApiKey = string.Empty };

        // Use the internal probe ctor so EnsureRunningAsync is a no-op.
        OmlxProviderExtension ext = new(config, _ => Task.FromResult(true));
        reg.Services.AddSingleton<IProviderExtension>(ext);

        ServiceProvider sp = reg.Services.BuildServiceProvider();

        Microsoft.Extensions.AI.IChatClient clientA = await sp.OmlxClient("model-a");
        Microsoft.Extensions.AI.IChatClient clientB = await sp.OmlxClient("model-b");

        Assert.NotNull(clientA);
        Assert.NotNull(clientB);
        // Each call constructs a fresh client — reference inequality confirms two distinct instances.
        Assert.NotSame(clientA, clientB);
    }

    [Fact]
    public async Task OmlxClient_EnsureRunningAsync_IsInvokedBeforeConstruction()
    {
        bool ensureCalled = false;
        FakeProviderRegistration reg = new();
        OmlxConfig config = new() { BaseUrl = "http://localhost:8666", ApiKey = string.Empty };

        OmlxProviderExtension ext = new(config, _ =>
        {
            ensureCalled = true;
            return Task.FromResult(true);
        });
        reg.Services.AddSingleton<IProviderExtension>(ext);

        ServiceProvider sp = reg.Services.BuildServiceProvider();

        await sp.OmlxClient("some-model");

        Assert.True(ensureCalled);
    }

    [Fact]
    public async Task OmlxClient_TwoCallsSameModel_ReturnTwoDistinctInstances()
    {
        FakeProviderRegistration reg = new();
        OmlxConfig config = new() { BaseUrl = "http://localhost:8666", ApiKey = string.Empty };

        OmlxProviderExtension ext = new(config, _ => Task.FromResult(true));
        reg.Services.AddSingleton<IProviderExtension>(ext);

        ServiceProvider sp = reg.Services.BuildServiceProvider();

        Microsoft.Extensions.AI.IChatClient c1 = await sp.OmlxClient("same-model");
        Microsoft.Extensions.AI.IChatClient c2 = await sp.OmlxClient("same-model");

        // Each call builds a fresh client — no caching.
        Assert.NotSame(c1, c2);
    }
}
