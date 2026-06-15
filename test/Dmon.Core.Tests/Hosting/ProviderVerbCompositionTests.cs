using Dmon.Abstractions.Providers;
using Dmon.Core.Providers;
using Dmon.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Core.Tests.Hosting;

/// <summary>
/// Tests for the Group-4 provider verb composition surface (tasks 4.3, 4.7).
/// Verifies that Use&lt;Provider&gt;() verbs register factories and that
/// ProviderRegistry discovers them from DI.
/// </summary>
public sealed class ProviderVerbCompositionTests
{
    private static DmonBuiltHost BuildHost(Action<DmonHostBuilder> configure)
    {
        DmonHostBuilder builder = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry();
        configure(builder);
        return builder.Build();
    }

    // ── 4.3 — UseAnthropic registers IProviderFactory ────────────────────────

    [Fact]
    public void UseAnthropic_RegistersAnthropicFactory()
    {
        DmonBuiltHost host = BuildHost(b => b.UseAnthropic());

        IEnumerable<IProviderFactory> factories = host.Services.GetServices<IProviderFactory>();

        Assert.Contains(factories, f => f.AdapterName == "anthropic");
    }

    // ── 4.3 — UseOpenAI registers IProviderFactory ───────────────────────────

    [Fact]
    public void UseOpenAI_RegistersOpenAIFactory()
    {
        DmonBuiltHost host = BuildHost(b => b.UseOpenAI());

        IEnumerable<IProviderFactory> factories = host.Services.GetServices<IProviderFactory>();

        Assert.Contains(factories, f => f.AdapterName == "openai");
    }

    // ── 4.3 — UseGemini registers IProviderFactory ───────────────────────────

    [Fact]
    public void UseGemini_RegistersGeminiFactory()
    {
        DmonBuiltHost host = BuildHost(b => b.UseGemini());

        IEnumerable<IProviderFactory> factories = host.Services.GetServices<IProviderFactory>();

        Assert.Contains(factories, f => f.AdapterName == "gemini");
    }

    // ── 4.3 — UseOllama registers IProviderExtension ────────────────────────

    [Fact]
    public void UseOllama_RegistersOllamaProviderExtension()
    {
        DmonBuiltHost host = BuildHost(b => b.UseOllama());

        IEnumerable<IProviderExtension> extensions = host.Services.GetServices<IProviderExtension>();

        Assert.Contains(extensions, e => e.ProviderName == "Ollama");
    }

    // ── 4.7 — stock composition registers all four providers ─────────────────

    /// <summary>
    /// The stock default composition (UseAnthropic + UseOpenAI + UseGemini + UseOllama)
    /// registers all three cloud factories and one local extension.
    /// ProviderRegistry is resolvable (DI-discovery of IProviderFactory singletons works).
    /// </summary>
    [Fact]
    public void StockComposition_AllFourProviders_RegistryIsResolvable()
    {
        DmonBuiltHost host = BuildHost(b => b
            .UseAnthropic()
            .UseOpenAI()
            .UseGemini()
            .UseOllama());

        IEnumerable<IProviderFactory> factories = host.Services.GetServices<IProviderFactory>();
        IProviderRegistry registry = host.Services.GetRequiredService<IProviderRegistry>();

        Assert.Contains(factories, f => f.AdapterName == "anthropic");
        Assert.Contains(factories, f => f.AdapterName == "openai");
        Assert.Contains(factories, f => f.AdapterName == "gemini");
        Assert.NotNull(registry);
    }

    // ── 4.4 — UseAnthropic(model) sets the active model ─────────────────────

    [Fact]
    public void UseAnthropic_WithModel_SetsActiveModel()
    {
        DmonBuiltHost host = BuildHost(b => b.UseAnthropic("claude-sonnet-4-6"));

        IActiveModelStore store = host.Services.GetRequiredService<IActiveModelStore>();
        ModelRef? loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("anthropic", loaded.Provider);
        Assert.Equal("claude-sonnet-4-6", loaded.Model);
    }

    // ── 4.4 — UseOpenAI(model) sets the active model ────────────────────────

    [Fact]
    public void UseOpenAI_WithModel_SetsActiveModel()
    {
        DmonBuiltHost host = BuildHost(b => b.UseOpenAI("gpt-4o"));

        IActiveModelStore store = host.Services.GetRequiredService<IActiveModelStore>();
        ModelRef? loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("openai", loaded.Provider);
        Assert.Equal("gpt-4o", loaded.Model);
    }

    // ── 4.4 — UseGemini(model) sets the active model ────────────────────────

    [Fact]
    public void UseGemini_WithModel_SetsActiveModel()
    {
        DmonBuiltHost host = BuildHost(b => b.UseGemini("gemini-2.5-pro"));

        IActiveModelStore store = host.Services.GetRequiredService<IActiveModelStore>();
        ModelRef? loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("gemini", loaded.Provider);
        Assert.Equal("gemini-2.5-pro", loaded.Model);
    }

    // ── No vendor SDK in Core: IProviderFactory singletons come from provider packages ──

    /// <summary>
    /// Without calling any Use&lt;Provider&gt;() verb, no IProviderFactory singletons
    /// are registered. The old AddDmonProviders baked them in; now they must be explicit.
    /// </summary>
    [Fact]
    public void Build_WithNoUseProviderVerbs_NoFactoriesRegistered()
    {
        DmonBuiltHost host = BuildHost(_ => { });

        IEnumerable<IProviderFactory> factories = host.Services.GetServices<IProviderFactory>();

        Assert.Empty(factories);
    }
}
