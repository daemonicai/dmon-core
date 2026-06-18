using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Core.Auth;
using Dmon.Core.Providers;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Providers;

/// <summary>
/// Unit tests for <see cref="ProviderConfigComposer"/> (4.1) and the shared-list
/// contract between <see cref="ProviderRegistry"/> and <see cref="CredentialResolver"/> (4.2).
/// </summary>
public sealed class ProviderConfigComposerTests
{
    // ---------------------------------------------------------------------------
    // 4.1 — ProviderConfigComposer unit tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void Compose_FactoryWithNoMatchingConfig_SynthesizesDefault()
    {
        StubProviderFactory factory = new("gemini", "gemini-2.0-flash", "GEMINI_API_KEY");

        IReadOnlyList<ProviderConfig> result = ProviderConfigComposer.Compose([], [factory]);

        Assert.Single(result);
        ProviderConfig synth = result[0];
        Assert.Equal("gemini", synth.Name);
        Assert.Equal("gemini", synth.Adapter);
        Assert.Equal("gemini-2.0-flash", synth.DefaultModelId);
        Assert.Equal("envVar", synth.Auth.Type);
        Assert.Equal("GEMINI_API_KEY", synth.Auth.EnvVar);
        Assert.Null(synth.BaseUrl);
    }

    [Fact]
    public void Compose_FactoryAdapterMatchesConfigAdapter_NoSynthesizedDefault()
    {
        ProviderConfig existing = new()
        {
            Name = "my-gemini",
            Adapter = "gemini",
            DefaultModelId = "gemini-1.5-pro",
            Auth = new ProviderAuthConfig { Type = "envVar", EnvVar = "MY_GEMINI_KEY" }
        };
        StubProviderFactory factory = new("gemini", "gemini-2.0-flash", "GEMINI_API_KEY");

        IReadOnlyList<ProviderConfig> result = ProviderConfigComposer.Compose([existing], [factory]);

        // Config entry is present; no synthesized default should be appended.
        Assert.Single(result);
        Assert.Equal("my-gemini", result[0].Name);
        Assert.Equal("MY_GEMINI_KEY", result[0].Auth.EnvVar);
    }

    [Fact]
    public void Compose_FactoryAdapterMatchesConfigAdapter_CaseInsensitive()
    {
        ProviderConfig existing = new()
        {
            Name = "my-gemini",
            Adapter = "GEMINI",          // upper-cased in config
            DefaultModelId = "gemini-1.5-pro",
            Auth = new ProviderAuthConfig { Type = "envVar", EnvVar = "MY_GEMINI_KEY" }
        };
        StubProviderFactory factory = new("gemini", "gemini-2.0-flash", "GEMINI_API_KEY");  // lower-cased factory

        IReadOnlyList<ProviderConfig> result = ProviderConfigComposer.Compose([existing], [factory]);

        Assert.Single(result);
        Assert.Equal("my-gemini", result[0].Name);
    }

    [Fact]
    public void Compose_Ordering_ConfigEntriesFirstThenSynthesized()
    {
        ProviderConfig configA = new()
        {
            Name = "a",
            Adapter = "anthropic",
            DefaultModelId = "claude-3",
            Auth = new ProviderAuthConfig { Type = "envVar", EnvVar = "ANTHROPIC_API_KEY" }
        };
        ProviderConfig configB = new()
        {
            Name = "b",
            Adapter = "openai",
            DefaultModelId = "gpt-4o",
            Auth = new ProviderAuthConfig { Type = "envVar", EnvVar = "OPENAI_API_KEY" }
        };
        StubProviderFactory factoryGemini = new("gemini", "gemini-2.0-flash", "GEMINI_API_KEY");
        StubProviderFactory factoryOllama = new("ollama", "llama3", "");

        IReadOnlyList<ProviderConfig> result =
            ProviderConfigComposer.Compose([configA, configB], [factoryGemini, factoryOllama]);

        Assert.Equal(4, result.Count);
        // Config entries first, original order preserved.
        Assert.Equal("a", result[0].Name);
        Assert.Equal("b", result[1].Name);
        // Synthesized defaults follow in factory enumeration order.
        Assert.Equal("gemini", result[2].Name);
        Assert.Equal("ollama", result[3].Name);
    }

