using Dmon.Abstractions.Providers;
using Dmon.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Providers.Mlx.Tests;

// ---------------------------------------------------------------------------
// 2.1/2.2/2.3 — UseMlx single-model active-provider composition verb
// Reuses FakeProviderRegistration from AddMlxExtensionsTests.cs (same namespace).
// ---------------------------------------------------------------------------

public sealed class UseMlxTests
{
    private const string ModelId = "mlx-community/some-model-4bit";

    // ── 2.1 registers an active MlxProviderExtension ─────────────────────────

    [Fact]
    public void UseMlx_WithModel_RegistersActiveMlxProviderExtension()
    {
        FakeProviderRegistration reg = new();

        reg.UseMlx(ModelId);

        IEnumerable<IProviderExtension> extensions = reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>();

        Assert.Contains(extensions, e => e is MlxProviderExtension && e.ProviderName == "mlx");
    }

    // ── 2.1 sets mlx/<modelId> as the default active model ───────────────────

    [Fact]
    public void UseMlx_WithModel_SetsActiveModelKey()
    {
        FakeProviderRegistration reg = new();

        reg.UseMlx(ModelId);

        string? activeModel = reg.Configuration["activeModel"];
        Assert.Equal("mlx/mlx-community/some-model-4bit", activeModel);
    }

    [Fact]
    public void UseMlx_WithModel_DefaultIsOverridable()
    {
        FakeProviderRegistration reg = new();

        reg.UseMlx(ModelId);
        reg.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>("activeModel", "anthropic/claude-sonnet-4-6"),
        ]);

        string? activeModel = reg.Configuration["activeModel"];
        Assert.Equal("anthropic/claude-sonnet-4-6", activeModel);
    }

    // ── 2.2 convenience overload defaults the port to 8666 ───────────────────

    [Fact]
    public void UseMlx_WithModel_DefaultsPortTo8666()
    {
        FakeProviderRegistration reg = new();

        reg.UseMlx(ModelId);

        MlxProviderExtension ext = SingleMlxExtension(reg);
        Assert.Equal(8666, ext.Options.Port);
        Assert.Equal(ModelId, ext.Options.ModelId);
    }

    // ── 2.2 explicit port argument overrides the default ─────────────────────

    [Fact]
    public void UseMlx_WithExplicitPort_OverridesDefault()
    {
        FakeProviderRegistration reg = new();

        reg.UseMlx(ModelId, port: 8700);

        MlxProviderExtension ext = SingleMlxExtension(reg);
        Assert.Equal(8700, ext.Options.Port);
        Assert.Equal(ModelId, ext.Options.ModelId);
    }

    // ── 2.2 options overload preserves Port/ModelId unchanged ────────────────

    [Fact]
    public void UseMlx_WithOptions_PreservesPortAndModelId()
    {
        FakeProviderRegistration reg = new();
        MlxRuntimeOptions options = new() { ModelId = ModelId, Port = 9123 };

        reg.UseMlx(options);

        MlxProviderExtension ext = SingleMlxExtension(reg);
        Assert.Equal(9123, ext.Options.Port);
        Assert.Equal(ModelId, ext.Options.ModelId);
    }

    [Fact]
    public void UseMlx_WithOptions_SetsActiveModelKey()
    {
        FakeProviderRegistration reg = new();
        MlxRuntimeOptions options = new() { ModelId = ModelId, Port = 9123 };

        reg.UseMlx(options);

        string? activeModel = reg.Configuration["activeModel"];
        Assert.Equal("mlx/mlx-community/some-model-4bit", activeModel);
    }

    // ── 2.3 keyed verbs are unaffected — not active-provider candidates ──────

    [Fact]
    public void KeyedVerbs_RegisterKeyedSingletons_ButNotActiveProviders()
    {
        FakeProviderRegistration reg = new();

        reg.AddMlxFirstline();
        reg.AddMlxEscalation();

        ServiceProvider sp = reg.Services.BuildServiceProvider();

        Assert.NotNull(sp.GetKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Firstline));
        Assert.NotNull(sp.GetKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation));

        IEnumerable<IProviderExtension> providers = sp.GetServices<IProviderExtension>();
        Assert.DoesNotContain(providers, p => p is MlxProviderExtension);
    }

    private static MlxProviderExtension SingleMlxExtension(FakeProviderRegistration reg)
        => (MlxProviderExtension)reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>()
            .Single(e => e is MlxProviderExtension);
}
