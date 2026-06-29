using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Providers.Mlx.Tests;

// ---------------------------------------------------------------------------
// 4.2/4.3 — AddMlx composition verbs + keyed-runtime resolvability
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal test double for <see cref="IProviderRegistration"/> backed by real
/// <see cref="ServiceCollection"/> and <see cref="ConfigurationManager"/> instances.
/// Defined locally to avoid a cross-project test dependency on Dmon.Providers.Mtplx.Tests.
/// </summary>
internal sealed class FakeProviderRegistration : IProviderRegistration
{
    public IServiceCollection Services { get; } = new ServiceCollection();
    public IConfigurationManager Configuration { get; } = new ConfigurationManager();
}

public sealed class AddMlxExtensionsTests
{
    // ---------------------------------------------------------------------------
    // Resolvability — keyed singletons must resolve by their declared key
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddMlxFirstline_RegistersKeyedSingleton_ResolvableByFirstlineKey()
    {
        FakeProviderRegistration reg = new();

        reg.AddMlxFirstline();

        MlxProviderExtension? ext = reg.Services
            .BuildServiceProvider()
            .GetKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Firstline);

        Assert.NotNull(ext);
    }

    [Fact]
    public void AddMlxEscalation_RegistersKeyedSingleton_ResolvableByEscalationKey()
    {
        FakeProviderRegistration reg = new();

        reg.AddMlxEscalation();

        MlxProviderExtension? ext = reg.Services
            .BuildServiceProvider()
            .GetKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation);

        Assert.NotNull(ext);
    }

    [Fact]
    public void BothVerbs_ReturnDistinctInstances()
    {
        FakeProviderRegistration reg = new();

        reg.AddMlxFirstline();
        reg.AddMlxEscalation();

        ServiceProvider sp = reg.Services.BuildServiceProvider();
        MlxProviderExtension firstline = sp.GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Firstline);
        MlxProviderExtension escalation = sp.GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation);

        Assert.NotSame(firstline, escalation);
    }

    // ---------------------------------------------------------------------------
    // Default options — ports and model ids match the spec
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddMlxFirstline_DefaultOptions_HasPort8800()
    {
        FakeProviderRegistration reg = new();
        reg.AddMlxFirstline();

        MlxProviderExtension ext = reg.Services
            .BuildServiceProvider()
            .GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Firstline);

        Assert.Equal(8800, ext.Options.Port);
    }

    [Fact]
    public void AddMlxEscalation_DefaultOptions_HasPort8810()
    {
        FakeProviderRegistration reg = new();
        reg.AddMlxEscalation();

        MlxProviderExtension ext = reg.Services
            .BuildServiceProvider()
            .GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation);

        Assert.Equal(8810, ext.Options.Port);
    }

    [Fact]
    public void AddMlxFirstline_DefaultOptions_HasE4BModelId()
    {
        FakeProviderRegistration reg = new();
        reg.AddMlxFirstline();

        MlxProviderExtension ext = reg.Services
            .BuildServiceProvider()
            .GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Firstline);

        Assert.Equal("mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit", ext.Options.ModelId);
    }

    [Fact]
    public void AddMlxEscalation_DefaultOptions_Has26BModelId()
    {
        FakeProviderRegistration reg = new();
        reg.AddMlxEscalation();

        MlxProviderExtension ext = reg.Services
            .BuildServiceProvider()
            .GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation);

        Assert.Equal("mlx-community/gemma-4-26B-A4B-it-qat-nvfp4", ext.Options.ModelId);
    }

    // ---------------------------------------------------------------------------
    // Explicit options overrides
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddMlxFirstline_ExplicitOptions_HonoursPortOverride()
    {
        FakeProviderRegistration reg = new();
        MlxRuntimeOptions customOpts = MlxRuntimeOptions.Firstline("mlx-community/custom-e4b", port: 9000);

        reg.AddMlxFirstline(customOpts);

        MlxProviderExtension ext = reg.Services
            .BuildServiceProvider()
            .GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Firstline);

        Assert.Equal(9000, ext.Options.Port);
        Assert.Equal("mlx-community/custom-e4b", ext.Options.ModelId);
    }

    [Fact]
    public void AddMlxEscalation_ExplicitOptions_HonoursPortOverride()
    {
        FakeProviderRegistration reg = new();
        MlxRuntimeOptions customOpts = MlxRuntimeOptions.Escalation("mlx-community/custom-26B", port: 9010);

        reg.AddMlxEscalation(customOpts);

        MlxProviderExtension ext = reg.Services
            .BuildServiceProvider()
            .GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation);

        Assert.Equal(9010, ext.Options.Port);
        Assert.Equal("mlx-community/custom-26B", ext.Options.ModelId);
    }

    // ---------------------------------------------------------------------------
    // Keyed-only contract — runtimes must NOT appear in the IProviderExtension registry
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddMlxFirstline_DoesNotRegisterAsIProviderExtension()
    {
        FakeProviderRegistration reg = new();
        reg.AddMlxFirstline();

        IEnumerable<IProviderExtension> providers = reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>();

        Assert.DoesNotContain(providers, p => p is MlxProviderExtension);
    }

    [Fact]
    public void AddMlxEscalation_DoesNotRegisterAsIProviderExtension()
    {
        FakeProviderRegistration reg = new();
        reg.AddMlxEscalation();

        IEnumerable<IProviderExtension> providers = reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>();

        Assert.DoesNotContain(providers, p => p is MlxProviderExtension);
    }

    // ---------------------------------------------------------------------------
    // Fluency — verbs return the registration for chaining
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddMlxFirstline_ReturnsRegistration_ForFluency()
    {
        FakeProviderRegistration reg = new();
        FakeProviderRegistration result = reg.AddMlxFirstline();
        Assert.Same(reg, result);
    }

    [Fact]
    public void AddMlxEscalation_ReturnsRegistration_ForFluency()
    {
        FakeProviderRegistration reg = new();
        FakeProviderRegistration result = reg.AddMlxEscalation();
        Assert.Same(reg, result);
    }

    // ---------------------------------------------------------------------------
    // Singleton semantics — same instance on repeated resolution
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddMlxFirstline_ReturnsSameInstance_OnRepeatedResolution()
    {
        FakeProviderRegistration reg = new();
        reg.AddMlxFirstline();

        ServiceProvider sp = reg.Services.BuildServiceProvider();
        MlxProviderExtension a = sp.GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Firstline);
        MlxProviderExtension b = sp.GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Firstline);

        Assert.Same(a, b);
    }

    [Fact]
    public void AddMlxEscalation_ReturnsSameInstance_OnRepeatedResolution()
    {
        FakeProviderRegistration reg = new();
        reg.AddMlxEscalation();

        ServiceProvider sp = reg.Services.BuildServiceProvider();
        MlxProviderExtension a = sp.GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation);
        MlxProviderExtension b = sp.GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation);

        Assert.Same(a, b);
    }
}