    [Fact]
    public void Compose_KeylessFactory_SynthesizesAuthTypeNone()
    {
        StubProviderFactory factory = new("ollama", "llama3", "");  // empty DefaultEnvVar

        IReadOnlyList<ProviderConfig> result = ProviderConfigComposer.Compose([], [factory]);

        Assert.Single(result);
        Assert.Equal("none", result[0].Auth.Type);
        Assert.Null(result[0].Auth.EnvVar);
    }

    [Fact]
    public void Compose_WhitespaceEnvVar_SynthesizesAuthTypeNone()
    {
        StubProviderFactory factory = new("ollama", "llama3", "   ");  // whitespace DefaultEnvVar

        IReadOnlyList<ProviderConfig> result = ProviderConfigComposer.Compose([], [factory]);

        Assert.Single(result);
        Assert.Equal("none", result[0].Auth.Type);
        Assert.Null(result[0].Auth.EnvVar);
    }

    [Fact]
    public void Compose_NonEmptyEnvVar_SynthesizesAuthTypeEnvVar()
    {
        StubProviderFactory factory = new("anthropic", "claude-sonnet-4-6", "ANTHROPIC_API_KEY");

        IReadOnlyList<ProviderConfig> result = ProviderConfigComposer.Compose([], [factory]);

        Assert.Single(result);
        Assert.Equal("envVar", result[0].Auth.Type);
        Assert.Equal("ANTHROPIC_API_KEY", result[0].Auth.EnvVar);
    }

    [Fact]
    public void Compose_TwoFactoriesSameAdapterName_OnlyOneSynthesizedEntry()
    {
        StubProviderFactory factory1 = new("openai", "gpt-4o", "OPENAI_API_KEY");
        StubProviderFactory factory2 = new("openai", "gpt-4o-mini", "OPENAI_API_KEY_ALT");

        IReadOnlyList<ProviderConfig> result = ProviderConfigComposer.Compose([], [factory1, factory2]);

        // First wins — only one synthesized entry with factory1's DefaultModelId.
        Assert.Single(result);
        Assert.Equal("gpt-4o", result[0].DefaultModelId);
    }

    [Fact]
    public void Compose_TwoFactoriesSameAdapterNameCaseDifferent_OnlyOneSynthesizedEntry()
    {
        StubProviderFactory factory1 = new("OpenAI", "gpt-4o", "OPENAI_API_KEY");
        StubProviderFactory factory2 = new("openai", "gpt-4o-mini", "OPENAI_API_KEY_ALT");

        IReadOnlyList<ProviderConfig> result = ProviderConfigComposer.Compose([], [factory1, factory2]);

        Assert.Single(result);
        Assert.Equal("gpt-4o", result[0].DefaultModelId);
    }

    // ---------------------------------------------------------------------------
    // 4.2 — Registry/resolver shared-list contract
    // ---------------------------------------------------------------------------

    [Fact]
    public void GetAll_SynthesizedProvider_IsListedByRegistry()
    {
        StubProviderFactory factory = new("gemini", "gemini-2.0-flash", "GEMINI_API_KEY");
        IReadOnlyList<ProviderConfig> composed = ProviderConfigComposer.Compose([], [factory]);

        ProviderRegistry registry = new(
            composed,
            [factory],
            new NullCredentialFileStoreResolver(),
            new NullActiveModelStore(),
            NullLogger<ProviderRegistry>.Instance);

        IReadOnlyList<ProviderConfig> all = registry.GetAll();

        Assert.Single(all);
        Assert.Equal("gemini", all[0].Name);
        Assert.Equal("gemini", all[0].Adapter);
    }

