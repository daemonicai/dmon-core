using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Core.Providers;
using Dmon.Hosting;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Core.Tests.Hosting;

/// <summary>
/// Tests for Group-5: sub-agent provider isolation and <see cref="IChatClientFactory"/>.
/// Tasks 5.1 – 5.4.
/// </summary>
public sealed class SubAgentProviderIsolationTests
{
    // ── 5.1 — isolated IProviderRegistration ─────────────────────────────────

    /// <summary>
    /// The isolated registration exposes its own IServiceCollection and
    /// IConfigurationManager — both must be non-null and functional.
    /// </summary>
    [Fact]
    public void SubAgentProviderRegistration_ExposesOwnServicesAndConfiguration()
    {
        IChatClientFactory factory = SubAgent.BuildClient(p =>
        {
            p.Services.AddSingleton<IProviderFactory>(new StubProviderFactory("test", "test-model", "TEST_API_KEY"));
            p.Configuration.AddInMemoryCollection([
                new KeyValuePair<string, string?>("activeModel", "test/test-model"),
            ]);
        });

        Assert.NotNull(factory);
    }

    // ── 5.4a — isolation from IProviderRegistry ───────────────────────────────

    /// <summary>
    /// A sub-agent registration must never touch <c>IProviderRegistry</c>.
    /// The host registry is not even reachable through the sub-agent path —
    /// it receives a fresh ServiceCollection with no IProviderRegistry registered.
    /// </summary>
    [Fact]
    public void SubAgentRegistration_DoesNotTouchIProviderRegistry()
    {
        // Build host with OpenAI as the root provider.
        DmonBuiltHost host = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry()
            .UseOpenAI("gpt-4o")
            .Build();

        IActiveModelStore rootStore = host.Services.GetRequiredService<IActiveModelStore>();
        ModelRef? rootBefore = rootStore.Load();

        // Build a sub-agent using a completely different stub provider — Gemini-shaped.
        IChatClientFactory subAgentFactory = SubAgent.BuildClient(p =>
        {
            p.Services.AddSingleton<IProviderFactory>(new StubProviderFactory("gemini-stub", "gemini-flash", "GEMINI_API_KEY_STUB"));
            p.Configuration.AddInMemoryCollection([
                new KeyValuePair<string, string?>("activeModel", "gemini-stub/gemini-flash"),
            ]);
        });

        // Configuring the sub-agent must not change the root host's active model.
        ModelRef? rootAfter = rootStore.Load();
        Assert.Equal(rootBefore?.Provider, rootAfter?.Provider);
        Assert.Equal(rootBefore?.Model, rootAfter?.Model);

        // The sub-agent factory is distinct — it does not surface IProviderRegistry.
        Assert.IsType<SubAgentChatClientFactory>(subAgentFactory);
    }

    // ── 5.3 / 5.4b — build-time structural failure: empty action ─────────────

