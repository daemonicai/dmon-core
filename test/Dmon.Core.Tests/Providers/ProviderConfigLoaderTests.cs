using Dmon.Abstractions.Providers;
using Dmon.Core.Providers;
using Microsoft.Extensions.Configuration;

namespace Dmon.Core.Tests.Providers;

public sealed class ProviderConfigLoaderTests
{
    [Fact]
    public void Load_SingleProvider_ParsesAllFields()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["providers:anthropic:adapter"] = "anthropic",
                ["providers:anthropic:defaultModelId"] = "claude-sonnet-4-6",
                ["providers:anthropic:auth:type"] = "apiKey",
                ["providers:anthropic:auth:envVar"] = "ANTHROPIC_API_KEY"
            })
            .Build();

        ProviderConfigLoader loader = new(config);
        IReadOnlyList<ProviderConfig> providers = loader.Load();

        Assert.Single(providers);
        ProviderConfig p = providers[0];
        Assert.Equal("anthropic", p.Name);
        Assert.Equal("anthropic", p.Adapter);
        Assert.Equal("claude-sonnet-4-6", p.DefaultModelId);
        Assert.Equal("apiKey", p.Auth.Type);
        Assert.Equal("ANTHROPIC_API_KEY", p.Auth.EnvVar);
    }

    [Fact]
    public void Load_MultipleProviders_ReturnsBothInOrder()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["providers:anthropic:adapter"] = "anthropic",
                ["providers:anthropic:auth:type"] = "apiKey",
                ["providers:ollama:adapter"] = "openai",
                ["providers:ollama:baseUrl"] = "http://localhost:11434/v1",
                ["providers:ollama:auth:type"] = "none"
            })
            .Build();

        ProviderConfigLoader loader = new(config);
        IReadOnlyList<ProviderConfig> providers = loader.Load();

        Assert.Equal(2, providers.Count);
    }

    [Fact]
    public void Load_OllamaProvider_ParsesBaseUrl()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["providers:ollama:adapter"] = "openai",
                ["providers:ollama:baseUrl"] = "http://localhost:11434/v1",
                ["providers:ollama:auth:type"] = "none"
            })
            .Build();

        ProviderConfigLoader loader = new(config);
        IReadOnlyList<ProviderConfig> providers = loader.Load();

        Assert.Single(providers);
        Assert.Equal("http://localhost:11434/v1", providers[0].BaseUrl);
        Assert.Equal("none", providers[0].Auth.Type);
        Assert.Null(providers[0].Auth.EnvVar);
    }

    [Fact]
    public void Load_NoProvidersSection_ReturnsEmpty()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        ProviderConfigLoader loader = new(config);
        IReadOnlyList<ProviderConfig> providers = loader.Load();

        Assert.Empty(providers);
    }

    [Fact]
    public void Load_MissingAdapter_Throws()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["providers:broken:auth:type"] = "none"
            })
            .Build();

        ProviderConfigLoader loader = new(config);

        Assert.Throws<InvalidOperationException>(() => loader.Load());
    }

    // 2.1 — Numeric-keyed entries (produced when providers is a YAML sequence) must throw.
    [Fact]
    public void Load_NumericKeys_ThrowsWithDiagnosticMessage()
    {
        // The .NET configuration binder keys sequence children as "0", "1", …
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["providers:0:adapter"] = "anthropic",
                ["providers:0:auth:type"] = "apiKey",
                ["providers:1:adapter"] = "openai",
                ["providers:1:auth:type"] = "none"
            })
            .Build();

        ProviderConfigLoader loader = new(config);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => loader.Load());
        Assert.Contains("0", ex.Message);
        Assert.Contains("map", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // 2.2 — Empty provider key must throw.
    // The in-memory provider represents an empty segment via a key like "providers::adapter"
    // (the segment between the two colons is the empty string), which yields a child with Key == "".
    [Fact]
    public void Load_EmptyKey_Throws()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["providers::adapter"] = "anthropic"
            })
            .Build();

        ProviderConfigLoader loader = new(config);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => loader.Load());
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // 2.3 — A valid map-keyed provider with no auth block defaults to type "none".
    [Fact]
    public void Load_NoAuthBlock_DefaultsToNone()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["providers:ollama:adapter"] = "openai"
            })
            .Build();

        ProviderConfigLoader loader = new(config);
        IReadOnlyList<ProviderConfig> providers = loader.Load();

        Assert.Single(providers);
        Assert.Equal("none", providers[0].Auth.Type);
        Assert.Null(providers[0].Auth.EnvVar);
    }
}
