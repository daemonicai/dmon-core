using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Providers.LlamaCpp;
using Dmon.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Providers.LlamaCpp.Tests;

// ---------------------------------------------------------------------------
// 6.7 — UseLlamaCpp composition verb
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal test double for <see cref="IProviderRegistration"/> backed by real
/// <see cref="ServiceCollection"/> and <see cref="ConfigurationManager"/> instances.
/// This avoids taking a dependency on Dmon.Core (which would pull in the full host).
/// End-to-end configuration-precedence behaviour (runtime override wins at the next
/// turn boundary) is covered by dmon-core's <c>ConfigurationPrecedenceTests</c>.
/// </summary>
internal sealed class FakeProviderRegistration : IProviderRegistration
{
    public IServiceCollection Services { get; } = new ServiceCollection();
    public IConfigurationManager Configuration { get; } = new ConfigurationManager();
}

public sealed class UseLlamaCppStringOverloadTests
{
    // ── (a) registers a LlamaCppProviderExtension ────────────────────────────

    [Fact]
    public void UseLlamaCpp_WithModel_RegistersLlamaCppProviderExtension()
    {
        FakeProviderRegistration reg = new();

        reg.UseLlamaCpp("owner/repo-GGUF");

        IEnumerable<IProviderExtension> extensions = reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>();

        Assert.Contains(extensions, e => e.ProviderName == "llama.cpp");
    }

    // ── (b) sets llamacpp/<modelId> as the default active model ─────────────

    [Fact]
    public void UseLlamaCpp_WithModel_SetsActiveModelKey()
    {
        FakeProviderRegistration reg = new();

        reg.UseLlamaCpp("owner/repo-GGUF");

        // UseModel writes "activeModel" = "llamacpp/<modelId>" into IConfigurationManager.
        string? activeModel = reg.Configuration["activeModel"];
        Assert.Equal("llamacpp/owner/repo-GGUF", activeModel);
    }

    // ── (c) the default is overridable (not pinned) ──────────────────────────

    [Fact]
    public void UseLlamaCpp_WithModel_DefaultIsOverridable()
    {
        FakeProviderRegistration reg = new();

        // Simulate: UseLlamaCpp sets the default, then a later AddInMemoryCollection overrides it.
        reg.UseLlamaCpp("owner/repo-GGUF");
        reg.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>("activeModel", "anthropic/claude-sonnet-4-6"),
        ]);

        // The later source wins — IConfigurationManager resolves last-wins for in-memory sources.
        string? activeModel = reg.Configuration["activeModel"];
        Assert.Equal("anthropic/claude-sonnet-4-6", activeModel);
    }

    // ── model string parsing: bare repo uses default quant ───────────────────

    [Fact]
    public void UseLlamaCpp_BareModel_NoColonParsedAsModelId()
    {
        FakeProviderRegistration reg = new();

        reg.UseLlamaCpp("owner/repo-GGUF");

        string? activeModel = reg.Configuration["activeModel"];
        Assert.Equal("llamacpp/owner/repo-GGUF", activeModel);

        // No colon → Quant must be the record default.
        LlamaCppProviderExtension ext = (LlamaCppProviderExtension)reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>()
            .Single(e => e.ProviderName == "llama.cpp");
        Assert.Equal("Q4_K_M", ext.Options.Quant);
    }

    // ── model string parsing: repo:quant splits on first colon ───────────────

    [Fact]
    public void UseLlamaCpp_ModelWithExplicitQuant_SplitsOnFirstColon()
    {
        FakeProviderRegistration reg = new();

        // The quant is the right-hand side after the first colon; ModelId is the left.
        reg.UseLlamaCpp("owner/repo-GGUF:Q5_K_M");

        string? activeModel = reg.Configuration["activeModel"];
        // Active model key uses ModelId only (left of first colon).
        Assert.Equal("llamacpp/owner/repo-GGUF", activeModel);

        // Quant must carry the right-hand side of the split.
        LlamaCppProviderExtension ext = (LlamaCppProviderExtension)reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>()
            .Single(e => e.ProviderName == "llama.cpp");
        Assert.Equal("Q5_K_M", ext.Options.Quant);
    }
}

public sealed class UseLlamaCppOptionsOverloadTests
{
    // ── (a) full-options overload registers extension ────────────────────────

    [Fact]
    public void UseLlamaCpp_WithOptions_RegistersLlamaCppProviderExtension()
    {
        FakeProviderRegistration reg = new();
        LlamaCppOptions options = new() { ModelId = "owner/repo-GGUF", GpuLayers = 32 };

        reg.UseLlamaCpp(options);

        IEnumerable<IProviderExtension> extensions = reg.Services
            .BuildServiceProvider()
            .GetServices<IProviderExtension>();

        Assert.Contains(extensions, e => e.ProviderName == "llama.cpp");
    }

    // ── (b) full-options overload sets activeModel key ───────────────────────

    [Fact]
    public void UseLlamaCpp_WithOptions_SetsActiveModelKey()
    {
        FakeProviderRegistration reg = new();
        LlamaCppOptions options = new() { ModelId = "owner/repo-GGUF", GpuLayers = 32 };

        reg.UseLlamaCpp(options);

        string? activeModel = reg.Configuration["activeModel"];
        Assert.Equal("llamacpp/owner/repo-GGUF", activeModel);
    }

    // ── (c) full-options overload default is overridable ─────────────────────

    [Fact]
    public void UseLlamaCpp_WithOptions_DefaultIsOverridable()
    {
        FakeProviderRegistration reg = new();
        LlamaCppOptions options = new() { ModelId = "owner/repo-GGUF" };

        reg.UseLlamaCpp(options);
        reg.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>("activeModel", "anthropic/claude-sonnet-4-6"),
        ]);

        string? activeModel = reg.Configuration["activeModel"];
        Assert.Equal("anthropic/claude-sonnet-4-6", activeModel);
    }
}