    [Fact]
    public void SubAgent_BuildClient_EmptyAction_ThrowsImmediately()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SubAgent.BuildClient(_ => { }));

        // Message names the problem (no provider registered).
        Assert.Contains("provider", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 5.3 / 5.4b — build-time structural failure: multiple providers ────────

    [Fact]
    public void SubAgent_BuildClient_MultipleProviders_ThrowsImmediately()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            SubAgent.BuildClient(p =>
            {
                p.Services.AddSingleton<IProviderFactory>(new StubProviderFactory("a", "m-a", "KEY_A"));
                p.Services.AddSingleton<IProviderFactory>(new StubProviderFactory("b", "m-b", "KEY_B"));
                p.Configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("activeModel", "a/m-a")]);
            }));

        Assert.Contains("one", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 5.3 / 5.4b — build-time structural failure: provider but no model ─────

    [Fact]
    public void SubAgent_BuildClient_ProviderWithoutModel_ThrowsImmediately()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            SubAgent.BuildClient(p =>
            {
                p.Services.AddSingleton<IProviderFactory>(new StubProviderFactory("test", "test-model", "TEST_KEY"));
                // No UseModel / no activeModel in config.
            }));

        Assert.Contains("model", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 5.3 / 5.4c — lazy missing-key InvalidOperationException ──────────────

    [Fact]
    public async Task SubAgent_CreateAsync_MissingEnvVar_ThrowsNamingEnvVar()
    {
        const string expectedEnvVar = "DMON_TEST_MISSING_KEY_XYZ_UNIQUE";

        // Ensure the env var is not set.
        Environment.SetEnvironmentVariable(expectedEnvVar, null);

        IChatClientFactory factory = SubAgent.BuildClient(p =>
        {
            p.Services.AddSingleton<IProviderFactory>(
                new StubProviderFactory("stub", "stub-model", expectedEnvVar));
            p.Configuration.AddInMemoryCollection([
                new KeyValuePair<string, string?>("activeModel", "stub/stub-model"),
            ]);
        });

        // BuildClient must succeed (structural validation passes).
        Assert.NotNull(factory);

        // CreateAsync must fail, naming the missing env var.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await factory.CreateAsync(CancellationToken.None));

        Assert.Contains(expectedEnvVar, ex.Message, StringComparison.Ordinal);
    }

    // ── 5.3 / 5.4d — memoization: two CreateAsync calls return the same client ─

    [Fact]
    public async Task SubAgent_CreateAsync_ReturnsMemoizedClient()
    {
        const string envVar = "DMON_TEST_MEMO_KEY_XYZ";
        Environment.SetEnvironmentVariable(envVar, "fake-api-key-for-memo-test");
        try
        {
            StubProviderFactory stub = new("stub", "stub-model", envVar);

            IChatClientFactory factory = SubAgent.BuildClient(p =>
            {
                p.Services.AddSingleton<IProviderFactory>(stub);
                p.Configuration.AddInMemoryCollection([
                    new KeyValuePair<string, string?>("activeModel", "stub/stub-model"),
                ]);
            });

            IChatClient first = await factory.CreateAsync(CancellationToken.None);
            IChatClient second = await factory.CreateAsync(CancellationToken.None);

            // Same instance — memoized.
            Assert.Same(first, second);
            // CreateAsync on the factory was called exactly once.
            Assert.Equal(1, stub.CreateAsyncCallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    // ── 5.4e — different provider than root composes ──────────────────────────

    /// <summary>
    /// Root uses Anthropic; sub-agent uses a Gemini-shaped stub.
    /// Both must build without conflict.
    /// </summary>
    [Fact]
    public async Task SubAgent_DifferentProviderThanRoot_ComposesSuccessfully()
    {
        const string geminiEnvVar = "DMON_TEST_GEMINI_STUB_KEY_XYZ";
        Environment.SetEnvironmentVariable(geminiEnvVar, "fake-gemini-key");
        try
        {
            // Root uses Anthropic (no key needed at build time).
            DmonBuiltHost host = DmonHost.CreateBuilder()
                .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
                .WithoutTelemetry()
                .UseAnthropic("claude-opus-4")
                .Build();

            // Sub-agent uses Gemini-shaped stub.
            StubProviderFactory geminiStub = new("gemini-stub", "flash", geminiEnvVar);
            IChatClientFactory subAgentFactory = SubAgent.BuildClient(p =>
            {
                p.Services.AddSingleton<IProviderFactory>(geminiStub);
                p.Configuration.AddInMemoryCollection([
                    new KeyValuePair<string, string?>("activeModel", "gemini-stub/flash"),
                ]);
            });

            // Both composition roots must not interfere.
            IActiveModelStore rootStore = host.Services.GetRequiredService<IActiveModelStore>();
            ModelRef? rootModel = rootStore.Load();
            Assert.Equal("anthropic", rootModel?.Provider);

            // Sub-agent client must construct from its own factory.
            IChatClient subClient = await subAgentFactory.CreateAsync(CancellationToken.None);
            Assert.NotNull(subClient);
            Assert.Equal(1, geminiStub.CreateAsyncCallCount);

            // Root's active model must be unchanged.
            ModelRef? rootModelAfter = rootStore.Load();
            Assert.Equal("anthropic", rootModelAfter?.Provider);
            Assert.Equal("claude-opus-4", rootModelAfter?.Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable(geminiEnvVar, null);
        }
    }

    // ── 5.1 — IProviderExtension path resolves via CreateFactory() ────────────

    [Fact]
    public async Task SubAgent_WithProviderExtension_UsesCreateFactory()
    {
        const string envVar = "DMON_TEST_EXT_KEY_XYZ";
        Environment.SetEnvironmentVariable(envVar, "fake-ext-key");
        try
        {
            StubProviderFactory innerFactory = new("ext-provider", "ext-model", envVar);
            StubProviderExtension extension = new(innerFactory);

            IChatClientFactory factory = SubAgent.BuildClient(p =>
            {
                // Register via IProviderExtension (local-provider path).
                p.Services.AddSingleton<IProviderExtension>(extension);
                p.Configuration.AddInMemoryCollection([
                    new KeyValuePair<string, string?>("activeModel", "ext-provider/ext-model"),
                ]);
            });

            IChatClient client = await factory.CreateAsync(CancellationToken.None);
            Assert.NotNull(client);
            Assert.True(extension.CreateFactoryCalled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────

/// <summary>
/// A no-network stub <see cref="IProviderFactory"/> that returns a minimal
/// <see cref="IChatClient"/> without making any HTTP calls.
/// </summary>
file sealed class StubProviderFactory(string adapterName, string defaultModelId, string defaultEnvVar) : IProviderFactory
{
    public string AdapterName => adapterName;
    public string DisplayName => adapterName;
    public string DefaultModelId => defaultModelId;
    public string DefaultEnvVar => defaultEnvVar;
    public int CreateAsyncCallCount { get; private set; }

    public ChatClientCapabilities GetCapabilities(string modelId)
        => new() { SupportsToolCalling = false };

    public ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
    {
        CreateAsyncCallCount++;
        return ValueTask.FromResult<IChatClient>(new StubChatClient());
    }

    public ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string? apiKey, string? baseUrl = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<ModelInfo>>([]);

    public ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<WizardStep>(new WizardCompletedStep { Id = "done", Prompt = "", Message = "" });
}

/// <summary>
/// A stub <see cref="IProviderExtension"/> backed by a <see cref="StubProviderFactory"/>.
/// Tracks whether <see cref="CreateFactory"/> was called.
/// </summary>
file sealed class StubProviderExtension(StubProviderFactory innerFactory) : IProviderExtension
{
    public string ProviderName => innerFactory.AdapterName;
    public bool CreateFactoryCalled { get; private set; }

    public bool IsApplicable() => true;
    public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task EnsureRunningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

    public IProviderFactory CreateFactory()
    {
        CreateFactoryCalled = true;
        return innerFactory;
    }
}

/// <summary>
/// Minimal <see cref="IChatClient"/> stub — no-op, no network.
/// </summary>
file sealed class StubChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "stub")]));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => AsyncEnumerable.Empty<ChatResponseUpdate>();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