    [Fact]
    public async Task ResolveAsync_SynthesizedProvider_ReturnsEnvVarValue()
    {
        const string envVarName = "DMON_TEST_GEMINI_KEY_4F2A";
        const string fakeKey = "fake-gemini-key-xyz";

        StubProviderFactory factory = new("gemini", "gemini-2.0-flash", envVarName);
        IReadOnlyList<ProviderConfig> composed = ProviderConfigComposer.Compose([], [factory]);

        CredentialResolver resolver = new(composed, new NullCredentialFileStore());

        Environment.SetEnvironmentVariable(envVarName, fakeKey);
        try
        {
            string? resolved = await resolver.ResolveAsync("gemini");

            Assert.Equal(fakeKey, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    [Fact]
    public async Task ResolveAsync_SynthesizedProvider_EnvVarUnset_ReturnsNull()
    {
        const string envVarName = "DMON_TEST_GEMINI_KEY_UNSET_B7C1";

        StubProviderFactory factory = new("gemini", "gemini-2.0-flash", envVarName);
        IReadOnlyList<ProviderConfig> composed = ProviderConfigComposer.Compose([], [factory]);

        CredentialResolver resolver = new(composed, new NullCredentialFileStore());

        Environment.SetEnvironmentVariable(envVarName, null);
        try
        {
            string? resolved = await resolver.ResolveAsync("gemini");

            Assert.Null(resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    [Fact]
    public async Task RegistryAndResolver_SharedComposedList_BothSeeProvider()
    {
        const string envVarName = "DMON_TEST_ANTHROPIC_KEY_9E3B";
        const string fakeKey = "fake-anthropic-key-abc";

        StubProviderFactory factory = new("anthropic", "claude-sonnet-4-6", envVarName);
        IReadOnlyList<ProviderConfig> composed = ProviderConfigComposer.Compose([], [factory]);

        ProviderRegistry registry = new(
            composed,
            [factory],
            new NullCredentialFileStoreResolver(),
            new NullActiveModelStore(),
            NullLogger<ProviderRegistry>.Instance);

        CredentialResolver credResolver = new(composed, new NullCredentialFileStore());

        // Registry side: synthesized entry is visible.
        IReadOnlyList<ProviderConfig> all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal("anthropic", all[0].Name);

        // Resolver side: same composed list — resolver can find and return the credential.
        Environment.SetEnvironmentVariable(envVarName, fakeKey);
        try
        {
            string? resolved = await credResolver.ResolveAsync("anthropic");
            Assert.Equal(fakeKey, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    // ---------------------------------------------------------------------------
    // Test doubles
    // ---------------------------------------------------------------------------

    private sealed class StubProviderFactory(
        string adapterName,
        string defaultModelId,
        string defaultEnvVar) : IProviderFactory
    {
        public string AdapterName => adapterName;
        public string DisplayName => adapterName;
        public string DefaultModelId => defaultModelId;
        public string DefaultEnvVar => defaultEnvVar;

        public ChatClientCapabilities GetCapabilities(string modelId) => new();

        public ValueTask<IChatClient> CreateAsync(
            ProviderConfig config,
            string? apiKey,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IChatClient>(new StubChatClient());

        public ValueTask<WizardStep> GetNextStepAsync(
            WizardState state,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Wizard not needed in composer tests.");
    }

    private sealed class StubChatClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public void Dispose() { }
    }

    /// <summary>
    /// Passed to <see cref="ProviderRegistry"/> as the <see cref="ICredentialResolver"/>
    /// when the test only exercises <see cref="ProviderRegistry.GetAll"/>.
    /// </summary>
    private sealed class NullCredentialFileStoreResolver : ICredentialResolver
    {
        public ValueTask<string?> ResolveAsync(string providerName, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }

    private sealed class NullCredentialFileStore : ICredentialFileStore
    {
        public ValueTask<CredentialRecord?> ReadAsync(string providerName, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CredentialRecord?>(null);

        public ValueTask WriteAsync(CredentialRecord record, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string providerName, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class NullActiveModelStore : IActiveModelStore
    {
        public ModelRef? Load() => null;
        public Task SaveAsync(ModelRef selection, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
