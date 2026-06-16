using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Providers.Mtplx.Tests;

// ---------------------------------------------------------------------------
// 5.6 — UseMtplx composition verbs
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

public sealed class UseMtplxStringOverloadTests
{
    [Fact]
    public void UseMtplx_WithModel_RegistersMtplxProviderExtension()
    {
        FakeProviderRegistration reg = new();

        reg.UseMtplx("Youssofal/Qwen3.5-9B");

        IEnumerable<IProviderExtension> extensions = reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>();

        Assert.Contains(extensions, e => e.ProviderName == "mtplx");
    }

    [Fact]
    public void UseMtplx_WithModel_SetsActiveModelKey()
    {
        FakeProviderRegistration reg = new();

        reg.UseMtplx("Youssofal/Qwen3.5-9B");

        string? activeModel = reg.Configuration[ConfigurationKeys.ActiveModel];
        Assert.Equal("mtplx/Youssofal/Qwen3.5-9B", activeModel);
    }

    [Fact]
    public void UseMtplx_WithModel_DefaultIsOverridable()
    {
        FakeProviderRegistration reg = new();

        reg.UseMtplx("Youssofal/Qwen3.5-9B");
        reg.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>(ConfigurationKeys.ActiveModel, "anthropic/claude-sonnet-4-6"),
        ]);

        string? activeModel = reg.Configuration[ConfigurationKeys.ActiveModel];
        Assert.Equal("anthropic/claude-sonnet-4-6", activeModel);
    }
}

public sealed class UseMtplxOptionsOverloadTests
{
    [Fact]
    public void UseMtplx_WithOptions_RegistersMtplxProviderExtension()
    {
        FakeProviderRegistration reg = new();
        MtplxOptions options = new() { ModelId = "Youssofal/Qwen3.5-9B" };

        reg.UseMtplx(options);

        IEnumerable<IProviderExtension> extensions = reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>();

        Assert.Contains(extensions, e => e.ProviderName == "mtplx");
    }

    [Fact]
    public void UseMtplx_WithOptions_WithModelId_SetsActiveModelKey()
    {
        FakeProviderRegistration reg = new();
        MtplxOptions options = new() { ModelId = "m" };

        reg.UseMtplx(options);

        string? activeModel = reg.Configuration[ConfigurationKeys.ActiveModel];
        Assert.Equal("mtplx/m", activeModel);
    }

    [Fact]
    public void UseMtplx_WithOptions_NullModelId_DoesNotSetActiveModelKey()
    {
        FakeProviderRegistration reg = new();
        MtplxOptions options = new() { ModelId = null };

        reg.UseMtplx(options);

        // ModelId is null → UseModel must not be called → key absent.
        string? activeModel = reg.Configuration[ConfigurationKeys.ActiveModel];
        Assert.Null(activeModel);
    }

    [Fact]
    public void UseMtplx_WithOptions_EmptyModelId_DoesNotSetActiveModelKey()
    {
        FakeProviderRegistration reg = new();
        // MtplxOptions.ModelId defaults to null, which is falsy for IsNullOrEmpty — also cover
        // an options instance with no ModelId set (the parameterless record ctor leaves it null).
        MtplxOptions options = new();

        reg.UseMtplx(options);

        string? activeModel = reg.Configuration[ConfigurationKeys.ActiveModel];
        Assert.Null(activeModel);
    }

    [Fact]
    public void UseMtplx_WithOptions_DefaultIsOverridable()
    {
        FakeProviderRegistration reg = new();
        MtplxOptions options = new() { ModelId = "Youssofal/Qwen3.5-9B" };

        reg.UseMtplx(options);
        reg.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>(ConfigurationKeys.ActiveModel, "anthropic/claude-sonnet-4-6"),
        ]);

        string? activeModel = reg.Configuration[ConfigurationKeys.ActiveModel];
        Assert.Equal("anthropic/claude-sonnet-4-6", activeModel);
    }
}
